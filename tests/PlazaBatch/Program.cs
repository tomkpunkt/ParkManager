using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParkManager.Geometry;
using Unity.Mathematics;

if (args.Length > 0 && args[0] == "--serve")
{
    await MockServer.Run(args.Length > 1 && int.TryParse(args[1], out var port)
        ? port : 8766);
    return;
}
var count = args.Length > 0 && int.TryParse(args[0], out var parsed)
    ? Math.Clamp(parsed, 1, 10000) : 100;
var output = args.Length > 1 ? args[1] : "plaza-batch.json";
var cases = new List<object>();
var failures = new List<string>();
for (var index = 0; index < count; index++)
{
    var seed = index + 1;
    var centerPlacement = (PlazaCenterPlacementMode)(index % 3);
    var arrangementPlacement = (PlazaArrangementPlacementMode)(index / 3 % 2);
    var width = 36f + index % 13 * 4f;
    var height = 30f + index % 9 * 5f;
    var polygon = (index % 5) switch {
        0 => new List<float2> { new(0, 0), new(width, 0),
            new(width, height), new(0, height) },
        1 => new List<float2> { new(0, 0), new(width, 0),
            new(width, height * .35f), new(width * .45f, height * .35f),
            new(width * .45f, height), new(0, height) },
        2 => new List<float2> { new(0, 0), new(width, 0),
            new(width, height), new(width * .7f, height),
            new(width * .7f, height * .4f), new(width * .3f, height * .4f),
            new(width * .3f, height), new(0, height) },
        3 => new List<float2> { new(0, 0), new(width, 0),
            new(width * .85f, height), new(width * .15f, height) },
        _ => new List<float2> { new(0, 0), new(width, 0),
            new(width, height * .32f), new(0, height * .32f) },
    };
    var gates = new List<float2> { new(width / 2, 0) };
    var radius = index % 7 == 0 ? 5.5f : 2.5f;
    var withCenter = index % 5 != 4;
    var centerpieceSpacing = 8 + index % 6 * 5;
    var arrangementSpacing = index % 11;
    var density = 50 + index % 7 * 25;
    var arrangement = new List<PlazaArrangementItem> {
        new() { Kind = PlazaFurnitureKind.Bench, AssetName = "MockBench",
            FootprintRadius = 0.8f, Size = 1.6f },
        new() { Kind = PlazaFurnitureKind.TrashBin, AssetName = "MockBin",
            FootprintRadius = 0.35f, Size = 0.7f },
        new() { Kind = PlazaFurnitureKind.Bench, AssetName = "MockBench",
            FootprintRadius = 0.8f, Size = 1.6f },
    };
    var plan = PlazaPlanner.Generate(polygon, gates, radius,
        centerPlacement, arrangementPlacement, centerpieceSpacing,
        arrangementSpacing, density, seed, withCenter, arrangement);
    var errors = new List<string>();
    if (plan.Furniture.Count > 64) errors.Add("Furniture limit exceeded");
    if (withCenter && PlazaPlanner.CanFitCenterpiece(polygon, radius)
        && plan.Centerpieces.Count == 0) errors.Add("Missing fitting center");
    foreach (var item in plan.Furniture)
    {
        if (!math.all(math.isfinite(item.Position)) || !math.isfinite(item.Rotation))
            errors.Add("Non-finite furniture transform");
        if (!Inside(item.Position)
            || BoundaryDistance(item.Position) < item.FootprintRadius - 0.001f)
            errors.Add("Furniture footprint outside bounds");
        if (plan.Centerpieces.Any(center =>
            math.distance(center.Position, item.Position)
            < center.Radius + item.FootprintRadius + 0.75f
                + (arrangementPlacement == PlazaArrangementPlacementMode.AroundCenter
                    ? arrangementSpacing : 0f) - 0.001f))
            errors.Add("Furniture overlaps center");
        var minimumBoundaryGap = item.FootprintRadius
            + (arrangementPlacement == PlazaArrangementPlacementMode.AlongBoundary
                ? arrangementSpacing : 0.65f);
        if (BoundaryDistance(item.Position) < minimumBoundaryGap - 0.001f)
            errors.Add("Furniture violates its boundary spacing");
    }
    foreach (var center in plan.Centerpieces)
        if (!Inside(center.Position)
            || BoundaryDistance(center.Position) < center.Radius - 0.001f)
            errors.Add("Centerpiece footprint outside bounds");
    for (var i = 0; i + 1 < plan.Furniture.Count; i += 2)
    {
        var first = plan.Furniture[i];
        var second = plan.Furniture[i + 1];
        if (first.ArrangementId != second.ArrangementId || first.Kind != second.Kind)
            errors.Add("Arrangement pair was split");
        CheckFacingCenter(first);
        CheckFacingCenter(second);
        if (i >= 2 && math.distance(
            (first.Position + second.Position) * 0.5f,
            (plan.Furniture[0].Position + plan.Furniture[1].Position) * 0.5f) > 0.01f)
            errors.Add("Arrangement pairs are not mirrored around one center");
    }
    var replay = PlazaPlanner.Generate(polygon, gates, radius,
        centerPlacement, arrangementPlacement, centerpieceSpacing,
        arrangementSpacing, density, seed + 1000, withCenter, arrangement);
    if (JsonSerializer.Serialize(Describe(plan)) != JsonSerializer.Serialize(Describe(replay)))
        errors.Add("Non-deterministic replay");
    if (errors.Count > 0) failures.Add($"Case {index}: {string.Join(", ", errors.Distinct())}");
    cases.Add(new { index, seed, centerPlacement = centerPlacement.ToString(),
        arrangementPlacement = arrangementPlacement.ToString(), centerpieceSpacing,
        arrangementSpacing, density, width, height, radius,
        errors = errors.Distinct().ToArray(), plan = Describe(plan) });
    bool Inside(float2 p) {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++) {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if ((a.y > p.y) == (b.y > p.y)) continue;
            if (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }
    float BoundaryDistance(float2 p) {
        var best = float.MaxValue;
        for (var i = 0; i < polygon.Count; i++) {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            var edge = b - a;
            var t = math.clamp(math.dot(p - a, edge)
                / math.max(0.0001f, math.lengthsq(edge)), 0f, 1f);
            best = math.min(best, math.distance(p, a + edge * t));
        }
        return best;
    }
    void CheckFacingCenter(PlazaFurniturePlacement item)
    {
        if (item.Kind == PlazaFurnitureKind.Bush) return;
        var midpoint = (plan.Furniture[0].Position + plan.Furniture[1].Position) * 0.5f;
        var towardCenter = math.normalizesafe(midpoint - item.Position);
        var forward = new float2(math.sin(item.Rotation), math.cos(item.Rotation));
        if (math.dot(forward, towardCenter) < 0.75f)
            errors.Add($"{item.Kind} faces away from its arrangement center");
    }
}
var report = new { count, failed = failures.Count, failures, cases };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{count} Plaza variants; {failures.Count} with findings. {Path.GetFullPath(output)}");
foreach (var failure in failures.Take(20)) Console.WriteLine(failure);
if (failures.Count > 0) Environment.ExitCode = 1;

static object Describe(PlazaPlan plan) => new {
    centers = plan.Centerpieces.Select(x => new { x = x.Position.x,
        y = x.Position.y, x.Radius }).ToArray(),
    furniture = plan.Furniture.Select(x => new { kind = x.Kind.ToString(),
        x = x.Position.x, y = x.Position.y, x.Rotation,
        x.FootprintRadius, x.ArrangementId }).ToArray(),
    routes = Array.Empty<object>()
};
