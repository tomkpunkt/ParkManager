using System.Collections.Generic;
using System.Linq;
using ParkManager.Geometry;
using Unity.Mathematics;

/// <summary>JSON shape of a <see cref="PlazaDesign"/> for the mock and the gallery.</summary>
internal static class PlazaDesignJson
{
    internal static object Describe(PlazaDesign design, bool includeTiming = true) => new
    {
        requested = design.RequestedStyle.ToString(),
        style = design.Style.ToString(),
        coreShape = design.CoreShape.ToString(),
        failure = design.Failure,
        mainPlaza = Points(design.MainPlaza),
        secondarySpaces = design.SecondarySpaces.Select(Points).ToArray(),
        greenZones = design.GreenZones.Select(Points).ToArray(),
        corridors = design.MovementCorridors.Select(c => new
        {
            points = Points(c.Points), width = c.Width, entrance = c.Entrance,
        }).ToArray(),
        axes = design.CompositionAxes.Select(a => new
        {
            ax = a.Origin.x - a.Direction.x * a.HalfLength,
            ay = a.Origin.y - a.Direction.y * a.HalfLength,
            bx = a.Origin.x + a.Direction.x * a.HalfLength,
            by = a.Origin.y + a.Direction.y * a.HalfLength,
            primary = a.Primary,
        }).ToArray(),
        focalPoints = design.FocalPoints.Select(f => new
        {
            x = f.Position.x, y = f.Position.y, kind = f.Kind.ToString(),
            clearance = f.Clearance,
        }).ToArray(),
        treeRows = design.TreeRows.Select(r => new
        {
            points = Enumerable.Range(0, r.Count).Select(i =>
            {
                var p = math.lerp(r.Start, r.End, r.Count > 1 ? i / (r.Count - 1f) : 0f);
                return (object)new { x = p.x, y = p.y };
            }).ToArray(),
            spacing = r.Spacing,
        }).ToArray(),
        furnitureZones = design.FurnitureZones.Select(z => new
        {
            kind = z.Kind.ToString(), polygon = Points(z.Polygon),
            facing = new { x = z.Facing.x, y = z.Facing.y },
        }).ToArray(),
        metrics = new
        {
            polygonArea = design.Metrics.PolygonArea,
            mainShare = design.Metrics.PolygonArea > 0f
                ? design.Metrics.MainPlazaArea / design.Metrics.PolygonArea : 0f,
            greenShare = design.Metrics.PolygonArea > 0f
                ? design.Metrics.GreenArea / design.Metrics.PolygonArea : 0f,
            minimumWidth = design.Metrics.MainPlazaMinimumWidth,
            coreScore = design.Metrics.CoreScore,
            compactness = design.Metrics.Compactness,
            convexity = design.Metrics.Convexity,
            elongation = design.Metrics.Elongation,
            cellSize = design.Metrics.CellSize,
            suitability = design.Metrics.StyleSuitability.OrderBy(p => p.Key)
                .ToDictionary(p => p.Key.ToString(), p => p.Value),
            milliseconds = includeTiming ? design.Metrics.Milliseconds : 0.0,
        },
    };

    private static object[] Points(IEnumerable<float2> points)
        => points.Select(p => (object)new { x = p.x, y = p.y }).ToArray();
}
