using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Deterministic, bounded first furnishing pass. Vegetation is sampled in
    /// the polygon with separate random streams; furniture follows paths and
    /// the optional fence follows the boundary while leaving gate gaps.
    /// </summary>
    internal static class ParkDecorationPlanner
    {
        private const int MaximumTrees = 180;
        private const int MaximumBushes = 300;
        private const int MaximumFurniture = 160;
        private const int MaximumFencePieces = 500;

        internal static ParkDecorationPlan Generate(IReadOnlyList<float2> polygon,
            ParkPathPlan paths, IReadOnlyList<float2> entrances, int seed,
            float builtPathWidth, bool fenceEnabled, int vegetationDensity)
        {
            var result = new List<ParkDecorationPlacement>();
            if (polygon == null || polygon.Count < 3)
                return new ParkDecorationPlan(seed, fenceEnabled, result);

            var area = Math.Abs(SignedArea(polygon));
            var min = polygon[0];
            var max = polygon[0];
            for (var i = 1; i < polygon.Count; i++)
            {
                min = math.min(min, polygon[i]);
                max = math.max(max, polygon[i]);
            }

            var densityScale = math.clamp(vegetationDensity, 25, 200) / 100.0;
            var trees = math.clamp((int)Math.Round(area / 380.0 * densityScale),
                1, MaximumTrees);
            var bushes = math.clamp((int)Math.Round(area / 230.0 * densityScale),
                1, MaximumBushes);
            var vegetationClusters = BuildVegetationClusters(polygon, min, max,
                area, MixSeed(seed, 0x3c6ef372u));
            // Furniture is planned first so both vegetation layers can reserve
            // its footprint. The random streams are independent, therefore the
            // ordering does not make either layer non-deterministic.
            SampleFurniture(result, polygon, paths, entrances,
                ParkDecorationKind.Bench, 34f, 0.75f,
                builtPathWidth, MixSeed(seed, 0x7f4a7c15u));
            SampleFurniture(result, polygon, paths, entrances,
                ParkDecorationKind.Lamp, 23f, 0.45f,
                builtPathWidth, MixSeed(seed, 0x94d049bbu));
            SampleTrashBins(result, polygon, paths, entrances, builtPathWidth,
                MixSeed(seed, 0xa54ff53au));
            SampleVegetation(result, polygon, paths, entrances, min, max,
                vegetationClusters, area, trees, ParkDecorationKind.Tree,
                builtPathWidth, MixSeed(seed, 0x51f15e21u));
            SampleVegetation(result, polygon, paths, entrances, min, max,
                vegetationClusters, area, bushes, ParkDecorationKind.Bush,
                builtPathWidth, MixSeed(seed, 0x9e3779b9u));
            if (fenceEnabled)
                SampleFence(result, polygon, entrances, builtPathWidth,
                    MixSeed(seed, 0xd1b54a35u));
            return new ParkDecorationPlan(seed, fenceEnabled, result);
        }

        private static void SampleVegetation(List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, ParkPathPlan paths,
            IReadOnlyList<float2> entrances, float2 min, float2 max,
            IReadOnlyList<float2> clusters, double area, int target,
            ParkDecorationKind kind, float builtPathWidth, uint seed)
        {
            var random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            var accepted = new List<float2>();
            var spacing = kind == ParkDecorationKind.Tree ? 10f : 5f;
            var boundaryClearance = kind == ParkDecorationKind.Tree ? 4f : 2.2f;
            var clusterRadius = math.clamp((float)Math.Sqrt(area) * 0.16f,
                12f, 28f) * (kind == ParkDecorationKind.Tree ? 1f : 0.78f);
            var attempts = Math.Max(80, target * 90);
            for (var attempt = 0; attempt < attempts && accepted.Count < target; attempt++)
            {
                float2 point;
                // Both vegetation layers share their cluster centres. Their
                // independent streams still keep the result organic and the
                // uniform remainder avoids isolated artificial "islands".
                if (clusters != null && clusters.Count > 0
                    && random.NextFloat() < 0.62f)
                {
                    var centre = clusters[random.NextInt(0, clusters.Count)];
                    var angle = random.NextFloat(0f, math.PI * 2f);
                    var radius = math.sqrt(random.NextFloat()) * clusterRadius;
                    point = centre + new float2(math.cos(angle), math.sin(angle))
                        * radius;
                }
                else point = random.NextFloat2(min, max);
                var size = kind == ParkDecorationKind.Tree
                    ? random.NextFloat(5f, 8.5f)
                    : random.NextFloat(2.3f, 4.2f);
                var collisionRadius = kind == ParkDecorationKind.Tree
                    ? math.max(2.5f, size * 0.45f)
                    : math.max(1.2f, size * 0.45f);
                if (!PointInside(point, polygon)
                    || DistanceToBoundarySquared(point, polygon)
                        < boundaryClearance * boundaryClearance
                    || !HasPathClearance(point, collisionRadius, paths,
                        builtPathWidth)
                    || DistanceToPointsSquared(point, entrances)
                        < 100f) continue;

                var valid = true;
                for (var i = 0; i < accepted.Count; i++)
                    if (math.distancesq(point, accepted[i]) < spacing * spacing)
                    { valid = false; break; }
                if (!valid) continue;
                for (var i = 0; i < result.Count; i++)
                {
                    var other = result[i];
                    float combined;
                    if (other.Kind == ParkDecorationKind.Bench)
                        combined = collisionRadius + 1.75f;
                    else if (other.Kind == ParkDecorationKind.Lamp)
                        combined = collisionRadius + 0.85f;
                    else if (other.Kind == ParkDecorationKind.TrashBin)
                        combined = collisionRadius + 0.75f;
                    else if (other.Kind == ParkDecorationKind.Tree
                        || other.Kind == ParkDecorationKind.Bush)
                        combined = kind == ParkDecorationKind.Tree
                            || other.Kind == ParkDecorationKind.Tree ? 6f : 3.5f;
                    else continue;
                    if (math.distancesq(point, other.Position) < combined * combined)
                    { valid = false; break; }
                }
                if (!valid) continue;
                accepted.Add(point);
                result.Add(new ParkDecorationPlacement
                {
                    Kind = kind,
                    Position = point,
                    Rotation = random.NextFloat(0f, math.PI * 2f),
                    Size = size,
                    Variant = unchecked((uint)random.NextInt()),
                    AgeStage = kind == ParkDecorationKind.Tree
                        ? SelectTreeAge(ref random) : (byte)0,
                });
            }
        }

        private static List<float2> BuildVegetationClusters(
            IReadOnlyList<float2> polygon, float2 min, float2 max, double area,
            uint seed)
        {
            var result = new List<float2>();
            var random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            var target = math.clamp((int)Math.Round(Math.Sqrt(area) / 38.0), 2, 7);
            var minimumSpacing = math.clamp((float)Math.Sqrt(area) * 0.18f,
                15f, 34f);
            for (var attempt = 0; attempt < target * 80
                && result.Count < target; attempt++)
            {
                var point = random.NextFloat2(min, max);
                if (!PointInside(point, polygon)) continue;
                var valid = true;
                for (var i = 0; i < result.Count; i++)
                    if (math.distancesq(point, result[i])
                        < minimumSpacing * minimumSpacing)
                    { valid = false; break; }
                if (valid) result.Add(point);
            }
            return result;
        }

        private static byte SelectTreeAge(ref Unity.Mathematics.Random random)
        {
            // Keep consuming the dedicated vegetation stream so changing the
            // age policy does not accidentally reshuffle later placements.
            random.NextInt();
            return 3; // bestehender Park: ausschließlich alte Bäume
        }

        private static void SampleFurniture(List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, ParkPathPlan paths,
            IReadOnlyList<float2> entrances, ParkDecorationKind kind,
            float interval, float footprintRadius, float builtPathWidth, uint seed)
        {
            if (paths == null) return;
            var random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            var count = 0;
            for (var edgeIndex = 0; edgeIndex < paths.Edges.Count
                && count < MaximumFurniture; edgeIndex++)
            {
                var edge = paths.Edges[edgeIndex];
                var a = paths.Nodes[edge.A].Position;
                var b = paths.Nodes[edge.B].Position;
                var delta = b - a;
                var length = math.length(delta);
                if (length < interval * 0.55f) continue;
                var tangent = delta / length;
                var normal = new float2(-tangent.y, tangent.x);
                var pieces = Math.Max(1, (int)Math.Floor(length / interval));
                for (var i = 0; i < pieces && count < MaximumFurniture; i++)
                {
                    var t = (i + 1f) / (pieces + 1f);
                    t = math.clamp(t + random.NextFloat(-0.06f, 0.06f), 0.15f, 0.85f);
                    var sidePhase = kind == ParkDecorationKind.Lamp ? 1
                        : kind == ParkDecorationKind.TrashBin ? 2 : 0;
                    var side = ((i + edgeIndex + sidePhase) & 1) == 0
                        ? 1f : -1f;
                    // Put the object's footprint immediately beside the real
                    // rendered path. The ECS layer refines this fallback with
                    // the selected prefab's exact collision bounds.
                    var safeOffset = math.max(edge.Width, builtPathWidth) * 0.5f
                        + footprintRadius + 0.2f;
                    var point = math.lerp(a, b, t)
                        + normal * (safeOffset * side);
                    if (!PointInside(point, polygon)
                        || DistanceToBoundarySquared(point, polygon) < 2.25f
                        || DistanceToPointsSquared(point, entrances) < 64f)
                        continue;
                    if (TooCloseToKind(result, point, kind,
                        FurnitureSpacing(kind))) continue;
                    if (TooCloseToFurniture(result, point, kind)) continue;
                    result.Add(new ParkDecorationPlacement
                    {
                        Kind = kind,
                        Position = point,
                        Rotation = math.atan2(tangent.x, tangent.y)
                            + (side < 0f ? math.PI : 0f),
                        Size = FurniturePreviewSize(kind),
                        Variant = unchecked((uint)random.NextInt()),
                    });
                    count++;
                }
            }
        }

        /// <summary>
        /// Places bins where they are useful instead of distributing them at a
        /// fixed interval: first near gates, then at real graph junctions and
        /// finally beside benches. Positions remain just outside the rendered
        /// path footprint and use the same deterministic seed as the rest of
        /// the furnishing plan.
        /// </summary>
        private static void SampleTrashBins(
            List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, ParkPathPlan paths,
            IReadOnlyList<float2> entrances, float builtPathWidth, uint seed)
        {
            if (paths == null || paths.Edges.Count == 0) return;
            var random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            var incident = new List<int>[paths.Nodes.Count];
            for (var i = 0; i < incident.Length; i++) incident[i] = new List<int>();
            for (var i = 0; i < paths.Edges.Count; i++)
            {
                incident[paths.Edges[i].A].Add(i);
                incident[paths.Edges[i].B].Add(i);
            }

            var importantNodes = 0;
            for (var i = 0; i < paths.Nodes.Count; i++)
                if (paths.Nodes[i].Kind == ParkPathNodeKind.Gate
                    || incident[i].Count >= 3) importantNodes++;
            var target = math.clamp(Math.Max(importantNodes,
                (int)Math.Round(paths.TotalLength / 90f)), 1, 18);
            var placed = 0;

            // Gate nodes have first priority. Moving a few metres into the park
            // keeps the bin clear of the actual entrance opening.
            for (var i = 0; i < paths.Nodes.Count && placed < target; i++)
                if (paths.Nodes[i].Kind == ParkPathNodeKind.Gate
                    && TryPlaceTrashAtNode(result, polygon, paths, incident,
                        i, 3.5f, 6f, builtPathWidth, ref random)) placed++;

            // A graph degree of three or more is a genuine crossing or fork.
            for (var i = 0; i < paths.Nodes.Count && placed < target; i++)
                if (paths.Nodes[i].Kind != ParkPathNodeKind.Gate
                    && incident[i].Count >= 3
                    && TryPlaceTrashAtNode(result, polygon, paths, incident,
                        i, 2.5f, 4.5f, builtPathWidth, ref random)) placed++;

            // Benches were generated first. Offset along their path tangent so
            // the bin is nearby without intersecting the seating footprint.
            var furnitureCount = result.Count;
            for (var i = 0; i < furnitureCount && placed < target; i++)
            {
                var bench = result[i];
                if (bench.Kind != ParkDecorationKind.Bench) continue;
                var tangent = math.normalizesafe(new float2(
                    math.sin(bench.Rotation), math.cos(bench.Rotation)));
                var firstSign = random.NextBool() ? 1f : -1f;
                var accepted = false;
                for (var attempt = 0; attempt < 2 && !accepted; attempt++)
                {
                    var sign = attempt == 0 ? firstSign : -firstSign;
                    var point = bench.Position + tangent * sign
                        * random.NextFloat(2.7f, 4.2f);
                    accepted = TryAddTrashBin(result, polygon, point,
                        tangent, ref random);
                }
                if (accepted) placed++;
            }

            // Very small or degenerate graphs may contain none of the preferred
            // anchors. Retain one useful path-side bin as a graceful fallback.
            if (placed == 0)
                SampleFurniture(result, polygon, paths, entrances,
                    ParkDecorationKind.TrashBin, 70f, 0.45f,
                    builtPathWidth, seed);
        }

        private static bool TryPlaceTrashAtNode(
            List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, ParkPathPlan paths,
            IReadOnlyList<int>[] incident, int nodeIndex, float minimumAlong,
            float maximumAlong, float builtPathWidth,
            ref Unity.Mathematics.Random random)
        {
            var edges = incident[nodeIndex];
            if (edges == null || edges.Count == 0) return false;
            var edgeStart = random.NextInt(0, edges.Count);
            var firstSide = random.NextBool() ? 1f : -1f;
            for (var edgeOffset = 0; edgeOffset < edges.Count; edgeOffset++)
            {
                var edge = paths.Edges[edges[(edgeStart + edgeOffset) % edges.Count]];
                var other = edge.A == nodeIndex ? edge.B : edge.A;
                var tangent = math.normalizesafe(paths.Nodes[other].Position
                    - paths.Nodes[nodeIndex].Position);
                if (math.lengthsq(tangent) < 0.5f) continue;
                var normal = new float2(-tangent.y, tangent.x);
                var along = random.NextFloat(minimumAlong, maximumAlong);
                var sideOffset = math.max(edge.Width, builtPathWidth) * 0.5f
                    + FurnitureCollisionRadius(ParkDecorationKind.TrashBin) + 0.2f;
                for (var sideAttempt = 0; sideAttempt < 2; sideAttempt++)
                {
                    var side = sideAttempt == 0 ? firstSide : -firstSide;
                    var point = paths.Nodes[nodeIndex].Position + tangent * along
                        + normal * sideOffset * side;
                    if (TryAddTrashBin(result, polygon, point, tangent,
                        ref random)) return true;
                }
            }
            return false;
        }

        private static bool TryAddTrashBin(List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, float2 point, float2 tangent,
            ref Unity.Mathematics.Random random)
        {
            if (!PointInside(point, polygon)
                || DistanceToBoundarySquared(point, polygon) < 1f
                || TooCloseToKind(result, point, ParkDecorationKind.TrashBin, 9f)
                || TooCloseToFurniture(result, point,
                    ParkDecorationKind.TrashBin)) return false;
            result.Add(new ParkDecorationPlacement
            {
                Kind = ParkDecorationKind.TrashBin,
                Position = point,
                Rotation = math.atan2(tangent.x, tangent.y),
                Size = FurniturePreviewSize(ParkDecorationKind.TrashBin),
                Variant = unchecked((uint)random.NextInt()),
            });
            return true;
        }

        private static void SampleFence(List<ParkDecorationPlacement> result,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            float builtPathWidth, uint seed)
        {
            var random = new Unity.Mathematics.Random(seed == 0 ? 1u : seed);
            // One deterministic prefab variant is used for the complete fence.
            // Mixing pieces with different mesh lengths cannot form a continuous
            // line and differs from the game's object-line fence mode.
            var fenceVariant = unchecked((uint)random.NextInt());
            var count = 0;
            for (var edgeIndex = 0; edgeIndex < polygon.Count
                && count < MaximumFencePieces; edgeIndex++)
            {
                var a = polygon[edgeIndex];
                var b = polygon[(edgeIndex + 1) % polygon.Count];
                var delta = b - a;
                var length = math.length(delta);
                if (length < 1f) continue;
                var tangent = delta / length;
                var runs = new List<float2> { new float2(0f, length) };
                if (entrances != null)
                {
                    for (var gateIndex = 0; gateIndex < entrances.Count; gateIndex++)
                    {
                        var gate = entrances[gateIndex];
                        var projected = math.dot(gate - a, tangent);
                        var closest = a + tangent * math.clamp(projected, 0f, length);
                        if (math.distancesq(gate, closest) > 4f) continue;
                        var halfGap = math.max(3.75f,
                            builtPathWidth * 0.5f + 0.75f);
                        CutFenceGap(runs, projected - halfGap,
                            projected + halfGap, length);
                    }
                }
                runs.Sort((left, right) => left.x.CompareTo(right.x));
                for (var i = 0; i < runs.Count && count < MaximumFencePieces; i++)
                {
                    var runLength = runs[i].y - runs[i].x;
                    if (runLength < 0.5f) continue;
                    var point = a + tangent * ((runs[i].x + runs[i].y) * 0.5f);
                    result.Add(new ParkDecorationPlacement
                    {
                        Kind = ParkDecorationKind.Fence,
                        Position = point,
                        Rotation = math.atan2(tangent.x, tangent.y),
                        // Fence placements describe continuous runs. The ECS
                        // builder expands each run using the chosen prefab's
                        // real longitudinal mesh bounds.
                        Size = runLength,
                        Variant = fenceVariant,
                    });
                    count++;
                }
            }
        }

        private static void CutFenceGap(List<float2> runs, float gapStart,
            float gapEnd, float edgeLength)
        {
            gapStart = math.clamp(gapStart, 0f, edgeLength);
            gapEnd = math.clamp(gapEnd, 0f, edgeLength);
            if (gapEnd <= gapStart) return;
            for (var i = runs.Count - 1; i >= 0; i--)
            {
                var run = runs[i];
                if (gapEnd <= run.x || gapStart >= run.y) continue;
                runs.RemoveAt(i);
                if (gapStart - run.x >= 0.5f)
                    runs.Add(new float2(run.x, gapStart));
                if (run.y - gapEnd >= 0.5f)
                    runs.Add(new float2(gapEnd, run.y));
            }
        }

        private static bool TooCloseToKind(List<ParkDecorationPlacement> items,
            float2 point, ParkDecorationKind kind, float distance)
        {
            var squared = distance * distance;
            for (var i = 0; i < items.Count; i++)
                if (items[i].Kind == kind
                    && math.distancesq(items[i].Position, point) < squared)
                    return true;
            return false;
        }

        private static bool TooCloseToFurniture(
            List<ParkDecorationPlacement> items, float2 point,
            ParkDecorationKind kind)
        {
            var ownRadius = FurnitureCollisionRadius(kind);
            for (var i = 0; i < items.Count; i++)
            {
                var other = items[i];
                if (other.Kind != ParkDecorationKind.Bench
                    && other.Kind != ParkDecorationKind.Lamp
                    && other.Kind != ParkDecorationKind.TrashBin) continue;
                var otherRadius = FurnitureCollisionRadius(other.Kind);
                var clearance = ownRadius + otherRadius + 0.35f;
                if (math.distancesq(point, other.Position)
                    < clearance * clearance) return true;
            }
            return false;
        }

        private static float FurnitureCollisionRadius(ParkDecorationKind kind)
        {
            if (kind == ParkDecorationKind.Bench) return 1.75f;
            if (kind == ParkDecorationKind.Lamp) return 0.85f;
            return 0.75f;
        }

        private static float FurnitureSpacing(ParkDecorationKind kind)
        {
            if (kind == ParkDecorationKind.Bench) return 14f;
            if (kind == ParkDecorationKind.Lamp) return 10f;
            return 12f;
        }

        private static float FurniturePreviewSize(ParkDecorationKind kind)
        {
            if (kind == ParkDecorationKind.Bench) return 3f;
            if (kind == ParkDecorationKind.Lamp) return 2.2f;
            return 1.2f;
        }

        private static bool HasPathClearance(float2 point, float objectRadius,
            ParkPathPlan paths, float builtPathWidth)
        {
            if (paths == null) return true;
            for (var i = 0; i < paths.Edges.Count; i++)
            {
                var edge = paths.Edges[i];
                var clearance = math.max(edge.Width, builtPathWidth) * 0.5f
                    + objectRadius + 0.35f;
                if (DistanceToSegmentSquared(point,
                    paths.Nodes[edge.A].Position,
                    paths.Nodes[edge.B].Position) < clearance * clearance)
                    return false;
            }
            return true;
        }

        private static float DistanceToPathsSquared(float2 point, ParkPathPlan paths)
        {
            if (paths == null || paths.Edges.Count == 0) return float.MaxValue;
            var best = float.MaxValue;
            for (var i = 0; i < paths.Edges.Count; i++)
            {
                var edge = paths.Edges[i];
                best = math.min(best, DistanceToSegmentSquared(point,
                    paths.Nodes[edge.A].Position, paths.Nodes[edge.B].Position));
            }
            return best;
        }

        private static float DistanceToBoundarySquared(float2 point,
            IReadOnlyList<float2> polygon)
        {
            var best = float.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
                best = math.min(best, DistanceToSegmentSquared(point, polygon[i],
                    polygon[(i + 1) % polygon.Count]));
            return best;
        }

        private static float DistanceToPointsSquared(float2 point,
            IReadOnlyList<float2> points)
        {
            var best = float.MaxValue;
            if (points == null) return best;
            for (var i = 0; i < points.Count; i++)
                best = math.min(best, math.distancesq(point, points[i]));
            return best;
        }

        private static float DistanceToSegmentSquared(float2 point, float2 a, float2 b)
        {
            var delta = b - a;
            var length = math.lengthsq(delta);
            if (length < 1e-5f) return math.distancesq(point, a);
            var t = math.clamp(math.dot(point - a, delta) / length, 0f, 1f);
            return math.distancesq(point, a + delta * t);
        }

        private static bool PointInside(float2 point, IReadOnlyList<float2> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                        / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static double SignedArea(IReadOnlyList<float2> polygon)
        {
            double area = 0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += (double)a.x * b.y - (double)b.x * a.y;
            }
            return area * 0.5;
        }

        private static uint MixSeed(int seed, uint salt)
        {
            var value = unchecked((uint)seed) ^ salt;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value == 0 ? 1u : value;
        }
    }
}
