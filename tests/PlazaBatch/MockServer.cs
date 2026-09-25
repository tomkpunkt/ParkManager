using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using ParkManager.Geometry;
using Unity.Mathematics;

internal static class MockServer
{
    private sealed class PointDto { public float X { get; set; } public float Y { get; set; } }
    private sealed class ArrangementDto
    {
        public int Kind { get; set; }
        public string Name { get; set; } = "";
    }
    private sealed class PlanRequest
    {
        public PointDto[] Polygon { get; set; } = Array.Empty<PointDto>();
        public PointDto[] Entrances { get; set; } = Array.Empty<PointDto>();
        public int SiteType { get; set; }
        public int Seed { get; set; } = 1;
        public int PathType { get; set; } = 1;
        public int PlazaCenterPlacement { get; set; }
        public int PlazaArrangementPlacement { get; set; }
        public float PlazaCenterpieceSpacing { get; set; } = 20f;
        public float PlazaArrangementSpacing { get; set; } = 4f;
        public bool PlazaFenceEnabled { get; set; }
        public bool IncludeCenterpiece { get; set; } = true;
        public float CenterRadius { get; set; } = 2.5f;
        public int VegetationDensity { get; set; } = 100;
        public int FurnitureDensity { get; set; } = 100;
        public int EnabledMask { get; set; } = 0x2f;
        public ArrangementDto[] Arrangement { get; set; } = Array.Empty<ArrangementDto>();
    }

    internal static async Task Run(int port)
    {
        var root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "UI", "mock"));
        if (!File.Exists(Path.Combine(root, "index.html")))
            throw new DirectoryNotFoundException($"Start from the repository root: {root}");
        var app = WebApplication.Create();
        app.Urls.Add($"http://localhost:{port}");
        var files = new PhysicalFileProvider(root);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapPost("/api/plan", async (HttpRequest request) =>
        {
            var input = await JsonSerializer.DeserializeAsync<PlanRequest>(
                request.Body, new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true });
            return Results.Json(Plan(input ?? new PlanRequest()));
        });
        Console.WriteLine($"ParkManager live mock: http://localhost:{port}/");
        await app.RunAsync();
    }

    private static object Plan(PlanRequest request)
    {
        var polygon = request.Polygon.Select(p => new float2(p.X, p.Y)).ToList();
        var gates = request.Entrances.Select(p => new float2(p.X, p.Y)).ToList();
        if (polygon.Count < 3 || gates.Count < 1)
            return new { error = "At least three polygon points and one entrance are required." };
        var seed = Math.Max(1, request.Seed);
        if (request.SiteType == 1)
        {
            var arrangement = request.Arrangement.Take(5).Select(item =>
                new PlazaArrangementItem {
                    Kind = (PlazaFurnitureKind)Math.Clamp(item.Kind, 0, 4),
                    AssetName = item.Name,
                    FootprintRadius = item.Kind switch {
                        0 => 0.8f, 1 => 0.4f, 2 => 0.35f,
                        3 => 1.6f, _ => 0.65f },
                    Size = item.Kind switch { 0 => 1.6f, 3 => 3.2f, _ => 0.8f },
                }).ToList();
            var plan = PlazaPlanner.Generate(polygon, gates,
                Math.Clamp(request.CenterRadius, 0.5f, 20f),
                (PlazaCenterPlacementMode)Math.Clamp(
                    request.PlazaCenterPlacement, 0, 2),
                (PlazaArrangementPlacementMode)Math.Clamp(
                    request.PlazaArrangementPlacement, 0, 1),
                Math.Clamp(request.PlazaCenterpieceSpacing, 5f, 60f),
                Math.Clamp(request.PlazaArrangementSpacing, 0f, 20f),
                Math.Clamp(request.FurnitureDensity, 25, 200), seed,
                request.IncludeCenterpiece, arrangement);
            object[] fences = Array.Empty<object>();
            if (request.PlazaFenceEnabled)
            {
                var fenceBit = 1 << ((int)ParkDecorationKind.Fence - 1);
                var fencePlan = ParkDecorationPlanner.Generate(polygon,
                    ParkPathPlan.Empty(seed), gates, seed, 0f, true, 25, 25,
                    fenceBit);
                fences = fencePlan.Placements
                    .Where(item => item.Kind == ParkDecorationKind.Fence)
                    .Select(item =>
                    {
                        var tangent = new float2(math.sin(item.Rotation),
                            math.cos(item.Rotation)) * (item.Size * 0.5f);
                        return (object)new
                        {
                            ax = item.Position.x - tangent.x,
                            ay = item.Position.y - tangent.y,
                            bx = item.Position.x + tangent.x,
                            by = item.Position.y + tangent.y,
                        };
                    }).ToArray();
            }
            return new {
                seed, siteType = 1, centerFits = PlazaPlanner.CanFitCenterpiece(
                    polygon, request.CenterRadius),
                paths = Array.Empty<object>(), fences,
                centers = plan.Centerpieces.Select(x => new { x = x.Position.x,
                    y = x.Position.y, radius = x.Radius }).ToArray(),
                furniture = plan.Furniture.Select(x => new { x = x.Position.x,
                    y = x.Position.y, radius = x.FootprintRadius,
                    kind = x.Kind.ToString(), asset = x.AssetName }).ToArray(),
            };
        }
        var centroid = new float2(polygon.Average(p => p.x), polygon.Average(p => p.y));
        var paths = ParkPathPlanner.Generate(polygon, gates, centroid, seed);
        var decorations = ParkDecorationPlanner.Generate(polygon, paths, gates,
            seed, request.PathType == 0 ? 2f : 4f,
            (request.EnabledMask & (1 << 4)) != 0,
            request.VegetationDensity, request.FurnitureDensity,
            request.EnabledMask);
        return new {
            seed, siteType = 0, centerFits = true,
            fences = Array.Empty<object>(),
            paths = paths.Edges.Select(edge => new {
                ax = paths.Nodes[edge.A].Position.x,
                ay = paths.Nodes[edge.A].Position.y,
                bx = paths.Nodes[edge.B].Position.x,
                by = paths.Nodes[edge.B].Position.y,
                kind = edge.Kind.ToString(), hidden = false }).ToArray(),
            centers = Array.Empty<object>(),
            furniture = decorations.Placements.Select(x => new {
                x = x.Position.x, y = x.Position.y,
                radius = x.Kind == ParkDecorationKind.Tree ? 1.7f
                    : x.Kind == ParkDecorationKind.Bush ? 0.7f : 0.5f,
                kind = x.Kind.ToString(), asset = x.ExplicitAssetName ?? "" }).ToArray(),
        };
    }
}
