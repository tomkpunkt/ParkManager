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
        var styles = new[] { PlazaStyle.Auto, PlazaStyle.Classic, PlazaStyle.Modern,
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

        // The main plaza lies inside the plot as one coherent shape.
        foreach (var point in Densify(main, 1f))
            if (!PolygonMath.PointInsideOrBoundary(point, polygon, 0.05f))
            {
                errors.Add("main plaza leaves the outline");
                break;
            }
        if (polygon.Any(vertex => PolygonMath.PointInside(vertex, main)))
            errors.Add("outline notch inside the main plaza");
        if (design.Metrics.MainPlazaArea < area * 0.12)
            errors.Add($"main plaza too small ({design.Metrics.MainPlazaArea / area:P0})");
        if (design.Metrics.MainPlazaMinimumWidth < 5.9f)
            errors.Add($"main plaza too narrow ({design.Metrics.MainPlazaMinimumWidth:F1} m)");

        // Green zones never cut into the main plaza and are simple polygons.
        foreach (var zone in design.GreenZones)
        {
            if (!PlazaComposer.IsSimple(zone)) errors.Add("green zone self-intersects");
            if (zone.Any(point => !PolygonMath.PointInsideOrBoundary(point, polygon, 0.1f)))
                errors.Add("green zone leaves the outline");
            if (zone.Any(point => PolygonMath.PointInside(point, main)))
                errors.Add("green zone inside the main plaza");
        }
        foreach (var point in Densify(main, 1f))
            if (design.GreenZones.Any(zone => PolygonMath.PointInside(point, zone)))
            {
                errors.Add("main plaza fragmented by green");
                break;
            }

        // Every entrance is opened up to the main plaza, through no green.
        var entranceCorridors = design.MovementCorridors.Count(c => c.Entrance >= 0);
        if (entranceCorridors != testCase.Entrances.Count)
            errors.Add($"{entranceCorridors} corridors for {testCase.Entrances.Count} entrances");
        foreach (var corridor in design.MovementCorridors)
        {
            var end = corridor.Points[corridor.Points.Count - 1];
            if (corridor.Entrance >= 0 && !PolygonMath.PointInside(end, main)
                && PlazaComposer.DistanceToOutline(end, main) > 0.6f)
                errors.Add("corridor does not reach the main plaza");
            foreach (var point in Densify(corridor.Points, 0.5f, false))
                if (design.GreenZones.Any(zone => PolygonMath.PointInside(point, zone)))
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
                if (!PolygonMath.PointInside(point, polygon)
                    || PolygonMath.PointInside(point, main))
                {
                    errors.Add("tree row point outside the rim");
                    break;
                }
            }
        if (design.FocalPoints.Count == 0
            || !PolygonMath.PointInside(design.FocalPoints[0].Position, main))
            errors.Add("primary focal point not on the main plaza");
        return errors.Distinct().ToList();
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
