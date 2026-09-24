using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Builds a symmetric plaza furnishing plan and a separate, invisible
    /// pedestrian network. All positions use the local XZ planning plane.
    /// </summary>
    internal static class PlazaPlanner
    {
        private const int CirculationNodeCount = 12;
        private const float CenterSafetyMargin = 0.75f;
        private const float CirculationOffset = 1.8f;
        private const float FurnitureBoundaryClearance = 0.65f;
        private const float EntranceFurnitureClearance = 7f;
        private const float RoutingSampleSpacing = 2f;
        private const int MaximumFurniture = 64;

        /// <summary>
        /// Uses the same center and circulation clearance as Generate, so the
        /// chooser never offers a centerpiece that cannot fit the polygon.
        /// </summary>
        internal static bool CanFitCenterpiece(IReadOnlyList<float2> polygon,
            float centerFootprintRadius)
        {
            if (polygon == null || polygon.Count < 3
                || !IsFinite(centerFootprintRadius)) return false;
            var radius = math.max(2f, centerFootprintRadius);
            if (!TryChooseCenter(polygon, radius, out var center)) return false;
            var protectedRadius = radius + CenterSafetyMargin;
            var circulationRadius = protectedRadius + CirculationOffset;
            return TryBuildRing(polygon, center, protectedRadius,
                ref circulationRadius, out _);
        }

        /// <summary>
        /// Generates a deterministic plaza inside polygon. Furniture is added
        /// only in opposite pairs whose two footprints fit. The returned
        /// routing segments connect entrances to an inner circulation ring;
        /// callers must keep those segments invisible.
        /// </summary>
        internal static PlazaPlan Generate(IReadOnlyList<float2> polygon,
            IReadOnlyList<float2> entrances, float centerFootprintRadius,
            PlazaLayoutMode mode, int seed, bool includeCenterpiece = true,
            IReadOnlyList<PlazaArrangementItem> arrangement = null)
        {
            var furniture = new List<PlazaFurniturePlacement>();
            var routes = new List<PlazaRoutingSegment>();
            var centerpieces = new List<PlazaCenterpiecePlacement>();
            if (polygon == null || polygon.Count < 3)
                return new PlazaPlan(seed, mode, centerpieces, furniture, routes);

            var radius = includeCenterpiece && mode != PlazaLayoutMode.Open
                && IsFinite(centerFootprintRadius)
                ? math.max(0f, centerFootprintRadius) : 0f;
            float2 center;
            if (!TryChooseCenter(polygon, radius, out center))
                return new PlazaPlan(seed, mode, centerpieces, furniture, routes);

            var protectedRadius = radius > 0f
                ? math.max(2f, radius) + CenterSafetyMargin : 0.75f;
            var majorAxis = FindMajorAxis(polygon, center, out var majorSpan,
                out var minorSpan);
            if (radius > 0f)
            {
                var stationOffset = 0f;
                var count = 1;
                if (mode == PlazaLayoutMode.Axial
                    && majorSpan > minorSpan * 1.6f)
                {
                    // A capsule-shaped pedestrian loop can surround two or
                    // three matching centerpieces on a long rectangular plaza.
                    var spacing = protectedRadius * 2f + 2.5f;
                    if (majorSpan >= spacing * 2f + protectedRadius * 2f + 5f)
                    { count = 3; stationOffset = spacing; }
                    else if (majorSpan >= spacing + protectedRadius * 2f + 5f)
                    { count = 2; stationOffset = spacing * 0.5f; }
                }
                for (var i = 0; i < count; i++)
                    centerpieces.Add(new PlazaCenterpiecePlacement
                    {
                        Kind = SelectCenterpiece(seed),
                        Position = center + majorAxis * (count == 1 ? 0f
                            : count == 2 ? (i == 0 ? -stationOffset : stationOffset)
                            : (i - 1) * stationOffset),
                        Radius = radius,
                    });
            }
            if (!BuildHiddenRoutingNetwork(routes, polygon, entrances,
                    center, protectedRadius, centerpieces, majorAxis))
            {
                // Long-axis repetition is optional: fall back to one center
                // if the capsule does not fit an irregular outline.
                if (centerpieces.Count > 1)
                {
                    centerpieces.RemoveRange(1, centerpieces.Count - 1);
                    centerpieces[0] = new PlazaCenterpiecePlacement
                    {
                        Kind = SelectCenterpiece(seed), Position = center,
                        Radius = radius,
                    };
                    routes.Clear();
                    BuildHiddenRoutingNetwork(routes, polygon, entrances,
                        center, protectedRadius, centerpieces, majorAxis);
                }
            }
            if (routes.Count == 0)
                return new PlazaPlan(seed, mode, centerpieces, furniture,
                    routes);
            var candidates = mode == PlazaLayoutMode.Boundary
                || mode == PlazaLayoutMode.Open || centerpieces.Count > 1
                ? BuildBoundaryFurnitureCandidates(center, protectedRadius,
                    majorAxis, majorSpan, minorSpan, seed)
                : BuildFurnitureCandidates(center, protectedRadius, mode, seed);
            if (arrangement != null && arrangement.Count > 0)
                for (var i = 0; i < candidates.Count
                    && furniture.Count + arrangement.Count * 2 <= MaximumFurniture; i++)
                    TryAddArrangement(furniture, candidates[i], arrangement,
                        center, centerpieces, polygon,
                        entrances, routes, i + 1);
            return new PlazaPlan(seed, mode, centerpieces, furniture, routes);
        }

        private static void TryAddArrangement(
            List<PlazaFurniturePlacement> furniture,
            FurniturePairCandidate anchor,
            IReadOnlyList<PlazaArrangementItem> arrangement,
            float2 center,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaRoutingSegment> routes, int arrangementId)
        {
            var tangent = math.normalizesafe(new float2(
                -(anchor.Position - center).y,
                (anchor.Position - center).x), new float2(1f, 0f));
            var width = 0f;
            for (var i = 0; i < arrangement.Count; i++)
                width += arrangement[i].FootprintRadius * 2f;
            width += (arrangement.Count - 1) * 0.55f;
            var offset = -width * 0.5f;
            var proposed = new List<PlazaFurniturePlacement>(furniture);
            for (var i = 0; i < arrangement.Count; i++)
            {
                var item = arrangement[i];
                offset += item.FootprintRadius;
                var first = anchor.Position + tangent * offset;
                var second = center * 2f - first;
                var firstRotation = item.Kind == PlazaFurnitureKind.Bush
                    ? 0f : anchor.Rotation;
                var secondRotation = item.Kind == PlazaFurnitureKind.Bush
                    ? 0f : anchor.Rotation + math.PI;
                if (!FurniturePointValid(first, item.FootprintRadius,
                        centerpieces, polygon, entrances,
                        proposed, routes, item.Kind, arrangementId)
                    || !FurniturePointValid(second, item.FootprintRadius,
                        centerpieces, polygon,
                        entrances, proposed, routes, item.Kind,
                        arrangementId)) return;
                proposed.Add(new PlazaFurniturePlacement
                {
                    Kind = item.Kind, AssetName = item.AssetName,
                    ArrangementId = arrangementId,
                    FootprintRadius = item.FootprintRadius,
                    Position = first, Rotation = NormalizeAngle(firstRotation),
                    Size = item.Size,
                });
                proposed.Add(new PlazaFurniturePlacement
                {
                    Kind = item.Kind, AssetName = item.AssetName,
                    ArrangementId = arrangementId,
                    FootprintRadius = item.FootprintRadius,
                    Position = second, Rotation = NormalizeAngle(secondRotation),
                    Size = item.Size,
                });
                offset += item.FootprintRadius + 0.55f;
            }
            furniture.AddRange(proposed.GetRange(furniture.Count,
                proposed.Count - furniture.Count));
        }

        private static List<FurniturePairCandidate> BuildFurnitureCandidates(
            float2 center, float protectedRadius, PlazaLayoutMode mode, int seed)
        {
            var candidates = new List<FurniturePairCandidate>();
            var random = new Unity.Mathematics.Random((uint)math.max(1, seed));
            // One to six mirrored anchor pairs; each receives the entire
            // user-defined arrangement instead of a built-in furniture mix.
            var benchPairs = random.NextInt(1, 7);
            var phase = mode == PlazaLayoutMode.Axial
                ? random.NextInt(0, 4) * math.PI * 0.25f
                : random.NextFloat(0f, math.PI / benchPairs);
            // Keep the first furniture ring close to the centerpiece while
            // leaving the hidden pedestrian circulation ring unobstructed.
            var innerRadius = protectedRadius + random.NextFloat(3.8f, 5.2f);
            var stretch = mode == PlazaLayoutMode.Axial
                ? random.NextFloat(1f, 1.12f) : 1f;
            for (var pair = 0; pair < benchPairs; pair++)
            {
                var angle = phase + pair * math.PI / benchPairs;
                var direction = new float2(math.cos(angle), math.sin(angle));
                var elliptical = mode == PlazaLayoutMode.Axial
                    ? new float2(direction.x * stretch,
                        direction.y / stretch) : direction;
                var anchor = center + elliptical * innerRadius;
                AddPairAt(candidates, anchor, direction);
            }
            return candidates;
        }

        private static List<FurniturePairCandidate> BuildBoundaryFurnitureCandidates(
            float2 center, float protectedRadius, float2 majorAxis,
            float majorSpan, float minorSpan, int seed)
        {
            var candidates = new List<FurniturePairCandidate>();
            var random = new Unity.Mathematics.Random((uint)math.max(1, seed));
            var minorAxis = new float2(-majorAxis.y, majorAxis.x);
            var benchOffset = math.min(
                math.max(protectedRadius + 3.25f,
                    minorSpan * 0.5f - 2.7f),
                minorSpan * 0.5f - 2f);
            var pairsPerSide = math.clamp((int)(majorSpan / 16f), 1, 4);
            var halfLength = math.max(0f, majorSpan * 0.5f - 5f);
            var tangentSpacing = pairsPerSide == 1 ? 0f
                : halfLength * 2f / (pairsPerSide - 1);
            var side = random.NextBool() ? 1f : -1f;
            for (var i = 0; i < pairsPerSide; i++)
            {
                var along = pairsPerSide == 1 ? 0f
                    : -halfLength + i * tangentSpacing;
                var anchor = center + majorAxis * along
                    + minorAxis * (benchOffset * side);
                var facing = minorAxis * side;
                AddPairAt(candidates, anchor, facing);
            }
            return candidates;
        }

        private static float2 FindMajorAxis(IReadOnlyList<float2> polygon,
            float2 center, out float majorSpan, out float minorSpan)
        {
            var xx = 0f;
            var xy = 0f;
            var yy = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var delta = polygon[i] - center;
                xx += delta.x * delta.x;
                xy += delta.x * delta.y;
                yy += delta.y * delta.y;
            }
            var angle = 0.5f * math.atan2(2f * xy, xx - yy);
            var axis = new float2(math.cos(angle), math.sin(angle));
            var normal = new float2(-axis.y, axis.x);
            var minMajor = float.MaxValue;
            var maxMajor = float.MinValue;
            var minMinor = float.MaxValue;
            var maxMinor = float.MinValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var delta = polygon[i] - center;
                var major = math.dot(delta, axis);
                var minor = math.dot(delta, normal);
                minMajor = math.min(minMajor, major);
                maxMajor = math.max(maxMajor, major);
                minMinor = math.min(minMinor, minor);
                maxMinor = math.max(maxMinor, minor);
            }
            majorSpan = maxMajor - minMajor;
            minorSpan = maxMinor - minMinor;
            if (minorSpan > majorSpan)
            {
                var swap = majorSpan;
                majorSpan = minorSpan;
                minorSpan = swap;
                return normal;
            }
            return axis;
        }

        private static void AddPairAt(List<FurniturePairCandidate> candidates,
            float2 point, float2 facing)
        {
            candidates.Add(new FurniturePairCandidate
            {
                Position = point,
                Rotation = math.atan2(-facing.x, -facing.y),
            });
        }

        private static bool FurniturePointValid(float2 point, float footprintRadius,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaFurniturePlacement> accepted,
            IReadOnlyList<PlazaRoutingSegment> routes,
            PlazaFurnitureKind kind, int arrangementId)
        {
            var clearance = footprintRadius + FurnitureBoundaryClearance;
            if (!PointInsideOrBoundary(point, polygon)
                || DistanceToBoundarySquared(point, polygon) < clearance * clearance
                || DistanceToPointsSquared(point, entrances)
                    < EntranceFurnitureClearance * EntranceFurnitureClearance)
                return false;

            for (var i = 0; i < centerpieces.Count; i++)
            {
                var clearanceToCenter = centerpieces[i].Radius
                    + CenterSafetyMargin + footprintRadius + 0.75f;
                if (math.distancesq(point, centerpieces[i].Position)
                    < clearanceToCenter * clearanceToCenter) return false;
            }

            {
                // Physical props must not sit on the invisible walking net.
                // Keep a smaller margin for seats and bins than for planting.
                var routeClearance = footprintRadius
                    + (kind == PlazaFurnitureKind.Tree
                        || kind == PlazaFurnitureKind.Bush ? 0.75f : 0.15f);
                for (var i = 0; i < routes.Count; i++)
                    if (DistanceToSegmentSquared(point, routes[i].A,
                            routes[i].B) < routeClearance * routeClearance)
                        return false;
            }

            for (var i = 0; i < accepted.Count; i++)
            {
                var otherRadius = accepted[i].FootprintRadius > 0f
                    ? accepted[i].FootprintRadius
                    : FootprintRadius(accepted[i].Kind);
                var combined = footprintRadius + otherRadius
                    + (arrangementId != 0
                        && accepted[i].ArrangementId == arrangementId
                        ? 0.4f : 1.25f);
                if (math.distancesq(point, accepted[i].Position) < combined * combined)
                    return false;
            }
            return true;
        }

        private static bool BuildHiddenRoutingNetwork(
            List<PlazaRoutingSegment> output, IReadOnlyList<float2> polygon,
            IReadOnlyList<float2> entrances, float2 center, float protectedRadius,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            float2 majorAxis)
        {
            var circulationRadius = protectedRadius + CirculationOffset;
            List<float2> ring;
            if (centerpieces.Count > 1)
            {
                var extent = math.abs(math.dot(centerpieces[0].Position - center,
                    majorAxis));
                if (!TryBuildCapsuleRing(polygon, center, majorAxis, extent,
                    circulationRadius, centerpieces, out ring)) return false;
            }
            else if (!TryBuildRing(polygon, center, protectedRadius,
                    ref circulationRadius, out ring)) return false;

            for (var i = 0; i < ring.Count; i++)
            {
                var next = (i + 1) % ring.Count;
                if (SegmentInsidePolygon(ring[i], ring[next], polygon)
                    && SegmentAvoidsCenterpieces(ring[i], ring[next],
                        centerpieces))
                    AddUniqueSegment(output, ring[i], ring[next]);
            }

            if (entrances == null || entrances.Count == 0) return true;
            var nodes = new List<float2>();
            for (var i = 0; i < ring.Count; i++) nodes.Add(ring[i]);
            var ringNodeCount = ring.Count;
            for (var i = 0; i < polygon.Count; i++)
                AddUniquePoint(nodes, polygon[i]);
            var entranceNodeIds = new List<int>();
            for (var i = 0; i < entrances.Count; i++)
            {
                if (!PointInsideOrBoundary(entrances[i], polygon)) continue;
                entranceNodeIds.Add(FindOrAddPoint(nodes, entrances[i]));
            }
            if (entranceNodeIds.Count == 0) return true;

            var graph = BuildVisibilityGraph(nodes, polygon, centerpieces);
            for (var i = 0; i < entranceNodeIds.Count; i++)
            {
                var route = ShortestRouteToRing(entranceNodeIds[i], ringNodeCount,
                    graph, nodes);
                if (route.Count == 0) return false;
                for (var step = 0; step + 1 < route.Count; step++)
                    AddUniqueSegment(output, nodes[route[step]], nodes[route[step + 1]]);
            }
            return true;
        }

        private static bool TryBuildCapsuleRing(IReadOnlyList<float2> polygon,
            float2 center, float2 axis, float extent, float radius,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            out List<float2> ring)
        {
            ring = new List<float2>(14);
            var normal = new float2(-axis.y, axis.x);
            for (var i = 0; i <= 6; i++)
            {
                var angle = -math.PI * 0.5f + i * math.PI / 6f;
                ring.Add(center + axis * extent
                    + (axis * math.cos(angle) + normal * math.sin(angle)) * radius);
            }
            for (var i = 0; i <= 6; i++)
            {
                var angle = math.PI * 0.5f + i * math.PI / 6f;
                ring.Add(center - axis * extent
                    + (axis * math.cos(angle) + normal * math.sin(angle)) * radius);
            }
            for (var i = 0; i < ring.Count; i++)
            {
                var next = ring[(i + 1) % ring.Count];
                if (!PointInsideOrBoundary(ring[i], polygon)
                    || DistanceToBoundarySquared(ring[i], polygon) < 0.25f
                    || !SegmentInsidePolygon(ring[i], next, polygon)
                    || !SegmentAvoidsCenterpieces(ring[i], next, centerpieces))
                    return false;
            }
            return true;
        }

        private static bool SegmentAvoidsCenterpieces(float2 a, float2 b,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces)
        {
            for (var i = 0; i < centerpieces.Count; i++)
                if (!SegmentAvoidsCircle(a, b, centerpieces[i].Position,
                    centerpieces[i].Radius + CenterSafetyMargin + 0.8f))
                    return false;
            return true;
        }

        private static bool TryBuildRing(IReadOnlyList<float2> polygon,
            float2 center, float protectedRadius, ref float radius,
            out List<float2> ring)
        {
            ring = new List<float2>();
            var minimum = protectedRadius + 1.1f;
            while (radius >= minimum)
            {
                ring.Clear();
                var valid = true;
                for (var i = 0; i < CirculationNodeCount; i++)
                {
                    var angle = math.PI * 2f * i / CirculationNodeCount;
                    var point = center + new float2(math.cos(angle), math.sin(angle)) * radius;
                    if (!PointInsideOrBoundary(point, polygon)
                        || DistanceToBoundarySquared(point, polygon) < 0.25f)
                    { valid = false; break; }
                    ring.Add(point);
                }
                if (valid)
                {
                    for (var i = 0; i < ring.Count; i++)
                    {
                        var next = ring[(i + 1) % ring.Count];
                        if (!SegmentInsidePolygon(ring[i], next, polygon)
                            || !SegmentAvoidsCircle(ring[i], next, center,
                                protectedRadius + 0.8f))
                        { valid = false; break; }
                    }
                }
                if (valid) return true;
                radius -= 0.5f;
            }
            ring.Clear();
            return false;
        }

        private static List<int>[] BuildVisibilityGraph(IReadOnlyList<float2> nodes,
            IReadOnlyList<float2> polygon,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces)
        {
            var graph = new List<int>[nodes.Count];
            for (var i = 0; i < graph.Length; i++) graph[i] = new List<int>();
            for (var a = 0; a < nodes.Count; a++)
            for (var b = a + 1; b < nodes.Count; b++)
            {
                if (!SegmentInsidePolygon(nodes[a], nodes[b], polygon)
                    || !SegmentAvoidsCenterpieces(nodes[a], nodes[b],
                        centerpieces))
                    continue;
                graph[a].Add(b);
                graph[b].Add(a);
            }
            return graph;
        }

        private static List<int> ShortestRouteToRing(int start, int ringNodeCount,
            IReadOnlyList<List<int>> graph, IReadOnlyList<float2> nodes)
        {
            var distance = new float[nodes.Count];
            var previous = new int[nodes.Count];
            var visited = new bool[nodes.Count];
            for (var i = 0; i < distance.Length; i++)
            {
                distance[i] = float.MaxValue;
                previous[i] = -1;
            }
            distance[start] = 0f;
            for (var pass = 0; pass < nodes.Count; pass++)
            {
                var current = -1;
                var best = float.MaxValue;
                for (var i = 0; i < nodes.Count; i++)
                    if (!visited[i] && distance[i] < best)
                    { current = i; best = distance[i]; }
                if (current < 0) break;
                if (current < ringNodeCount)
                    return ReconstructRoute(current, previous);
                visited[current] = true;

                for (var edge = 0; edge < graph[current].Count; edge++)
                {
                    var next = graph[current][edge];
                    if (visited[next]) continue;
                    var candidate = best + math.distance(nodes[current], nodes[next]);
                    if (candidate >= distance[next]) continue;
                    distance[next] = candidate;
                    previous[next] = current;
                }
            }
            return new List<int>();
        }

        private static List<int> ReconstructRoute(int end, int[] previous)
        {
            var route = new List<int>();
            var current = end;
            var guard = previous.Length + 1;
            while (current >= 0 && guard-- > 0)
            {
                route.Add(current);
                current = previous[current];
            }
            route.Reverse();
            return route;
        }

        private static bool TryChooseCenter(IReadOnlyList<float2> polygon,
            float requiredClearance, out float2 center)
        {
            center = PolygonCentroid(polygon);
            if (PointInsideOrBoundary(center, polygon)
                && DistanceToBoundarySquared(center, polygon)
                    >= requiredClearance * requiredClearance)
                return true;

            var min = polygon[0];
            var max = polygon[0];
            for (var i = 1; i < polygon.Count; i++)
            {
                min = math.min(min, polygon[i]);
                max = math.max(max, polygon[i]);
            }

            var bestClearance = -1f;
            const int grid = 32;
            for (var y = 0; y <= grid; y++)
            for (var x = 0; x <= grid; x++)
            {
                var t = new float2((float)x / grid, (float)y / grid);
                var point = math.lerp(min, max, t);
                if (!PointInsideOrBoundary(point, polygon)) continue;
                var clearance = DistanceToBoundarySquared(point, polygon);
                if (clearance <= bestClearance) continue;
                bestClearance = clearance;
                center = point;
            }
            return bestClearance >= requiredClearance * requiredClearance;
        }

        private static float2 PolygonCentroid(IReadOnlyList<float2> polygon)
        {
            double twiceArea = 0.0;
            double x = 0.0;
            double y = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                var cross = (double)a.x * b.y - (double)b.x * a.y;
                twiceArea += cross;
                x += (a.x + b.x) * cross;
                y += (a.y + b.y) * cross;
            }
            if (Math.Abs(twiceArea) < 0.0001)
            {
                var average = float2.zero;
                for (var i = 0; i < polygon.Count; i++) average += polygon[i];
                return average / polygon.Count;
            }
            var divisor = 3.0 * twiceArea;
            return new float2((float)(x / divisor), (float)(y / divisor));
        }

        private static PlazaCenterpieceKind SelectCenterpiece(int seed)
        {
            unchecked
            {
                var value = (uint)seed;
                value ^= value >> 16;
                value *= 0x7feb352du;
                value ^= value >> 15;
                value *= 0x846ca68bu;
                value ^= value >> 16;
                return (value & 1u) == 0u
                    ? PlazaCenterpieceKind.Fountain : PlazaCenterpieceKind.Statue;
            }
        }

        private static float FootprintRadius(PlazaFurnitureKind kind)
        {
            switch (kind)
            {
                case PlazaFurnitureKind.Bench: return 1.35f;
                case PlazaFurnitureKind.Lamp: return 0.55f;
                default: return 0.5f;
            }
        }

        private static float NormalizeAngle(float angle)
        {
            var full = math.PI * 2f;
            while (angle < 0f) angle += full;
            while (angle >= full) angle -= full;
            return angle;
        }

        private static bool SegmentInsidePolygon(float2 a, float2 b,
            IReadOnlyList<float2> polygon)
        {
            var length = math.distance(a, b);
            var steps = math.max(1, (int)Math.Ceiling(length / RoutingSampleSpacing));
            for (var i = 0; i <= steps; i++)
                if (!PointInsideOrBoundary(math.lerp(a, b, (float)i / steps), polygon))
                    return false;
            return true;
        }

        private static bool SegmentAvoidsCircle(float2 a, float2 b,
            float2 center, float radius)
            => DistanceToSegmentSquared(center, a, b) >= radius * radius;

        private static bool PointInsideOrBoundary(float2 point,
            IReadOnlyList<float2> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[j];
                var b = polygon[i];
                if (DistanceToSegmentSquared(point, a, b) < 0.01f) return true;
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                       / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private static float DistanceToBoundarySquared(float2 point,
            IReadOnlyList<float2> polygon)
        {
            var nearest = float.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
                nearest = math.min(nearest, DistanceToSegmentSquared(point,
                    polygon[i], polygon[(i + 1) % polygon.Count]));
            return nearest;
        }

        private static float DistanceToPointsSquared(float2 point,
            IReadOnlyList<float2> points)
        {
            if (points == null || points.Count == 0) return float.MaxValue;
            var nearest = float.MaxValue;
            for (var i = 0; i < points.Count; i++)
                nearest = math.min(nearest, math.distancesq(point, points[i]));
            return nearest;
        }

        private static float DistanceToSegmentSquared(float2 point, float2 a, float2 b)
        {
            var ab = b - a;
            var length = math.lengthsq(ab);
            if (length < 0.0001f) return math.distancesq(point, a);
            var t = math.clamp(math.dot(point - a, ab) / length, 0f, 1f);
            return math.distancesq(point, a + ab * t);
        }

        private static void AddUniqueSegment(List<PlazaRoutingSegment> segments,
            float2 a, float2 b)
        {
            if (math.distancesq(a, b) < 0.01f) return;
            for (var i = 0; i < segments.Count; i++)
            {
                var existing = segments[i];
                if (math.distancesq(existing.A, a) < 0.01f
                        && math.distancesq(existing.B, b) < 0.01f
                    || math.distancesq(existing.A, b) < 0.01f
                        && math.distancesq(existing.B, a) < 0.01f)
                    return;
            }
            segments.Add(new PlazaRoutingSegment { A = a, B = b });
        }

        private static void AddUniquePoint(List<float2> points, float2 point)
        {
            for (var i = 0; i < points.Count; i++)
                if (math.distancesq(points[i], point) < 0.01f) return;
            points.Add(point);
        }

        private static int FindOrAddPoint(List<float2> points, float2 point)
        {
            for (var i = 0; i < points.Count; i++)
                if (math.distancesq(points[i], point) < 0.01f) return i;
            points.Add(point);
            return points.Count - 1;
        }

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private struct FurniturePairCandidate
        {
            internal float2 Position;
            internal float Rotation;
        }
    }
}
