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
    if (arrangementPlacement == PlazaArrangementPlacementMode.AlongBoundary)
    {
        // The L-shaped case is intentionally asymmetric. Other batch shapes
        // have a mirror axis and must keep each boundary group mirrored.
        var mirrored = index % 5 != 1;
        var expectedGroupSize = arrangement.Count * (mirrored ? 2 : 1);
        foreach (var group in plan.Furniture.GroupBy(item => item.ArrangementId))
        {
            var items = group.ToArray();
            if (items.Length != expectedGroupSize)
                errors.Add("Boundary arrangement has the wrong group size");
            for (var i = 0; i < items.Length; i++)
            {
                if (BoundaryDistance(items[i].Position)
                    > arrangementSpacing + 1.6f)
                    errors.Add("Boundary furniture does not follow an edge");
                CheckFacingBoundary(items[i]);
            }
            if (!mirrored) continue;
            for (var i = 0; i + 1 < items.Length; i += 2)
            {
                var first = items[i];
                var second = items[i + 1];
                var polygonHeight = index % 5 == 4 ? height * .32f : height;
                var reflectedAcrossHorizontal = new float2(first.Position.x,
                    polygonHeight - first.Position.y);
                var reflectedAcrossVertical = new float2(width - first.Position.x,
                    first.Position.y);
                var positionMatches = math.distance(reflectedAcrossHorizontal,
                    second.Position) <= 0.1f || math.distance(
                    reflectedAcrossVertical, second.Position) <= 0.1f;
                if (first.Kind != second.Kind
                    || !positionMatches)
                    errors.Add("Symmetric polygon has an unmirrored boundary pair");
            }
        }
    }
    else
    {
        var groups = plan.Furniture.GroupBy(item => item.ArrangementId)
            .Select(group => group.ToArray()).ToArray();
        if (groups.Length > 1)
        {
            var circleCenter = (plan.Furniture[0].Position
                + plan.Furniture[1].Position) * 0.5f;
            var angles = groups.Select(group => {
                var anchor = float2.zero;
                for (var i = 0; i < group.Length; i += 2)
                    anchor += group[i].Position;
                anchor /= group.Length / 2;
                var delta = anchor - circleCenter;
                var angle = math.atan2(delta.y, delta.x);
                if (angle < 0f) angle += math.PI;
                if (angle >= math.PI) angle -= math.PI;
                return angle;
            }).OrderBy(angle => angle).ToArray();
            var expectedStep = math.PI / groups.Length;
            for (var i = 0; i < angles.Length; i++)
            {
                var next = i + 1 < angles.Length
                    ? angles[i + 1] : angles[0] + math.PI;
                if (math.abs(next - angles[i] - expectedStep) > 0.01f)
                    errors.Add("Circular arrangements are not evenly spaced");
            }
        }
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
    void CheckFacingBoundary(PlazaFurniturePlacement item)
    {
        if (item.Kind == PlazaFurnitureKind.Bush) return;
        var forward = new float2(math.sin(item.Rotation),
            math.cos(item.Rotation));
        var facesAnEdge = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var edge = polygon[(i + 1) % polygon.Count] - a;
            var lengthSquared = math.lengthsq(edge);
            if (lengthSquared < 0.0001f) continue;
            var t = math.clamp(math.dot(item.Position - a, edge)
                / lengthSquared, 0f, 1f);
            var distance = math.distancesq(item.Position, a + edge * t);
            var inward = math.normalizesafe(new float2(-edge.y, edge.x));
            if (math.sqrt(distance) > arrangementSpacing + 1.6f
                || math.dot(forward, inward) < 0.75f) continue;
            facesAnEdge = true;
            break;
        }
        if (!facesAnEdge)
            errors.Add($"{item.Kind} does not face inward from the edge");
    }
}
// Exercise a rotated regular polygon whose mirror axes do not align with X/Y.
var regularPolygon = Enumerable.Range(0, 5).Select(i => {
    var angle = 0.31f + i * 2f * math.PI / 5f;
    return new float2(50f + 40f * math.cos(angle),
        50f + 40f * math.sin(angle));
}).ToList();
var regularArrangement = new List<PlazaArrangementItem> {
    new() { Kind = PlazaFurnitureKind.Bench, AssetName = "MockBench",
        FootprintRadius = 0.8f, Size = 1.6f },
};
var regularPlan = PlazaPlanner.Generate(regularPolygon,
    new List<float2> { regularPolygon[0] }, 2.5f,
    PlazaCenterPlacementMode.Centered,
    PlazaArrangementPlacementMode.AlongBoundary, 20f, 3f, 100, 1,
    true, regularArrangement);
if (regularPlan.Furniture.Count == 0
    || regularPlan.Furniture.GroupBy(item => item.ArrangementId)
        .Any(group => group.Count() != 2))
    failures.Add("Rotated mirror-symmetric polygon lost paired edge furniture");
var openSquare = new List<float2> {
    new(0f, 0f), new(400f, 0f), new(400f, 400f), new(0f, 400f),
};
var expectedCirclePairs = new[] { 1, 2, 3, 4, 6, 8, 10, 12 };
for (var level = 0; level < expectedCirclePairs.Length; level++)
{
    var density = (level + 1) * 25;
    var circlePlan = PlazaPlanner.Generate(openSquare,
        Array.Empty<float2>(), 2.5f,
        PlazaCenterPlacementMode.Centered,
        PlazaArrangementPlacementMode.AroundCenter, 20f, 4f, density, 1,
        true, regularArrangement);
    if (circlePlan.Furniture.Count != expectedCirclePairs[level] * 2)
        failures.Add($"{density}% did not produce its complete circular ring");
}
var fiveItemArrangement = Enumerable.Range(0, 5).Select(_ =>
    new PlazaArrangementItem { Kind = PlazaFurnitureKind.Bench,
        AssetName = "MockBench", FootprintRadius = 0.8f, Size = 1.6f })
    .ToList();
var cappedCircle = PlazaPlanner.Generate(openSquare,
    Array.Empty<float2>(), 2.5f, PlazaCenterPlacementMode.Centered,
    PlazaArrangementPlacementMode.AroundCenter, 20f, 4f, 200, 1,
    true, fiveItemArrangement);
if (cappedCircle.Furniture.Count != 60)
    failures.Add("Furniture cap did not yield six complete circular pairs");
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
