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
        topology = design.Topology.ToString(),
        grammar = design.Grammar.ToString(),
        coreShape = design.CoreShape.ToString(),
        failure = design.Failure,
        mainPlaza = Points(design.MainPlaza),
        rooms = design.Rooms.Select(room => new
        {
            role = room.Role.ToString(), importance = room.Importance,
            shape = room.Shape.Kind.ToString(),
            center = new { x = room.Center.x, y = room.Center.y },
            boundary = Points(room.Boundary),
            connected = room.ConnectedRooms.ToArray(),
            edges = room.Edges.Select(edge => new
            {
                role = edge.Role.ToString(), rim = edge.Rim, arrival = edge.Arrival,
                points = Points(edge.Points),
            }).ToArray(),
        }).ToArray(),
        greenZones = design.GreenZones.Select(zone => new
        {
            kind = zone.Kind.ToString(), room = zone.Room, primary = zone.Primary,
            polygon = Points(zone.Polygon),
        }).ToArray(),
        corridors = design.MovementCorridors.Select(c => new
        {
            points = Points(c.Points), width = c.Width, entrance = c.Entrance, room = c.Room,
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
        treeGroups = design.TreeGroups.Select(Points).ToArray(),
        furnitureZones = design.FurnitureZones.Select(z => new
        {
            kind = z.Kind.ToString(), polygon = Points(z.Polygon),
            facing = new { x = z.Facing.x, y = z.Facing.y },
        }).ToArray(),
        metrics = new
        {
            polygonArea = design.Metrics.PolygonArea,
            mainShare = Share(design.Metrics.MainPlazaArea, design.Metrics.PolygonArea),
            roomShare = Share(design.Metrics.RoomArea, design.Metrics.PolygonArea),
            greenShare = Share(design.Metrics.GreenArea, design.Metrics.PolygonArea),
            greenSurfaceRatio = design.Metrics.GreenSurfaceRatio,
            canopyCoverageRatio = design.Metrics.CanopyCoverageRatio,
            minimumWidth = design.Metrics.MainPlazaMinimumWidth,
            compactness = design.Metrics.Compactness,
            convexity = design.Metrics.Convexity,
            elongation = design.Metrics.Elongation,
            cellSize = design.Metrics.CellSize,
            topologySuitability = design.Metrics.TopologySuitability.OrderBy(p => p.Key)
                .ToDictionary(p => p.Key.ToString(), p => p.Value),
            grammarSuitability = design.Metrics.GrammarSuitability.OrderBy(p => p.Key)
                .ToDictionary(p => p.Key.ToString(), p => p.Value),
            scores = new
            {
                hierarchy = design.Metrics.SpatialHierarchy,
                green = design.Metrics.GreenComposition,
                edges = design.Metrics.EdgeDiversity,
                identity = design.Metrics.StyleIdentity,
                entrances = design.Metrics.EntranceIntegration,
            },
            milliseconds = includeTiming ? design.Metrics.Milliseconds : 0.0,
        },
    };

    private static float Share(float part, float whole) => whole > 0f ? part / whole : 0f;

    private static object[] Points(IEnumerable<float2> points)
        => points.Select(p => (object)new { x = p.x, y = p.y }).ToArray();
}
