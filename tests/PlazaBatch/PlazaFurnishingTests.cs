using System;
using System.Collections.Generic;
using System.Linq;
using ParkManager.Geometry;
using Unity.Mathematics;

/// <summary>
/// Phase-2 checks: the furnishing realises the design without breaking it.
/// Objects stay on the plot, apart from each other, off corridors and
/// entrances, benches and lamps off green, rows evenly spaced, mirrored
/// avenues equal, and every room reachable from every entrance.
/// </summary>
internal static class PlazaFurnishingTests
{
    internal static List<string> Check(PlazaDesignTests.Case testCase, PlazaDesign design,
        PlazaFurnishing furnishing)
    {
        var errors = new List<string>();
        if (!design.Succeeded) return errors;
        var polygon = testCase.Polygon;
        var plan = furnishing.Plan;
        var items = plan.Furniture.Select(f => (f.Position, f.FootprintRadius, Kind: f.Kind.ToString()))
            .Concat(plan.Centerpieces.Select(c => (c.Position, FootprintRadius: c.Radius, Kind: "Centerpiece")))
            .ToList();

        foreach (var item in items)
        {
            if (!PolygonMath.PointInside(item.Position, polygon)
                || Math.Sqrt(PolygonMath.DistanceToBoundarySquared(item.Position, polygon))
                    < item.FootprintRadius + 0.25)
                errors.Add($"{item.Kind} leaves the plot");
            foreach (var corridor in design.MovementCorridors.Where(c => c.Points.Count > 1))
                if (PlazaMovement.DistanceToPolyline(item.Position, corridor.Points)
                    < corridor.Width * 0.5f + item.FootprintRadius)
                    errors.Add($"{item.Kind} on a corridor");
            if (item.Kind != "Tree" && design.GreenZones.Any(z => PolygonMath.PointInside(item.Position, z.Polygon)))
                errors.Add($"{item.Kind} in a bed");
        }
        for (var i = 0; i < items.Count; i++)
        for (var k = i + 1; k < items.Count; k++)
            if (math.distance(items[i].Position, items[k].Position)
                < items[i].FootprintRadius + items[k].FootprintRadius + 0.3f)
            {
                errors.Add($"{items[i].Kind} overlaps {items[k].Kind}");
                break;
            }

        // Rows read as rows: even gaps, no hole where one object fell out.
        foreach (var motif in furnishing.Motifs.Where(m => m.Kind == PlazaMotifKind.TreeAvenue
                     && m.Placements.Count >= 3))
        {
            var gaps = new List<float>();
            for (var i = 1; i < motif.Placements.Count; i++)
                gaps.Add(math.distance(motif.Placements[i].Position, motif.Placements[i - 1].Position));
            if (gaps.Max() - gaps.Min() > 0.05f) errors.Add("tree avenue unevenly spaced");
        }
        foreach (var motif in furnishing.Motifs.Where(m => m.Partner >= 0))
        {
            var partner = furnishing.Motifs[motif.Partner];
            if (motif.Placements.Count > 0 && partner.Placements.Count > 0
                && motif.Placements.Count != partner.Placements.Count)
                errors.Add("mirrored avenues differ");
        }
        if (furnishing.Motifs.Any(m => m.Kind == PlazaMotifKind.EntranceLamps
                && m.Placements.Count == 1))
            errors.Add("entrance marked by a single lamp");
        if (!furnishing.Walkable) errors.Add("furnished plaza is not walkable");
        return errors;
    }

    internal static object Describe(PlazaFurnishing furnishing, bool includeTiming = true) => new
    {
        centerpieces = furnishing.Plan.Centerpieces.Select(c => new
        {
            kind = c.Kind.ToString(), x = c.Position.x, y = c.Position.y, radius = c.Radius,
        }).ToArray(),
        furniture = furnishing.Plan.Furniture.Select(f => new
        {
            kind = f.Kind.ToString(), x = f.Position.x, y = f.Position.y,
            rotation = f.Rotation, radius = f.FootprintRadius, size = f.Size,
            motif = f.ArrangementId,
        }).ToArray(),
        motifs = furnishing.Motifs.Select(m => new
        {
            kind = m.Kind.ToString(), room = m.Room, count = m.Placements.Count + m.Centerpieces.Count,
        }).ToArray(),
        walkable = furnishing.Walkable,
        removed = furnishing.RemovedForMovement,
        milliseconds = includeTiming ? furnishing.Milliseconds : 0.0,
    };
}
