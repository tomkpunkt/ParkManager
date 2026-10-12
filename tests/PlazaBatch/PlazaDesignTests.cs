using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParkManager.Geometry;
using Unity.Mathematics;

/// <summary>
/// Phase-1 checks for the plaza composer: hard spatial rules on a set of
/// characteristic outlines, each with several entrance configurations and
/// every style. Writes a gallery for visual review.
/// </summary>
internal static class PlazaDesignTests
{
    internal sealed record Case(string Name, List<float2> Polygon, List<float2> Entrances);

    internal static List<Case> Cases()
    {
        var cases = new List<Case>();
        void Add(string shape, List<float2> polygon, params (string, float2[])[] configs)
        {
            foreach (var (name, entrances) in configs)
                cases.Add(new Case($"{shape} · {name}", polygon, entrances.ToList()));
        }

        var square = Rect(0, 0, 60, 60);
        Add("Quadrat", square,
            ("1 Eingang", new[] { new float2(30, 0) }),
            ("gegenüber", new[] { new float2(30, 0), new float2(30, 60) }),
            ("Ecken", new[] { new float2(4, 0), new float2(60, 56) }),
            ("4 Seiten", new[] { new float2(30, 0), new float2(60, 30),
                new float2(30, 60), new float2(0, 30) }));
        var elongated = Rect(0, 0, 140, 36);
        Add("Langgestreckt", elongated,
            ("Enden", new[] { new float2(0, 18), new float2(140, 18) }),
            ("Längsseite", new[] { new float2(70, 0) }),
            ("diagonal", new[] { new float2(10, 0), new float2(130, 36) }));
        var lShape = new List<float2> { new(0, 0), new(80, 0), new(80, 32),
            new(32, 32), new(32, 80), new(0, 80) };
        Add("L-Form", lShape,
            ("Arm-Enden", new[] { new float2(80, 16), new float2(16, 80) }),
            ("Ecke", new[] { new float2(0, 40) }),
            ("Innenecke", new[] { new float2(56, 32), new float2(32, 56) }));
        var uShape = new List<float2> { new(0, 0), new(90, 0), new(90, 70),
            new(62, 70), new(62, 30), new(28, 30), new(28, 70), new(0, 70) };
        Add("U-Form", uShape,
            ("Basis", new[] { new float2(45, 0) }),
            ("Schenkel", new[] { new float2(14, 70), new float2(76, 70) }),
            ("Bucht", new[] { new float2(45, 30), new float2(45, 0) }));
        var irregular = new List<float2> { new(0, 8), new(46, 0), new(88, 14),
            new(96, 52), new(70, 84), new(24, 78), new(6, 50) };
        Add("Unregelmäßig", irregular,
            ("2 Eingänge", new[] { new float2(23, 4), new float2(83, 68) }),
            ("3 Eingänge", new[] { new float2(67, 7), new float2(47, 81),
                new float2(3, 29) }));
        var bottleneck = new List<float2> { new(0, 0), new(50, 0), new(50, 14),
            new(70, 14), new(70, 0), new(120, 0), new(120, 40), new(70, 40),
            new(70, 26), new(50, 26), new(50, 40), new(0, 40) };
        Add("Engstelle", bottleneck,
            ("Räume", new[] { new float2(25, 0), new float2(95, 40) }),
            ("an der Engstelle", new[] { new float2(60, 14), new float2(0, 20) }),
            ("Enden", new[] { new float2(0, 20), new float2(120, 20) }));
        Add("Klein", Rect(0, 0, 24, 24), ("1 Eingang", new[] { new float2(12, 0) }));
        Add("Groß", Rect(0, 0, 180, 120),
            ("2 Eingänge", new[] { new float2(90, 0), new float2(180, 60) }));
        return cases;
    }

