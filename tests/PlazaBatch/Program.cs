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
    var mode = (PlazaLayoutMode)(index % 4);
    var width = 36f + index % 13 * 4f;
    var height = 30f + index % 9 * 5f;
    var polygon = new List<float2> {
        new(0, 0), new(width, 0), new(width, height), new(0, height) };
    var gates = new List<float2> { new(width / 2, 0) };
    var radius = index % 7 == 0 ? 5.5f : 2.5f;
    var withCenter = mode != PlazaLayoutMode.Open;
    var arrangement = new List<PlazaArrangementItem> {
        new() { Kind = PlazaFurnitureKind.Bench, AssetName = "MockBench",
            FootprintRadius = 0.8f, Size = 1.6f },
        new() { Kind = PlazaFurnitureKind.TrashBin, AssetName = "MockBin",
            FootprintRadius = 0.35f, Size = 0.7f },
        new() { Kind = PlazaFurnitureKind.Bench, AssetName = "MockBench",
            FootprintRadius = 0.8f, Size = 1.6f },
    };
    var plan = PlazaPlanner.Generate(polygon, gates, radius, mode, seed,
        withCenter, arrangement);
    var errors = new List<string>();
    if (plan.RoutingSegments.Count == 0) errors.Add("No routing");
    if (plan.Furniture.Count > 64) errors.Add("Furniture limit exceeded");
    if (withCenter && PlazaPlanner.CanFitCenterpiece(polygon, radius)
        && plan.Centerpieces.Count == 0) errors.Add("Missing fitting center");
    foreach (var item in plan.Furniture)
    {
        if (!math.all(math.isfinite(item.Position)) || !math.isfinite(item.Rotation))
            errors.Add("Non-finite furniture transform");
        if (item.Position.x < item.FootprintRadius - 0.001f
            || item.Position.x > width - item.FootprintRadius + 0.001f
            || item.Position.y < item.FootprintRadius - 0.001f
            || item.Position.y > height - item.FootprintRadius + 0.001f)
            errors.Add("Furniture footprint outside bounds");
        if (plan.Centerpieces.Any(center =>
            math.distance(center.Position, item.Position)
            < center.Radius + item.FootprintRadius - 0.001f))
            errors.Add("Furniture overlaps center");
    }
    foreach (var center in plan.Centerpieces)
        if (center.Position.x < center.Radius || center.Position.x > width - center.Radius
            || center.Position.y < center.Radius || center.Position.y > height - center.Radius)
            errors.Add("Centerpiece footprint outside bounds");
    foreach (var route in plan.RoutingSegments)
        if (!Inside(route.A) || !Inside(route.B)) errors.Add("Routing endpoint outside bounds");
    var replay = PlazaPlanner.Generate(polygon, gates, radius, mode, seed,
        withCenter, arrangement);
    if (JsonSerializer.Serialize(Describe(plan)) != JsonSerializer.Serialize(Describe(replay)))
        errors.Add("Non-deterministic replay");
    if (errors.Count > 0) failures.Add($"Case {index}: {string.Join(", ", errors.Distinct())}");
    cases.Add(new { index, seed, mode = mode.ToString(), width, height, radius,
        errors = errors.Distinct().ToArray(), plan = Describe(plan) });
    bool Inside(float2 p) => p.x >= -0.001f && p.x <= width + 0.001f
        && p.y >= -0.001f && p.y <= height + 0.001f;
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
    routes = plan.RoutingSegments.Select(x => new { ax = x.A.x, ay = x.A.y,
        bx = x.B.x, by = x.B.y }).ToArray()
};
