using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Builds plaza furnishing plans for a walkable polygon. Boundary layouts
    /// follow its edges and mirror groups only when the outline has an axis.
    /// All positions use the local XZ planning plane.
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
        /// Uses the same centerpiece clearance as Generate, so the
        /// chooser never offers a centerpiece that cannot fit the polygon.
        /// </summary>
        internal static bool CanFitCenterpiece(IReadOnlyList<float2> polygon,
            float centerFootprintRadius)
        {
            if (polygon == null || polygon.Count < 3
                || !IsFinite(centerFootprintRadius)) return false;
            var radius = math.max(2f, centerFootprintRadius);
            if (!TryChooseCenter(polygon, radius, out var center)) return false;
            return DistanceToBoundarySquared(center, polygon)
                >= (radius + CenterSafetyMargin) * (radius + CenterSafetyMargin);
        }

        /// <summary>
        /// Generates a deterministic plaza inside polygon. Furniture is added
        /// only in opposite pairs whose two footprints fit. The returned
        /// Access is supplied by the navigation area, not a hidden path ring.
        /// </summary>
        internal static PlazaPlan Generate(IReadOnlyList<float2> polygon,
            IReadOnlyList<float2> entrances, float centerFootprintRadius,
            PlazaCenterPlacementMode centerPlacement,
            PlazaArrangementPlacementMode arrangementPlacement,
            float centerpieceSpacing, float arrangementSpacing, int density,
            int seed, bool includeCenterpiece = true,
            IReadOnlyList<PlazaArrangementItem> arrangement = null)
        {
            var furniture = new List<PlazaFurniturePlacement>();
            var routes = new List<PlazaRoutingSegment>();
            var centerpieces = new List<PlazaCenterpiecePlacement>();
            centerpieceSpacing = math.clamp(centerpieceSpacing, 5f, 60f);
            arrangementSpacing = math.clamp(arrangementSpacing, 0f, 20f);
            density = math.clamp(density, 25, 200);
            if (polygon == null || polygon.Count < 3)
                return new PlazaPlan(seed, centerPlacement, arrangementPlacement,
                    centerpieceSpacing, arrangementSpacing, centerpieces,
                    furniture, routes);

            var radius = includeCenterpiece
                && IsFinite(centerFootprintRadius)
                ? math.max(0f, centerFootprintRadius) : 0f;
            float2 center;
            if (!TryChooseCenter(polygon, radius, out center))
            {
                // The selected centerpiece is optional. Keep the polygon
                // buildable when it does not fit a narrow/concave footprint.
                radius = 0f;
                if (!TryChooseCenter(polygon, 0f, out center))
                    return new PlazaPlan(seed, centerPlacement,
                        arrangementPlacement, centerpieceSpacing,
                        arrangementSpacing, centerpieces, furniture, routes);
            }

            var protectedRadius = radius > 0f
                ? math.max(2f, radius) + CenterSafetyMargin : 0.75f;
            var majorAxis = FindMajorAxis(polygon, center, out var majorSpan,
                out var minorSpan);
            if (radius > 0f)
            {
                var count = centerPlacement == PlazaCenterPlacementMode.Centered
                    ? 1 : centerPlacement == PlazaCenterPlacementMode.Mirrored
                        ? 2 : 3;
                var step = centerPlacement == PlazaCenterPlacementMode.Mirrored
                    ? math.max(radius * 2f + 2f, centerpieceSpacing) * 0.5f
                    : math.max(radius * 2f + 2f, centerpieceSpacing);
                var positions = new List<float2>(count);
                if (count == 1) positions.Add(center);
                else if (count == 2)
                {
                    positions.Add(center - majorAxis * step);
                    positions.Add(center + majorAxis * step);
                }
                else
                {
                    positions.Add(center - majorAxis * step);
                    positions.Add(center);
                    positions.Add(center + majorAxis * step);
                }
                var allFit = true;
                for (var i = 0; i < positions.Count; i++)
                {
                    var point = positions[i];
                    if (PointInsideOrBoundary(point, polygon)
                        && DistanceToBoundarySquared(point, polygon)
                            >= (radius + CenterSafetyMargin)
                            * (radius + CenterSafetyMargin)) continue;
                    allFit = false;
                    break;
                }
                if (!allFit && count > 1) positions = new List<float2> { center };
                for (var i = 0; i < positions.Count; i++)
                    centerpieces.Add(new PlazaCenterpiecePlacement
                    {
                        Kind = PlazaCenterpieceKind.Fountain,
                        Position = positions[i],
                        Radius = radius,
                    });
            }

            var maximumRadius = 1.35f;
            var arrangementWidth = 0f;
            if (arrangement != null)
            {
                for (var i = 0; i < arrangement.Count; i++)
                {
                    maximumRadius = math.max(maximumRadius,
                        arrangement[i].FootprintRadius);
                    arrangementWidth += arrangement[i].FootprintRadius * 2f;
                }
                arrangementWidth += math.max(0, arrangement.Count - 1) * 0.55f;
            }
            var alongBoundary = arrangementPlacement
                == PlazaArrangementPlacementMode.AlongBoundary;
            var candidates = alongBoundary
                ? BuildBoundaryFurnitureCandidates(polygon, arrangementSpacing,
                    arrangementWidth, maximumRadius)
                : new List<FurniturePairCandidate>();
            var mirrorNormal = float2.zero;
            var mirrorOffset = 0f;
            var mirroredBoundary = alongBoundary
                && TryFindBoundaryMirrorAxis(polygon,
                    out mirrorNormal, out mirrorOffset);
            var desiredPairs = alongBoundary
                ? math.clamp((int)math.round(4f * density / 100f), 1, 8)
                : DesiredCircularPairs(density);
            if (arrangement != null && arrangement.Count > 0)
                desiredPairs = math.min(desiredPairs,
                    math.max(1, MaximumFurniture / (arrangement.Count * 2)));
            if (arrangement != null && arrangement.Count > 0)
            {
                if (!alongBoundary)
                {
                    BuildCircularArrangements(furniture, center,
                        protectedRadius, arrangementSpacing,
                        arrangementWidth, maximumRadius, majorSpan, minorSpan,
                        desiredPairs, arrangement, centerpieces, polygon,
                        entrances, routes);
                    return new PlazaPlan(seed, centerPlacement,
                        arrangementPlacement, centerpieceSpacing,
                        arrangementSpacing, centerpieces, furniture, routes);
                }
                var groupSize = arrangement.Count
                    * (mirroredBoundary ? 2 : 1);
                var desiredGroups = !mirroredBoundary
                    ? desiredPairs * 2 : desiredPairs;
                desiredGroups = math.min(desiredGroups,
                    MaximumFurniture / groupSize);
                var acceptedGroups = 0;
                for (var i = 0; i < candidates.Count
                    && acceptedGroups < desiredGroups; i++)
                {
                    if (furniture.Count + groupSize > MaximumFurniture)
                        break;
                    var added = TryAddBoundaryArrangement(furniture,
                        candidates[i], arrangement, centerpieces, polygon,
                        entrances, routes, arrangementSpacing, i + 1,
                        mirroredBoundary, mirrorNormal, mirrorOffset);
                    if (added) acceptedGroups++;
                }
            }
            return new PlazaPlan(seed, centerPlacement, arrangementPlacement,
                centerpieceSpacing, arrangementSpacing, centerpieces, furniture,
                routes);
        }

        private static bool TryAddArrangement(
            List<PlazaFurniturePlacement> furniture,
            FurniturePairCandidate anchor,
            IReadOnlyList<PlazaArrangementItem> arrangement,
            float2 center,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaRoutingSegment> routes,
            PlazaArrangementPlacementMode placementMode, float spacing,
            int arrangementId)
        {
            var tangent = anchor.Tangent;
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
                        proposed, routes, item.Kind, arrangementId,
                        placementMode, spacing)
                    || !FurniturePointValid(second, item.FootprintRadius,
                        centerpieces, polygon,
                        entrances, proposed, routes, item.Kind,
                        arrangementId, placementMode, spacing)) return false;
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
            return true;
        }

        private static bool TryAddBoundaryArrangement(
            List<PlazaFurniturePlacement> furniture,
            FurniturePairCandidate anchor,
            IReadOnlyList<PlazaArrangementItem> arrangement,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaRoutingSegment> routes, float spacing,
            int arrangementId, bool mirrored, float2 mirrorNormal,
            float mirrorOffset)
        {
            var width = 0f;
            for (var i = 0; i < arrangement.Count; i++)
                width += arrangement[i].FootprintRadius * 2f;
            width += (arrangement.Count - 1) * 0.55f;
            var offset = -width * 0.5f;
            var proposed = new List<PlazaFurniturePlacement>(furniture);
            var forward = new float2(math.sin(anchor.Rotation),
                math.cos(anchor.Rotation));
            var reflectedForward = mirrored
                ? ReflectDirection(forward, mirrorNormal) : float2.zero;
            for (var i = 0; i < arrangement.Count; i++)
            {
                var item = arrangement[i];
                offset += item.FootprintRadius;
                var first = anchor.Position + anchor.Tangent * offset;
                if (!FurniturePointValid(first, item.FootprintRadius,
                        centerpieces, polygon, entrances, proposed, routes,
                        item.Kind, arrangementId,
                        PlazaArrangementPlacementMode.AlongBoundary, spacing))
                    return false;
                proposed.Add(new PlazaFurniturePlacement
                {
                    Kind = item.Kind, AssetName = item.AssetName,
                    ArrangementId = arrangementId,
                    FootprintRadius = item.FootprintRadius,
                    Position = first,
                    Rotation = item.Kind == PlazaFurnitureKind.Bush ? 0f
                        : NormalizeAngle(anchor.Rotation),
                    Size = item.Size,
                });
                if (mirrored)
                {
                    var second = ReflectPoint(first, mirrorNormal, mirrorOffset);
                    if (!FurniturePointValid(second, item.FootprintRadius,
                            centerpieces, polygon, entrances, proposed, routes,
                            item.Kind, arrangementId,
                            PlazaArrangementPlacementMode.AlongBoundary, spacing))
                        return false;
                    proposed.Add(new PlazaFurniturePlacement
                    {
                        Kind = item.Kind, AssetName = item.AssetName,
                        ArrangementId = arrangementId,
                        FootprintRadius = item.FootprintRadius,
                        Position = second,
                        Rotation = item.Kind == PlazaFurnitureKind.Bush ? 0f
                            : NormalizeAngle(math.atan2(reflectedForward.x,
                                reflectedForward.y)),
                        Size = item.Size,
                    });
                }
                offset += item.FootprintRadius + 0.55f;
            }
            furniture.AddRange(proposed.GetRange(furniture.Count,
                proposed.Count - furniture.Count));
            return true;
        }

        private static int DesiredCircularPairs(int density)
        {
            // The UI's 25–200% steps correspond to 180°, 90°, 60°, 45°, 30°,
            // 22.5°, 18° and 15° between complete arrangements.
            switch (math.clamp((density + 12) / 25, 1, 8))
            {
                case 1: return 1;
                case 2: return 2;
                case 3: return 3;
                case 4: return 4;
                case 5: return 6;
                case 6: return 8;
                case 7: return 10;
                default: return 12;
            }
        }

        private static void BuildCircularArrangements(
            List<PlazaFurniturePlacement> furniture, float2 center,
            float protectedRadius, float spacing, float arrangementWidth,
            float maximumRadius, float majorSpan, float minorSpan,
            int desiredPairs, IReadOnlyList<PlazaArrangementItem> arrangement,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaRoutingSegment> routes)
        {
            var innerRadius = protectedRadius + spacing + maximumRadius + 0.5f;
            var availableRadius = math.min(majorSpan, minorSpan) * 0.5f
                - arrangementWidth * 0.5f - maximumRadius;
            var ringStep = math.max(8f, arrangementWidth + 4f);
            // Several centerpieces on the main axis are enclosed by a
            // stadium; a single centerpiece degenerates it to a circle.
            var axis = new float2(1f, 0f);
            var extent = 0f;
            if (centerpieces.Count > 1)
            {
                var span = centerpieces[centerpieces.Count - 1].Position
                    - centerpieces[0].Position;
                extent = math.length(span) * 0.5f;
                if (extent > 0.001f) axis = span / (extent * 2f);
                else extent = 0f;
            }
            // Try a complete, evenly distributed ring. When capacity or
            // geometry rules one out, recompute every position for fewer
            // pairs instead of keeping a prefix of a denser ring.
            for (var pairs = desiredPairs; pairs >= 1; pairs--)
            {
                for (var ring = 0; ring < 3; ring++)
                {
                    var distance = innerRadius + ring * ringStep;
                    if (distance > availableRadius + arrangementWidth * 0.5f)
                        break;
                    // Point-mirrored partners lie half a perimeter apart.
                    var pairStep = (math.PI * distance + extent * 2f) / pairs;
                    for (var phase = 0; phase < 8; phase++)
                    {
                        var trial = new List<PlazaFurniturePlacement>();
                        for (var pair = 0; pair < pairs; pair++)
                        {
                            var position = StadiumPoint(center, axis, extent,
                                distance, (pair + phase / 8f) * pairStep,
                                out var outward);
                            var candidate = new FurniturePairCandidate
                            {
                                Position = position,
                                Rotation = math.atan2(-outward.x,
                                    -outward.y),
                                Tangent = new float2(-outward.y, outward.x),
                            };
                            if (!TryAddArrangement(trial, candidate, arrangement,
                                    center, centerpieces, polygon, entrances,
                                    routes,
                                    PlazaArrangementPlacementMode.AroundCenter,
                                    spacing, pair + 1)) break;
                        }
                        if (trial.Count != pairs * arrangement.Count * 2)
                            continue;
                        furniture.AddRange(trial);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Returns the point at arc length s (counter-clockwise, starting at
        /// the +axis tip) on the stadium of half straight length extent and
        /// cap radius radius. The perimeter is point-symmetric around center,
        /// so s and s + perimeter / 2 are mirror partners.
        /// </summary>
        private static float2 StadiumPoint(float2 center, float2 axis,
            float extent, float radius, float s, out float2 outward)
        {
            var normal = new float2(-axis.y, axis.x);
            var quarterArc = math.PI * 0.5f * radius;
            var straight = extent * 2f;
            float angle;
            float side;
            if (s < quarterArc)
            {
                angle = s / radius;
                side = 1f;
            }
            else if ((s -= quarterArc) < straight)
            {
                outward = normal;
                return center + axis * (extent - s) + normal * radius;
            }
            else if ((s -= straight) < quarterArc * 2f)
            {
                angle = math.PI * 0.5f + s / radius;
                side = -1f;
            }
            else if ((s -= quarterArc * 2f) < straight)
            {
                outward = -normal;
                return center + axis * (s - extent) - normal * radius;
            }
            else
            {
                angle = math.PI * 1.5f + (s - straight) / radius;
                side = 1f;
            }
            outward = axis * math.cos(angle) + normal * math.sin(angle);
            return center + axis * (extent * side) + outward * radius;
        }

        private static List<FurniturePairCandidate> BuildBoundaryFurnitureCandidates(
            IReadOnlyList<float2> polygon, float spacing,
            float arrangementWidth, float maximumRadius)
        {
            var candidates = new List<FurniturePairCandidate>();
            var signedArea = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                signedArea += a.x * b.y - b.x * a.y;
            }
            if (math.abs(signedArea) < 0.01f) return candidates;
            var edgeCandidates = new List<FurniturePairCandidate>[polygon.Count];
            var maximumPerEdge = 0;
            var endMargin = arrangementWidth * 0.5f + maximumRadius + 0.5f;
            var groupSpacing = math.max(10f,
                arrangementWidth + maximumRadius * 2f + 1.25f);
            for (var edgeIndex = 0; edgeIndex < polygon.Count; edgeIndex++)
            {
                var a = polygon[edgeIndex];
                var edge = polygon[(edgeIndex + 1) % polygon.Count] - a;
                var length = math.length(edge);
                if (length < endMargin * 2f) continue;
                var tangent = edge / length;
                var inward = signedArea > 0f
                    ? new float2(-tangent.y, tangent.x)
                    : new float2(tangent.y, -tangent.x);
                var count = math.clamp((int)math.floor(
                    (length - endMargin * 2f) / groupSpacing) + 1, 1, 8);
                var start = (length - (count - 1) * groupSpacing) * 0.5f;
                var edgeList = new List<FurniturePairCandidate>(count);
                for (var i = 0; i < count; i++)
                    edgeList.Add(new FurniturePairCandidate
                    {
                        Position = a + tangent * (start + i * groupSpacing)
                            + inward * (spacing + maximumRadius),
                        Rotation = math.atan2(inward.x, inward.y),
                        Tangent = tangent,
                    });
                edgeCandidates[edgeIndex] = edgeList;
                maximumPerEdge = math.max(maximumPerEdge, count);
            }
            // Visit every side before taking a second position on a long side.
            for (var slot = 0; slot < maximumPerEdge; slot++)
                for (var edge = 0; edge < edgeCandidates.Length; edge++)
                    if (edgeCandidates[edge] != null
                        && slot < edgeCandidates[edge].Count)
                        candidates.Add(edgeCandidates[edge][slot]);
            return candidates;
        }

        private static bool TryFindBoundaryMirrorAxis(
            IReadOnlyList<float2> polygon,
            out float2 normal, out float offset)
        {
            // The planner's chosen centerpiece can be off-axis in concave
            // plazas. Symmetry must be measured around the polygon itself.
            var mean = float2.zero;
            for (var i = 0; i < polygon.Count; i++) mean += polygon[i];
            mean /= polygon.Count;
            var xx = 0f;
            var xy = 0f;
            var yy = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var delta = polygon[i] - mean;
                xx += delta.x * delta.x;
                xy += delta.x * delta.y;
                yy += delta.y * delta.y;
            }
            var angle = 0.5f * math.atan2(2f * xy, xx - yy);
            var majorAxis = new float2(math.cos(angle), math.sin(angle));
            var perpendicular = new float2(-majorAxis.y, majorAxis.x);
            if (PolygonMirrorsAcross(polygon, perpendicular, out offset))
            {
                normal = perpendicular;
                return true;
            }
            if (PolygonMirrorsAcross(polygon, majorAxis, out offset))
            {
                normal = majorAxis;
                return true;
            }
            // A regular polygon can have equal principal moments even when
            // its mirror axes are rotated. Every polygon mirror axis passes
            // through a vertex or an edge midpoint and the vertex mean.
            for (var i = 0; i < polygon.Count; i++)
            {
                var vertexDirection = polygon[i] - mean;
                if (math.lengthsq(vertexDirection) > 0.0001f)
                {
                    normal = math.normalizesafe(new float2(
                        -vertexDirection.y, vertexDirection.x));
                    if (PolygonMirrorsAcross(polygon, normal, out offset))
                        return true;
                }
                var midpoint = (polygon[i]
                    + polygon[(i + 1) % polygon.Count]) * 0.5f;
                var midpointDirection = midpoint - mean;
                if (math.lengthsq(midpointDirection) <= 0.0001f) continue;
                normal = math.normalizesafe(new float2(
                    -midpointDirection.y, midpointDirection.x));
                if (PolygonMirrorsAcross(polygon, normal, out offset))
                    return true;
            }
            normal = float2.zero;
            offset = 0f;
            return false;
        }

        private static bool PolygonMirrorsAcross(IReadOnlyList<float2> polygon,
            float2 normal, out float offset)
        {
            var minimum = float.MaxValue;
            var maximum = float.MinValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var projection = math.dot(polygon[i], normal);
                minimum = math.min(minimum, projection);
                maximum = math.max(maximum, projection);
            }
            offset = (minimum + maximum) * 0.5f;
            const float toleranceSquared = 0.01f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = ReflectPoint(polygon[i], normal, offset);
                var b = ReflectPoint(polygon[(i + 1) % polygon.Count],
                    normal, offset);
                var matchingEdge = false;
                for (var j = 0; j < polygon.Count; j++)
                {
                    if (math.distancesq(a, polygon[(j + 1) % polygon.Count])
                            > toleranceSquared
                        || math.distancesq(b, polygon[j]) > toleranceSquared)
                        continue;
                    matchingEdge = true;
                    break;
                }
                if (!matchingEdge) return false;
            }
            return true;
        }

        private static float2 ReflectPoint(float2 point, float2 normal,
            float offset)
            => point - normal * (2f * (math.dot(point, normal) - offset));

        private static float2 ReflectDirection(float2 direction, float2 normal)
            => direction - normal * (2f * math.dot(direction, normal));

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

        private static bool FurniturePointValid(float2 point, float footprintRadius,
            IReadOnlyList<PlazaCenterpiecePlacement> centerpieces,
            IReadOnlyList<float2> polygon, IReadOnlyList<float2> entrances,
            IReadOnlyList<PlazaFurniturePlacement> accepted,
            IReadOnlyList<PlazaRoutingSegment> routes,
            PlazaFurnitureKind kind, int arrangementId,
            PlazaArrangementPlacementMode placementMode, float spacing)
        {
            var boundaryGap = placementMode
                    == PlazaArrangementPlacementMode.AlongBoundary
                ? spacing : FurnitureBoundaryClearance;
            var clearance = footprintRadius + boundaryGap;
            if (!PointInsideOrBoundary(point, polygon)
                || DistanceToBoundarySquared(point, polygon) < clearance * clearance
                || DistanceToPointsSquared(point, entrances)
                    < EntranceFurnitureClearance * EntranceFurnitureClearance)
                return false;

            for (var i = 0; i < centerpieces.Count; i++)
            {
                var clearanceToCenter = centerpieces[i].Radius
                    + CenterSafetyMargin + footprintRadius
                    + (placementMode == PlazaArrangementPlacementMode.AroundCenter
                        ? spacing : 0.75f);
                if (math.distancesq(point, centerpieces[i].Position)
                    < clearanceToCenter * clearanceToCenter) return false;
            }

            {
                // Kept for compatibility with older routing-only plans. New
                // plazas pass an empty list because their whole area is walkable.
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
            internal float2 Tangent;
        }
    }
}