    internal static object Run(List<string> failures)
    {
        var styles = new[] { PlazaStyle.Auto, PlazaStyle.Classic, PlazaStyle.Modern, PlazaStyle.Natural,
            PlazaStyle.Promenade, PlazaStyle.Sequence };
        var results = new List<object>();
        var times = new List<double>();
        foreach (var testCase in Cases())
        foreach (var style in styles)
        {
            var seed = 7;
            var design = PlazaComposer.Compose(testCase.Polygon, testCase.Entrances,
                style, seed);
            var again = PlazaComposer.Compose(testCase.Polygon, testCase.Entrances,
                style, seed);
            var errors = Check(testCase, design);
            if (Serialize(design) != Serialize(again)) errors.Add("not deterministic");
            foreach (var error in errors)
                failures.Add($"Plaza design {testCase.Name} [{style}]: {error}");
            times.Add(design.Metrics.Milliseconds);
            results.Add(new
            {
                name = testCase.Name, requested = style.ToString(),
                polygon = Points(testCase.Polygon), entrances = Points(testCase.Entrances),
                design = PlazaDesignJson.Describe(design), errors,
            });
        }
        Console.WriteLine($"Plaza designs: {results.Count} composed, "
            + $"avg {times.Average():F1} ms, max {times.Max():F1} ms.");
        return new { count = results.Count, cases = results,
            averageMs = times.Average(), maximumMs = times.Max() };
    }

    private static readonly Dictionary<PlazaGrammar, float> GreenMaximum = new()
    {
        [PlazaGrammar.Classic] = 0.3f, [PlazaGrammar.Modern] = 0.35f,
        [PlazaGrammar.Natural] = 0.45f,
    };

