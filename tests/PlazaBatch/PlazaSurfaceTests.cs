using System;
using System.Collections.Generic;
using System.Linq;
using ParkManager.Geometry;
using Unity.Mathematics;

/// <summary>
/// Surface composition checks: green and paving cover the plot exactly
/// once (no overlap, no gap), every surface is a simple polygon the game
/// accepts, and the paving is split into few pieces.
/// </summary>
internal static class PlazaSurfaceTests
{
    internal static List<string> Check(PlazaDesignTests.Case testCase, PlazaDesign design,
        PlazaSurfacePlan plan)
    {
        var errors = new List<string>();
        if (!design.Succeeded) return errors;
        var plot = testCase.Polygon;
        var surfaces = plan.Paving.Concat(plan.Green).ToList();
        foreach (var surface in surfaces)
        {
            if (!PlazaSurfaceComposer.StrictlySimple(surface)) errors.Add("surface touches itself");
            if (Edges(surface).Any(length => length < PlazaSurfaceComposer.MinimumEdge * 0.99f))
                errors.Add("surface with a degenerate edge");
        }
        var plotArea = Math.Abs(PolygonMath.SignedArea(plot));
        var covered = surfaces.Sum(s => Math.Abs(PolygonMath.SignedArea(s)));
        if (Math.Abs(covered - plotArea) > plotArea * 0.01 + 2.0)
            errors.Add($"surfaces cover {covered:F0} m² of {plotArea:F0} m²");

        // Every sample point of the plot lies in exactly one surface.
        PolygonMath.Bounds(plot, out var min, out var max);
        var gaps = 0;
        var overlaps = 0;
        var samples = 0;
        var innerGaps = 0;
        for (var y = min.y + 0.37f; y < max.y; y += 1.3f)
        for (var x = min.x + 0.29f; x < max.x; x += 1.3f)
        {
            var point = new float2(x, y);
            if (!PolygonMath.PointInside(point, plot)
                || PolygonMath.DistanceToBoundarySquared(point, plot) < 0.01f) continue;
            var near = surfaces.Any(s => PolygonMath.DistanceToBoundarySquared(point, s) < 0.01f);
            if (near) continue;
            samples++;
            var count = surfaces.Count(s => PolygonMath.PointInside(point, s));
            if (count == 0)
            {
                gaps++;
                // Inside the plaza (paths, entrances, visible paving edges)
                // nothing may be bare; only right at the plot edge.
                if (PolygonMath.DistanceToBoundarySquared(point, plot) > 1.5f * 1.5f) innerGaps++;
            }
            if (count > 1) overlaps++;
        }
        // Dropped slivers and crumbs may leave a little bare ground.
        if (gaps > samples * 0.005) errors.Add($"{gaps} of {samples} sample points without a surface");
        if (overlaps > 0) errors.Add($"{overlaps} sample points under two surfaces");
        if (innerGaps > 0) errors.Add($"{innerGaps} bare sample points inside the plaza");
        if (plan.Paving.Count > plan.Green.Count * 2 + 2)
            errors.Add($"paving split into {plan.Paving.Count} pieces for {plan.Green.Count} green areas");
        return errors;
    }

    private static IEnumerable<float> Edges(List<float2> ring)
    {
        for (var i = 0; i < ring.Count; i++) yield return math.distance(ring[i], ring[(i + 1) % ring.Count]);
    }

    internal static object Describe(PlazaSurfacePlan plan) => new
    {
        paving = plan.Paving.Select(Points).ToArray(),
        green = plan.Green.Select(Points).ToArray(),
    };

    private static object[] Points(List<float2> points)
        => points.Select(p => (object)new { x = p.x, y = p.y }).ToArray();
}
