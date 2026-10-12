using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Composes a plaza as an urban space instead of filling a polygon with
    /// objects: analyse the geometry, choose a style, fit a coherent main
    /// plaza from exact primitives, open it to the entrances, and only then
    /// derive green zones, axes, focal points, tree rows and seating from
    /// that structure. Deterministic for equal inputs and seed.
    /// </summary>
    internal static partial class PlazaComposer
    {
        private sealed class StyleRules
        {
            internal PlazaCoreShape[] Shapes;
            /// <summary>Target and minimum share of the plot for the main plaza.</summary>
            internal float TargetShare;
            internal float MinimumShare;
            /// <summary>Free rim between plaza and boundary, as share of the minor span.</summary>
            internal float RimShare;
            internal float MinimumRim;
            internal float MaximumRim;
            internal float CorridorWidth;
            internal float TreeSpacing;
            internal int MaximumSecondarySpaces;
            internal bool OrganicGreen;
        }

        private static readonly Dictionary<PlazaStyle, StyleRules> Rules =
            new Dictionary<PlazaStyle, StyleRules>
            {
                [PlazaStyle.Classic] = new StyleRules
                {
                    Shapes = new[] { PlazaCoreShape.Rectangle,
                        PlazaCoreShape.ChamferedRectangle, PlazaCoreShape.Ellipse,
                        PlazaCoreShape.BoundaryAligned },
                    TargetShare = 0.46f, MinimumShare = 0.24f,
                    RimShare = 0.13f, MinimumRim = 3f, MaximumRim = 12f,
                    CorridorWidth = 5f, TreeSpacing = 7.5f,
                    MaximumSecondarySpaces = 0, OrganicGreen = false,
                },
                [PlazaStyle.Modern] = new StyleRules
                {
                    Shapes = new[] { PlazaCoreShape.Ellipse,
                        PlazaCoreShape.ConvexPolygon, PlazaCoreShape.ChamferedRectangle },
                    TargetShare = 0.42f, MinimumShare = 0.22f,
                    RimShare = 0.1f, MinimumRim = 2f, MaximumRim = 10f,
                    CorridorWidth = 4f, TreeSpacing = 6f,
                    MaximumSecondarySpaces = 1, OrganicGreen = true,
                },
                [PlazaStyle.Promenade] = new StyleRules
                {
                    Shapes = new[] { PlazaCoreShape.ChamferedRectangle,
                        PlazaCoreShape.Rectangle },
                    TargetShare = 0.4f, MinimumShare = 0.2f,
                    RimShare = 0.16f, MinimumRim = 3f, MaximumRim = 10f,
                    CorridorWidth = 5f, TreeSpacing = 7f,
                    MaximumSecondarySpaces = 0, OrganicGreen = false,
                },
                [PlazaStyle.Sequence] = new StyleRules
                {
                    Shapes = new[] { PlazaCoreShape.Ellipse,
                        PlazaCoreShape.ChamferedRectangle, PlazaCoreShape.ConvexPolygon },
                    TargetShare = 0.3f, MinimumShare = 0.14f,
                    RimShare = 0.1f, MinimumRim = 2.5f, MaximumRim = 9f,
                    CorridorWidth = 4f, TreeSpacing = 6.5f,
                    MaximumSecondarySpaces = 2, OrganicGreen = true,
                },
            };

        /// <summary>Plaza share of a secondary space.</summary>
        private const float SecondaryTargetShare = 0.1f;
        private const float MinimumCoreWidth = 6f;
        private const float GreenGap = 1.5f;
        private const float EntranceClearance = 4f;
        private const float MinimumGreenArea = 20f;

        /// <summary>Composes a plaza design; check <see cref="PlazaDesign.Succeeded"/>.</summary>
        internal static PlazaDesign Compose(IReadOnlyList<float2> polygon,
            IReadOnlyList<float2> entrances, PlazaStyle style, int seed)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var design = new PlazaDesign { RequestedStyle = style, Seed = seed };
            var geometry = PlazaGeometry.Analyze(polygon);
            if (geometry == null)
            {
                design.Failure = "invalid polygon";
                return design;
            }
            var movement = new PlazaMovement(geometry, entrances);
            var metrics = design.Metrics;
            metrics.PolygonArea = geometry.Area;
            metrics.Compactness = geometry.Compactness;
            metrics.Convexity = geometry.Convexity;
            metrics.Elongation = geometry.Elongation;
            metrics.CellSize = geometry.CellSize;
            RateStyles(geometry, movement, metrics.StyleSuitability);

            // Auto picks the best-suited style; a style whose core cannot fit
            // falls back to the next suitable one instead of failing.
            var order = new List<PlazaStyle>();
            if (style != PlazaStyle.Auto) order.Add(style);
            var ranked = new List<PlazaStyle>(metrics.StyleSuitability.Keys);
            ranked.Sort((a, b) => metrics.StyleSuitability[b]
                .CompareTo(metrics.StyleSuitability[a]));
            foreach (var candidate in ranked)
                if (!order.Contains(candidate)) order.Add(candidate);

            foreach (var candidate in order)
            {
                if (!TryFitCore(geometry, movement, candidate, seed,
                        out var core, out var coreScore)) continue;
                design.Style = candidate;
                design.CoreShape = core.Kind;
                design.MainPlaza.AddRange(core.Outline());
                metrics.CoreScore = coreScore;
                ComposeAround(design, geometry, movement, core, Rules[candidate], seed);
                break;
            }
            if (design.MainPlaza.Count < 3)
                design.Failure = "no main plaza fits the outline";

            metrics.MainPlazaArea = PlazaShape.Area(design.MainPlaza);
            metrics.MainPlazaMinimumWidth = design.MainPlaza.Count >= 3
                ? PlazaShape.MinimumWidth(design.MainPlaza) : 0f;
            for (var i = 0; i < design.SecondarySpaces.Count; i++)
                metrics.SecondaryArea += PlazaShape.Area(design.SecondarySpaces[i]);
            for (var i = 0; i < design.GreenZones.Count; i++)
                metrics.GreenArea += PlazaShape.Area(design.GreenZones[i]);
            timer.Stop();
            metrics.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return design;
        }

        /// <summary>
        /// Suitability 0–1 of each style for the geometry and entrances.
        /// </summary>
        private static void RateStyles(PlazaGeometry geometry, PlazaMovement movement,
            Dictionary<PlazaStyle, float> result)
        {
            // A square has compactness π/4; treat that as fully compact.
            var compact = math.saturate(geometry.Compactness / 0.785f);
            var elongation = math.saturate((geometry.Elongation - 1f) / 1.5f);
            var concave = math.saturate((0.93f - geometry.Convexity) / 0.3f);
            var rooms = geometry.RoomCentres(geometry.MaximumDistance * 0.45f,
                geometry.MaximumDistance * 2.2f).Count;

            // Entrances far apart along the main axis favour a promenade.
            var spread = 0f;
            for (var i = 0; i < movement.Entrances.Count; i++)
            for (var j = i + 1; j < movement.Entrances.Count; j++)
                spread = math.max(spread, math.abs(math.dot(movement.Entrances[i]
                    - movement.Entrances[j], geometry.MajorAxis)));
            spread = math.saturate(spread / math.max(1f, geometry.MajorSpan));

            // Desire lines across the main axis favour a modern diagonal layout.
            var diagonal = 0f;
            for (var i = 0; i < movement.DesireLines.Count; i++)
            {
                var line = movement.DesireLines[i];
                var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                var cosine = math.abs(math.dot(direction, geometry.MajorAxis));
                // 1 at 45°, 0 when parallel or perpendicular to the axis.
                diagonal = math.max(diagonal, 1f - math.abs(cosine * cosine * 2f - 1f));
            }

            result[PlazaStyle.Classic] = 0.45f * compact
                + 0.3f * math.saturate((geometry.Convexity - 0.7f) / 0.3f)
                + 0.25f * (1f - elongation);
            result[PlazaStyle.Promenade] = math.saturate((geometry.Elongation - 1.6f)
                / 1.4f) * 0.7f + 0.3f * spread * (geometry.Elongation > 1.6f ? 1f : 0.2f);
            result[PlazaStyle.Sequence] = 0.6f * concave
                + 0.4f * (rooms >= 2 ? 1f : 0f);
            result[PlazaStyle.Modern] = 0.3f + 0.3f * concave * (1f - compact)
                + 0.3f * diagonal;
        }

        // ------------------------------------------------------ main plaza

        /// <summary>
        /// Searches orientations, centres, proportions and primitives for the
        /// best main plaza of a style. Every candidate must lie inside the
        /// polygon with the style's rim and keep a minimum width.
        /// </summary>
        private static bool TryFitCore(PlazaGeometry geometry, PlazaMovement movement,
            PlazaStyle style, int seed, out PlazaShape best, out float bestScore)
        {
            var rules = Rules[style];
            best = default;
            bestScore = float.MinValue;
            // Usable width is the widest inscribed circle, not the bounding
            // box: an L-shaped plot is only as wide as its arms.
            var usable = geometry.MaximumDistance * 2f;
            var minimumWidth = math.clamp(usable * 0.45f, MinimumCoreWidth, usable * 0.8f);
            var rim = math.clamp(usable * rules.RimShare, rules.MinimumRim,
                rules.MaximumRim);
            rim = math.max(0.5f, math.min(rim, (usable - minimumWidth) * 0.5f));

            var scored = new List<(PlazaShape shape, float score)>();
            foreach (var kind in rules.Shapes)
            {
                if ((kind == PlazaCoreShape.BoundaryAligned
                        || kind == PlazaCoreShape.ConvexPolygon)
                    && geometry.Convexity < 0.95f) continue;
                foreach (var axis in CandidateAxes(geometry, movement, style))
                foreach (var center in CandidateCentres(geometry, movement, style))
                foreach (var aspect in CandidateAspects(geometry, style, kind))
                {
                    var shape = new PlazaShape
                    {
                        Kind = kind, Center = center, Axis = axis,
                    };
                    if (kind == PlazaCoreShape.BoundaryAligned
                        || kind == PlazaCoreShape.ConvexPolygon)
                    {
                        shape.Template = kind == PlazaCoreShape.BoundaryAligned
                            ? geometry.Polygon : geometry.Hull;
                        shape.TemplateAnchor = center;
                    }
                    if (!GrowToFit(geometry, ref shape, aspect, rim,
                            rules.TargetShare)) continue;
                    var outline = shape.Outline();
                    var area = PlazaShape.Area(outline);
                    if (area < geometry.Area * rules.MinimumShare) continue;
                    if (PlazaShape.MinimumWidth(outline) < minimumWidth) continue;
                    scored.Add((shape, ScoreCore(geometry, movement, style, shape,
                        outline, area, rules.TargetShare)));
                }
            }
            if (scored.Count == 0) return false;

            // Near-equal candidates are a matter of taste: the seed decides.
            scored.Sort((a, b) => b.score.CompareTo(a.score));
            var top = scored[0].score;
            var close = 1;
            while (close < scored.Count && scored[close].score >= top - 0.02f) close++;
            var random = new Unity.Mathematics.Random(Seeds.Mix(seed, 0x3c6ef372u));
            var pick = scored[random.NextInt(0, math.min(close, 4))];
            best = pick.shape;
            bestScore = pick.score;
            return true;
        }

        private static IEnumerable<float2> CandidateAxes(PlazaGeometry geometry,
            PlazaMovement movement, PlazaStyle style)
        {
            yield return geometry.MajorAxis;
            // Arms of L- and U-shaped plots run along either box axis.
            yield return geometry.MinorAxis;
            if (style == PlazaStyle.Classic || style == PlazaStyle.Promenade) yield break;
            // A sequence of rooms follows the rooms' own axes; only the
            // modern style turns its plaza freely.
            if (style == PlazaStyle.Modern)
            {
                yield return Rotate(geometry.MajorAxis, math.radians(30f));
                yield return Rotate(geometry.MajorAxis, math.radians(-30f));
            }
            if (style == PlazaStyle.Modern && movement.DesireLines.Count > 0)
            {
                var line = movement.DesireLines[StrongestDesireLine(movement)];
                var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                if (math.lengthsq(direction) > 0.5f) yield return direction;
            }
        }

        private static IEnumerable<float2> CandidateCentres(PlazaGeometry geometry,
            PlazaMovement movement, PlazaStyle style)
        {
            var centres = new List<float2>();
            void Add(float2 point)
            {
                if (!geometry.Contains(point)) return;
                for (var i = 0; i < centres.Count; i++)
                    if (math.distancesq(centres[i], point) < 1f) return;
                centres.Add(point);
            }
            switch (style)
            {
                case PlazaStyle.Classic:
                case PlazaStyle.Promenade:
                    // Ordered compositions sit on the plot's own centre lines.
                    Add(geometry.BoxCenter);
                    Add(geometry.Centroid);
                    Add(geometry.Pole);
                    break;
                case PlazaStyle.Sequence:
                    foreach (var room in geometry.RoomCentres(
                                 geometry.MaximumDistance * 0.45f,
                                 geometry.MaximumDistance * 1.5f))
                    {
                        Add(room);
                        if (centres.Count >= 2) break;
                    }
                    Add(geometry.Pole);
                    break;
                default:
                    Add(geometry.Pole);
                    Add(geometry.Centroid);
                    // Golden-section points along the main axis.
                    var offset = geometry.MajorAxis * (geometry.MajorSpan * 0.12f);
                    Add(geometry.Pole + offset);
                    Add(geometry.Pole - offset);
                    // Shifted towards where people walk.
                    var pull = float2.zero;
                    for (var i = 0; i < movement.Entrances.Count; i++)
                        pull += movement.Entrances[i];
                    if (movement.Entrances.Count > 0)
                        Add(math.lerp(geometry.Pole, pull / movement.Entrances.Count, 0.2f));
                    break;
            }
            // Centres of the plot's rooms, e.g. the middle of each arm.
            var rooms = geometry.RoomCentres(geometry.MaximumDistance * 0.6f,
                geometry.MaximumDistance);
            for (var i = 0; i < rooms.Count && i < 3; i++) Add(rooms[i]);
            return centres;
        }

        private static IEnumerable<float> CandidateAspects(PlazaGeometry geometry,
            PlazaStyle style, PlazaCoreShape kind)
        {
            // Polygon-based kinds keep the template's own proportions.
            if (kind == PlazaCoreShape.BoundaryAligned
                || kind == PlazaCoreShape.ConvexPolygon)
            {
                yield return 1f;
                yield break;
            }
            var plot = math.clamp(geometry.MinorSpan / math.max(1f, geometry.MajorSpan),
                0.15f, 1f);
            switch (style)
            {
                case PlazaStyle.Promenade:
                    // A long, comparatively narrow promenade space.
                    yield return math.max(0.18f, plot * 0.6f);
                    yield return math.max(0.22f, plot * 0.8f);
                    break;
                case PlazaStyle.Classic:
                    yield return plot;
                    if (plot > 0.7f) yield return 1f;
                    // Concave plots: the plot's box says little about its
                    // rooms, so try calm proportions for one arm or room.
                    if (geometry.Convexity < 0.9f)
                    {
                        yield return 0.35f;
                        yield return 0.55f;
                        yield return 0.8f;
                        if (plot <= 0.7f) yield return 1f;
                    }
                    break;
                default:
                    yield return plot;
                    yield return math.min(1f, plot * 1.3f);
                    yield return math.max(0.35f, plot * 0.75f);
                    if (geometry.Convexity < 0.9f)
                    {
                        yield return 0.4f;
                        yield return 0.8f;
                    }
                    break;
            }
        }

        /// <summary>
        /// Grows a shape around its centre until it would leave the polygon
        /// with the given rim, then shrinks it to the target share.
        /// </summary>
        private static bool GrowToFit(PlazaGeometry geometry, ref PlazaShape shape,
            float aspect, float rim, float targetShare)
        {
            float low = 0f, high;
            var polygonBased = shape.Kind == PlazaCoreShape.BoundaryAligned
                || shape.Kind == PlazaCoreShape.ConvexPolygon;
            high = polygonBased ? 1f : geometry.MajorSpan * 0.5f;
            for (var step = 0; step < 14; step++)
            {
                var middle = (low + high) * 0.5f;
                Apply(ref shape, middle, aspect, polygonBased);
                if (Fits(geometry, shape, rim)) low = middle;
                else high = middle;
            }
            if (low <= 0.001f) return false;
            Apply(ref shape, low, aspect, polygonBased);
            var area = PlazaShape.Area(shape.Outline());
            var target = geometry.Area * targetShare;
            if (area > target)
                Apply(ref shape, low * math.sqrt(target / area), aspect, polygonBased);
            return true;
        }

        private static void Apply(ref PlazaShape shape, float size, float aspect,
            bool polygonBased)
        {
            if (polygonBased)
            {
                shape.Scale = size;
                return;
            }
            shape.HalfLength = size;
            shape.HalfWidth = size * aspect;
        }

        private static bool Fits(PlazaGeometry geometry, PlazaShape shape, float rim)
        {
            var outline = shape.Outline();
            if (outline.Count < 3) return false;
            var rimSquared = rim * rim;
            var dense = PlazaShape.Densify(outline, math.max(1f, geometry.CellSize * 2f));
            for (var i = 0; i < dense.Count; i++)
                if (!geometry.Contains(dense[i])
                    || PolygonMath.DistanceToBoundarySquared(dense[i], geometry.Polygon)
                        < rimSquared) return false;
            // A notch of a concave outline may reach between outline samples.
            for (var i = 0; i < geometry.Polygon.Count; i++)
                if (shape.Contains(geometry.Polygon[i], outline)) return false;
            return true;
        }

        private static float ScoreCore(PlazaGeometry geometry, PlazaMovement movement,
            PlazaStyle style, PlazaShape shape, List<float2> outline, float area,
            float targetShare)
        {
            // D: how free the centre is from the boundary.
            var free = geometry.DistanceToBoundary(shape.Center)
                / math.max(0.1f, geometry.MaximumDistance);
            // V: share of the plaza outline seen from the entrances.
            var visible = 1f;
            if (movement.Entrances.Count > 0)
            {
                var seen = 0;
                var samples = 0;
                for (var e = 0; e < movement.Entrances.Count; e++)
                for (var i = 0; i < outline.Count; i += math.max(1, outline.Count / 12))
                {
                    samples++;
                    if (geometry.SegmentInside(movement.Entrances[e], outline[i])) seen++;
                }
                visible = samples > 0 ? (float)seen / samples : 1f;
            }
            // R: movement crossing the plaza makes it the natural hub.
            var relation = 0f;
            var count = 0;
            for (var i = 0; i < outline.Count; i += math.max(1, outline.Count / 16))
            {
                relation += movement.At(math.lerp(shape.Center, outline[i], 0.5f));
                count++;
            }
            relation = count > 0 ? relation / count : 0f;
            relation = math.max(relation, movement.At(shape.Center));
            // A: closeness to the intended share of open plaza.
            var share = area / math.max(1f, geometry.Area);
            var areaFit = math.saturate(1f - math.abs(share - targetShare) / targetShare);
            var score = 0.40f * free + 0.25f * visible + 0.20f * relation + 0.15f * areaFit;

            switch (style)
            {
                case PlazaStyle.Classic:
                    // Symmetry: sit on the plot's centre and follow its axis.
                    var offset = math.distance(shape.Center, geometry.BoxCenter)
                        / math.max(1f, geometry.MinorSpan * 0.5f);
                    score += 0.15f * (1f - math.saturate(offset))
                        + 0.05f * math.abs(math.dot(shape.Axis, geometry.MajorAxis));
                    break;
                case PlazaStyle.Promenade:
                    var length = shape.HalfLength * 2f / math.max(1f, geometry.MajorSpan);
                    score += 0.2f * math.saturate(length);
                    break;
                case PlazaStyle.Modern:
                    // Controlled asymmetry: slightly off the plot's centre.
                    var shift = math.distance(shape.Center, geometry.BoxCenter)
                        / math.max(1f, geometry.MinorSpan);
                    score += 0.08f * math.saturate(1f - math.abs(shift - 0.15f) / 0.15f);
                    break;
            }
            return score;
        }

        private static int StrongestDesireLine(PlazaMovement movement)
        {
            var best = 0;
            var bestLength = -1f;
            for (var i = 0; i < movement.DesireLines.Count; i++)
            {
                var length = PlazaMovement.PolylineLength(movement.DesireLines[i])
                    * movement.DesireWeights[i];
                if (length <= bestLength) continue;
                bestLength = length;
                best = i;
            }
            return best;
        }

        internal static float2 Rotate(float2 direction, float angle)
        {
            var c = math.cos(angle);
            var s = math.sin(angle);
            return new float2(direction.x * c - direction.y * s,
                direction.x * s + direction.y * c);
        }
    }
}