    private static List<string> Check(Case testCase, PlazaDesign design)
    {
        var errors = new List<string>();
        var polygon = testCase.Polygon;
        if (!design.Succeeded)
        {
            errors.Add($"no design: {design.Failure}");
            return errors;
        }
        var area = Math.Abs(PolygonMath.SignedArea(polygon));
        var main = design.MainPlaza;
        var rooms = design.Rooms;

        // The preset decides what it promises.
        if (design.RequestedStyle == PlazaStyle.Classic && design.Grammar != PlazaGrammar.Classic)
            errors.Add("classic preset without classic grammar");
        if (design.RequestedStyle == PlazaStyle.Natural && design.Grammar != PlazaGrammar.Natural)
            errors.Add("natural style without natural grammar");
        if (design.RequestedStyle == PlazaStyle.Modern && design.Grammar != PlazaGrammar.Modern)
            errors.Add("modern preset without modern grammar");
        // A small plot keeps a reduced design instead of a sequence.
        if (design.RequestedStyle == PlazaStyle.Sequence && design.Topology != PlazaTopology.Sequence
            && Math.Abs(PolygonMath.SignedArea(polygon)) >= 1200.0)
            errors.Add("sequence preset without a sequence of rooms");

        // Rooms lie inside the plot; the main square leads the hierarchy.
        foreach (var room in rooms)
        {
            foreach (var point in Densify(room.Boundary, 1f))
                if (!PolygonMath.PointInsideOrBoundary(point, polygon, 0.05f))
                {
                    errors.Add($"{room.Role} leaves the outline");
                    break;
                }
            if (room.Role != PlazaRoomRole.Passage
                && polygon.Any(vertex => PolygonMath.PointInside(vertex, room.Boundary)))
                errors.Add($"outline notch inside {room.Role}");
            if (room != rooms[0] && room.Importance >= rooms[0].Importance)
                errors.Add("main square is not the most important room");
        }
        var squares = rooms.Where(r => r.Role is PlazaRoomRole.MainSquare
            or PlazaRoomRole.SecondarySquare or PlazaRoomRole.GardenCourt).ToList();
        if (squares.Any(r => PolygonMath.SignedArea(r.Boundary) > PolygonMath.SignedArea(main) + 1.0))
            errors.Add("a secondary room is larger than the main square");
        if (design.Topology == PlazaTopology.Sequence && squares.Count < 2)
            errors.Add("sequence with a single room");
        if (design.Topology == PlazaTopology.Linear)
        {
            // Exact extents in the room's own frame.
            var axis = rooms[0].Axis;
            var normal = new float2(-axis.y, axis.x);
            var points = Densify(main, 1f).ToList();
            var length = points.Max(p => math.dot(p, axis)) - points.Min(p => math.dot(p, axis));
            var width = points.Max(p => math.dot(p, normal)) - points.Min(p => math.dot(p, normal));
            if (length < width * 2.4f) errors.Add("linear space is not linear");
        }
        if (design.Metrics.MainPlazaArea < area * (design.Topology == PlazaTopology.Central
                ? 0.15 : 0.08))
            errors.Add($"main square too small ({design.Metrics.MainPlazaArea / area:P0})");
        if (design.Metrics.MainPlazaMinimumWidth < 5.9f)
            errors.Add($"main square too narrow ({design.Metrics.MainPlazaMinimumWidth:F1} m)");
        if (rooms[0].Edges.Count == 0) errors.Add("main square without edge roles");

        // Green is designed and budgeted; the open square stays coherent.
        var greenShare = design.Metrics.GreenArea / area;
        if (greenShare > GreenMaximum[design.Grammar] + 0.02f)
            errors.Add($"too much green ({greenShare:P0})");
        foreach (var zone in design.GreenZones)
        {
            var points = zone.Polygon;
            if (!PlazaComposer.IsSimple(points)) errors.Add("green zone self-intersects");
            if (points.Any(p => !PolygonMath.PointInsideOrBoundary(p, polygon, 0.1f)))
                errors.Add("green zone leaves the outline");
            // Inner beds belong to one room: structure of the main square or
            // the garden of a garden court.
            var host = zone.Room >= 0 && zone.Room < rooms.Count ? rooms[zone.Room] : rooms[0];
            var inner = zone.Kind is PlazaGreenKind.Parterre or PlazaGreenKind.Island
                || zone.Kind == PlazaGreenKind.Garden
                    && points.All(p => PolygonMath.PointInside(p, host.Boundary));
            if (inner && zone.Kind is PlazaGreenKind.Parterre or PlazaGreenKind.Island) host = rooms[0];
            foreach (var room in rooms)
            {
                if (inner && room == host) continue;
                if (points.Any(p => PolygonMath.PointInside(p, room.Boundary)))
                {
                    errors.Add($"green inside {room.Role}");
                    break;
                }
            }
            if (inner && !points.All(p => PolygonMath.PointInside(p, host.Boundary)))
                errors.Add("inner bed leaves its room");
        }
        if (design.GreenZones.Count > 0 && design.GreenZones.Count(z => z.Primary) != 1)
            errors.Add("no single leading landscape motif");
        // Court gardens belong to their court, not to the plaza's motif.
        var motif = design.GreenZones.Where(z => !(rooms[z.Room].Role == PlazaRoomRole.GardenCourt
            && z.Kind == PlazaGreenKind.Garden)).ToList();
        if (design.Grammar == PlazaGrammar.Modern && motif.Count > 3)
            errors.Add($"modern design with {motif.Count} green features");
        if (design.Grammar == PlazaGrammar.Modern && motif.Count > 1 && motif.Any(z => z.Primary))
        {
            var lead = Math.Abs(PolygonMath.SignedArea(motif.First(z => z.Primary).Polygon));
            if (motif.Any(z => !z.Primary && Math.Abs(PolygonMath.SignedArea(z.Polygon))
                    > lead * PlazaComposer.ModernCompanionShare + 0.5))
                errors.Add("modern companion bed rivals the leading motif");
        }

        // Every room has a recognisable purpose.
        for (var r = 1; r < rooms.Count; r++)
        {
            var room = rooms[r];
            switch (room.Role)
            {
                case PlazaRoomRole.SecondarySquare:
                    if (PolygonMath.SignedArea(room.Boundary) > 0.65 * PolygonMath.SignedArea(main))
                        errors.Add("secondary square too close to the main square in size");
                    if (design.Topology != PlazaTopology.Linear
                        && !room.Edges.Any(e => e.Role == PlazaEdgeRole.SeatingEdge))
                        errors.Add("secondary square without a place to sit");
                    break;
                case PlazaRoomRole.GardenCourt:
                    if (!design.GreenZones.Any(z => z.Room == r))
                        errors.Add("garden court without green");
                    break;
                case PlazaRoomRole.EntranceCourt:
                    if (!room.Edges.Any(e => e.Role == PlazaEdgeRole.Open))
                        errors.Add("entrance court without an open side");
                    break;
                case PlazaRoomRole.Passage:
                    if (design.FocalPoints.Any(f => PolygonMath.PointInside(f.Position, room.Boundary)))
                        errors.Add("passage designed as a square");
                    break;
            }
        }
        if (!OpenSquareConnected(design)) errors.Add("open main square is fragmented");
        if (design.FocalPoints.Count == 0
            || !PolygonMath.PointInside(design.FocalPoints[0].Position, main)
            || design.GreenZones.Any(z => PolygonMath.PointInside(design.FocalPoints[0].Position, z.Polygon)))
            errors.Add("primary focal point not on the open main square");

        // Every entrance is opened up into a room, through no green.
        var entranceCorridors = design.MovementCorridors.Count(c => c.Entrance >= 0);
        if (entranceCorridors != testCase.Entrances.Count)
            errors.Add($"{entranceCorridors} corridors for {testCase.Entrances.Count} entrances");
        foreach (var corridor in design.MovementCorridors)
        {
            var end = corridor.Points[corridor.Points.Count - 1];
            if (corridor.Entrance >= 0 && corridor.Points.Count > 1
                && math.distance(corridor.Points[0], end) > 0.01f
                && !rooms.Any(r => PolygonMath.PointInside(end, r.Boundary)
                    || PlazaComposer.DistanceToOutline(end, r.Boundary) <= 0.6f))
                errors.Add("corridor does not reach a room");
            foreach (var point in Densify(corridor.Points, 0.5f, false))
                if (design.GreenZones.Any(zone => PolygonMath.PointInside(point, zone.Polygon)))
                {
                    errors.Add("corridor blocked by green");
                    break;
                }
        }

        // Design intent stays on free ground.
        foreach (var row in design.TreeRows)
            for (var i = 0; i < row.Count; i++)
            {
                var point = math.lerp(row.Start, row.End, row.Count > 1 ? i / (row.Count - 1f) : 0f);
                if (!PolygonMath.PointInside(point, polygon))
                {
                    errors.Add("tree outside the outline");
                    break;
                }
            }
        return errors.Distinct().ToList();
    }

