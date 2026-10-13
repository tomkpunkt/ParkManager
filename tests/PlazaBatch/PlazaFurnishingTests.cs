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
            var inBed = design.GreenZones.Any(z => PolygonMath.PointInside(item.Position, z.Polygon));
            // Planting belongs in the beds, everything else stays out.
            if (item.Kind == "Bush" ? !inBed : item.Kind != "Tree" && inBed)
                errors.Add(item.Kind == "Bush" ? "planting outside a bed" : $"{item.Kind} in a bed");
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

    /// <summary>
    /// Adversarial runs: oversized assets, very high density, a plaza
    /// reached through a narrow neck and plots with very small side rooms.
    /// The furnishing must stay valid and walkable; the solver has to
    /// prove that it repairs blocked plazas.
    /// </summary>
    internal static void Stress(List<string> failures)
    {
        var huge = PlazaPalette.Default();
        huge.Items[PlazaFurnitureKind.Tree] = new PlazaPaletteItem
            { AssetName = "Oak", FootprintRadius = 2.4f, Size = 14f, CrownRadius = 7f };
        huge.Items[PlazaFurnitureKind.Bench] = new PlazaPaletteItem
            { AssetName = "Long bench", FootprintRadius = 2.6f, Size = 5f };
        huge.Items[PlazaFurnitureKind.Lamp] = new PlazaPaletteItem
            { AssetName = "Mast", FootprintRadius = 1.1f, Size = 2f };
        huge.Items[PlazaFurnitureKind.Bush] = new PlazaPaletteItem
            { AssetName = "Big planter", FootprintRadius = 1.8f, Size = 3.6f };
        huge.Density = 2f;
        var dense = PlazaPalette.Default();
        dense.Density = 3f;
        var palettes = new[] { ("huge", huge), ("dense", dense) };

        var cases = new List<PlazaDesignTests.Case>(PlazaDesignTests.Cases());
        // A square reached only through a 6 m neck.
        cases.Add(new PlazaDesignTests.Case("Stress · enger Zugang", new List<float2>
        {
            new(0, 20), new(50, 20), new(50, 70), new(28, 70), new(28, 82), new(22, 82),
            new(22, 70), new(0, 70),
        }, new List<float2> { new(25, 82), new(50, 45) }));
        // A main square with two tiny pockets.
        cases.Add(new PlazaDesignTests.Case("Stress · kleine Nebenräume", new List<float2>
        {
            new(0, 0), new(60, 0), new(60, 40), new(52, 40), new(52, 50), new(44, 50),
            new(44, 40), new(16, 40), new(16, 50), new(8, 50), new(8, 40), new(0, 40),
        }, new List<float2> { new(30, 0), new(12, 50), new(48, 50) }));

        var removed = 0;
        var runs = 0;
        foreach (var testCase in cases)
        foreach (var style in new[] { PlazaStyle.Auto, PlazaStyle.Classic, PlazaStyle.Modern,
                     PlazaStyle.Natural, PlazaStyle.Sequence })
        {
            var design = PlazaComposer.Compose(testCase.Polygon, testCase.Entrances, style, 11);
            if (!design.Succeeded) continue;
            foreach (var (name, palette) in palettes)
            {
                var furnishing = PlazaFurnisher.Furnish(design, palette, 11);
                runs++;
                removed += furnishing.RemovedForMovement;
                foreach (var error in Check(testCase, design, furnishing))
                    failures.Add($"Plaza stress {testCase.Name} [{style}, {name}]: {error}");
            }
        }
        Console.WriteLine($"Plaza stress: {runs} furnishings, {removed} objects removed "
            + "to keep the plazas walkable.");

        // The repair itself: a row of benches across the only access.
        var neck = cases.First(c => c.Name == "Stress · enger Zugang");
        var blocked = PlazaComposer.Compose(neck.Polygon, neck.Entrances, PlazaStyle.Classic, 11);
        var wall = Enumerable.Range(0, 3).Select(i => new PlazaFurniturePlacement
        {
            Kind = PlazaFurnitureKind.Bench, AssetName = "Bench", FootprintRadius = 1f,
            Position = new float2(23f + i * 2f, 76f),
        }).ToList();
        var repaired = PlazaFurnisher.Furnish(blocked, PlazaPalette.Default(), 11, wall);
        Console.WriteLine($"Plaza stress: blocked neck repaired by removing "
            + $"{repaired.RemovedForMovement} objects.");
        if (!repaired.Walkable) failures.Add("Plaza stress: a blocked access is not repaired");
        if (repaired.RemovedForMovement == 0)
            failures.Add("Plaza stress: a bench row across the only access was not detected");
        if (repaired.Motifs.Where(m => m.Kind != PlazaMotifKind.Preset)
                .Sum(m => m.Placements.Count) == 0)
            failures.Add("Plaza stress: the repair removed the designed objects instead of the blockers");
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