    /// <summary>The main square minus its inner beds is one connected area.</summary>
    private static bool OpenSquareConnected(PlazaDesign design)
    {
        var main = design.MainPlaza;
        PolygonMath.Bounds(main, out var min, out var max);
        const float step = 1f;
        var columns = (int)Math.Ceiling((max.x - min.x) / step) + 1;
        var rows = (int)Math.Ceiling((max.y - min.y) / step) + 1;
        var open = new bool[columns * rows];
        var count = 0;
        var first = -1;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var point = min + new float2(x, y) * step;
            if (!PolygonMath.PointInside(point, main)
                || design.GreenZones.Any(z => PolygonMath.PointInside(point, z.Polygon))) continue;
            open[y * columns + x] = true;
            count++;
            if (first < 0) first = y * columns + x;
        }
        if (first < 0) return false;
        var seen = new bool[open.Length];
        var queue = new Queue<int>();
        queue.Enqueue(first);
        seen[first] = true;
        var reached = 0;
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            reached++;
            var cx = index % columns;
            var cy = index / columns;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= columns || ny >= rows) continue;
                var next = ny * columns + nx;
                if (!open[next] || seen[next]) continue;
                seen[next] = true;
                queue.Enqueue(next);
            }
        }
        // Grid aliasing along thin diagonal outlines may cut off single cells.
        return reached >= count * 0.98f;
    }

    private static string Serialize(PlazaDesign design)
        => JsonSerializer.Serialize(PlazaDesignJson.Describe(design, includeTiming: false));

    private static List<float2> Rect(float x, float y, float width, float height)
        => new() { new(x, y), new(x + width, y), new(x + width, y + height),
            new(x, y + height) };

    private static IEnumerable<float2> Densify(IReadOnlyList<float2> line, float step,
        bool closed = true)
    {
        var count = closed ? line.Count : line.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var a = line[i];
            var b = line[(i + 1) % line.Count];
            var pieces = Math.Max(1, (int)Math.Ceiling(math.distance(a, b) / step));
            for (var k = 0; k < pieces; k++) yield return math.lerp(a, b, (float)k / pieces);
        }
    }

    private static object[] Points(IEnumerable<float2> points)
        => points.Select(p => (object)new { x = p.x, y = p.y }).ToArray();
}
