using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Composes a plaza as an urban space instead of filling a polygon with
    /// objects. Two independent decisions shape it: the spatial topology
    /// (central square, linear space, sequence of rooms) and the design
    /// grammar (classic, modern, natural). Rooms come first, then the
    /// decision for every room edge, then designed green, then the intent
    /// for trees, focal points and seating. Deterministic for equal input.
    /// </summary>
    internal static partial class PlazaComposer
    {
        private sealed class GrammarRules
        {
            internal PlazaCoreShape[] Shapes;
            /// <summary>Plot share of a central main square.</summary>
            internal float CentralShare;
            internal float RimShare;
            internal float MinimumRim;
            internal float MaximumRim;
            internal float CorridorWidth;
            internal float TreeSpacing;
            /// <summary>Design target for the green share of the plot.</summary>
            internal float GreenMinimum;
            internal float GreenMaximum;
            internal float BedDepth;
        }

        private static readonly Dictionary<PlazaGrammar, GrammarRules> Grammars =
            new Dictionary<PlazaGrammar, GrammarRules>
            {
                [PlazaGrammar.Classic] = new GrammarRules
                {
                    Shapes = new[] { PlazaCoreShape.Rectangle,
                        PlazaCoreShape.ChamferedRectangle, PlazaCoreShape.BoundaryAligned },
                    CentralShare = 0.42f, RimShare = 0.14f, MinimumRim = 3f,
                    MaximumRim = 14f, CorridorWidth = 5f, TreeSpacing = 7.5f,
                    GreenMinimum = 0.15f, GreenMaximum = 0.3f, BedDepth = 9f,
                },
                [PlazaGrammar.Modern] = new GrammarRules
                {
                    Shapes = new[] { PlazaCoreShape.Parallelogram,
                        PlazaCoreShape.ConvexPolygon, PlazaCoreShape.ChamferedRectangle },
                    CentralShare = 0.38f, RimShare = 0.13f, MinimumRim = 2.5f,
                    MaximumRim = 13f, CorridorWidth = 4f, TreeSpacing = 6f,
                    GreenMinimum = 0.2f, GreenMaximum = 0.35f, BedDepth = 10f,
                },
                [PlazaGrammar.Natural] = new GrammarRules
                {
                    Shapes = new[] { PlazaCoreShape.Organic, PlazaCoreShape.Ellipse,
                        PlazaCoreShape.ChamferedRectangle },
                    CentralShare = 0.32f, RimShare = 0.16f, MinimumRim = 3f,
                    MaximumRim = 16f, CorridorWidth = 4f, TreeSpacing = 6.5f,
                    GreenMinimum = 0.3f, GreenMaximum = 0.45f, BedDepth = 12f,
                },
            };

        private const float MinimumCoreWidth = 6f;
        /// <summary>Paved walk kept between a room and any green.</summary>
        private const float GreenGap = 2f;
        private const float EntranceClearance = 4f;

        /// <summary>Everything the composition steps share.</summary>
        private sealed class Context
        {
            internal PlazaDesign Design;
            internal PlazaGeometry Geometry;
            internal PlazaMovement Movement;
            internal GrammarRules Rules;
            internal int Seed;
            /// <summary>Usable width: diameter of the widest inscribed circle.</summary>
            internal float Usable;
            internal float MinimumWidth;
            internal float Rim;
        }

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
            design.Geometry = geometry;
            design.Movement = movement;
            var metrics = design.Metrics;
            metrics.PolygonArea = geometry.Area;
            metrics.Compactness = geometry.Compactness;
            metrics.Convexity = geometry.Convexity;
            metrics.Elongation = geometry.Elongation;
            metrics.CellSize = geometry.CellSize;
            RateTopologies(geometry, movement, metrics.TopologySuitability);
            RateGrammars(geometry, movement, metrics.GrammarSuitability);

            // A preset fixes one of the two decisions; the geometry decides
            // the other one.
            PlazaTopology? forcedTopology = style == PlazaStyle.Promenade
                ? PlazaTopology.Linear
                : style == PlazaStyle.Sequence ? PlazaTopology.Sequence
                : (PlazaTopology?)null;
            PlazaGrammar? forcedGrammar = style == PlazaStyle.Classic
                ? PlazaGrammar.Classic
                : style == PlazaStyle.Modern ? PlazaGrammar.Modern
                : style == PlazaStyle.Natural ? PlazaGrammar.Natural
                : (PlazaGrammar?)null;
            design.Grammar = forcedGrammar ?? Best(metrics.GrammarSuitability);

            var topologies = new List<PlazaTopology>();
            if (forcedTopology.HasValue) topologies.Add(forcedTopology.Value);
            foreach (var candidate in Ranked(metrics.TopologySuitability))
                if (!topologies.Contains(candidate)) topologies.Add(candidate);

            var usable = geometry.MaximumDistance * 2f;
            var context = new Context
            {
                Design = design, Geometry = geometry, Movement = movement,
                Rules = Grammars[design.Grammar], Seed = seed, Usable = usable,
                MinimumWidth = math.clamp(usable * 0.45f, MinimumCoreWidth, usable * 0.8f),
            };
            context.Rim = math.max(0.5f, math.min(math.clamp(usable
                    * context.Rules.RimShare, context.Rules.MinimumRim,
                    context.Rules.MaximumRim), (usable - context.MinimumWidth) * 0.5f));

            foreach (var topology in topologies)
            {
                design.Rooms.Clear();
                if (!BuildRooms(context, topology)) continue;
                design.Topology = topology;
                break;
            }
            if (design.Rooms.Count == 0)
            {
                design.Failure = "no main square fits the outline";
            }
            else
            {
                design.CoreShape = design.Rooms[0].Shape.Kind;
                ConnectEntrances(context);
                AssignEdgeRoles(context);
                ComposeGreen(context);
                AddDesignIntent(context);
                ScoreDesign(context);
            }

            metrics.MainPlazaArea = design.Rooms.Count > 0
                ? PlazaShape.Area(design.Rooms[0].Boundary) : 0f;
            metrics.MainPlazaMinimumWidth = design.Rooms.Count > 0
                ? PlazaShape.MinimumWidth(design.Rooms[0].Boundary) : 0f;
            foreach (var room in design.Rooms)
                if (room.Role != PlazaRoomRole.Passage)
                    metrics.RoomArea += PlazaShape.Area(room.Boundary);
            foreach (var zone in design.GreenZones)
                metrics.GreenArea += PlazaShape.Area(zone.Polygon);
            metrics.GreenSurfaceRatio = geometry.Area > 0f ? metrics.GreenArea / geometry.Area : 0f;
            timer.Stop();
            metrics.Milliseconds = timer.Elapsed.TotalMilliseconds;
            return design;
        }

        private static T Best<T>(Dictionary<T, float> scores) => Ranked(scores)[0];

        private static List<T> Ranked<T>(Dictionary<T, float> scores)
        {
            var keys = new List<T>(scores.Keys);
            keys.Sort((a, b) =>
            {
                var order = scores[b].CompareTo(scores[a]);
                return order != 0 ? order : Comparer<T>.Default.Compare(a, b);
            });
            return keys;
        }

        // ---------------------------------------------------------- ratings

        private static void RateTopologies(PlazaGeometry geometry, PlazaMovement movement,
            Dictionary<PlazaTopology, float> result)
        {
            var compact = math.saturate(geometry.Compactness / 0.785f);
            var concave = math.saturate((0.93f - geometry.Convexity) / 0.3f);
            var rooms = SeparateRooms(geometry, movement);
            var spread = EntranceSpread(geometry, movement);
            result[PlazaTopology.Central] = 0.45f + 0.3f * compact
                - 0.35f * concave - 0.2f * math.saturate((geometry.Elongation - 1.8f) / 1.2f);
            // A plot made of separate rooms is a sequence, however long it is.
            result[PlazaTopology.Linear] = (0.8f * math.saturate((geometry.Elongation - 1.6f)
                / 1.2f) + 0.2f * spread * (geometry.Elongation > 1.6f ? 1f : 0f))
                * (rooms >= 2 ? 0.5f : 1f);
            // Only a plot with real rooms suggests a sequence on its own.
            result[PlazaTopology.Sequence] = 0.1f + 0.5f * concave + (rooms >= 2 ? 0.35f : 0f);
        }

        private static void RateGrammars(PlazaGeometry geometry, PlazaMovement movement,
            Dictionary<PlazaGrammar, float> result)
        {
            var compact = math.saturate(geometry.Compactness / 0.785f);
            var concave = math.saturate((0.93f - geometry.Convexity) / 0.3f);
            var symmetric = Symmetry(geometry);
            var diagonal = Diagonality(geometry, movement);
            result[PlazaGrammar.Classic] = 0.35f * compact + 0.25f
                * math.saturate((geometry.Convexity - 0.7f) / 0.3f) + 0.4f * symmetric;
            result[PlazaGrammar.Modern] = 0.3f + 0.35f * diagonal
                + 0.25f * (1f - symmetric) * (1f - concave);
            result[PlazaGrammar.Natural] = 0.25f + 0.45f * concave
                + 0.15f * math.saturate((geometry.Area - 5000f) / 10000f);
        }

        /// <summary>
        /// Number of rooms the plot really consists of: distance-field
        /// maxima count as separate rooms only if a bottleneck lies between
        /// them, not when they sit on the ridge of one long space.
        /// </summary>
        private static int SeparateRooms(PlazaGeometry geometry, PlazaMovement movement)
        {
            var kept = new List<float2>();
            foreach (var centre in geometry.RoomCentres(geometry.MaximumDistance * 0.45f,
                         geometry.MaximumDistance))
            {
                var separated = true;
                foreach (var other in kept)
                    separated &= Bottleneck(geometry, movement, centre, other);
                if (separated) kept.Add(centre);
            }
            return kept.Count;
        }

        private static bool Bottleneck(PlazaGeometry geometry, PlazaMovement movement,
            float2 a, float2 b)
        {
            var path = movement.ShortestPath(a, b);
            if (path == null) return true;
            var narrowest = float.MaxValue;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var pieces = math.max(1, (int)math.ceil(math.distance(path[i], path[i + 1])));
                for (var k = 0; k <= pieces; k++)
                    narrowest = math.min(narrowest, geometry.DistanceToBoundary(
                        math.lerp(path[i], path[i + 1], (float)k / pieces)));
            }
            var rooms = math.min(geometry.DistanceToBoundary(a), geometry.DistanceToBoundary(b));
            return narrowest < rooms * 0.7f;
        }

        /// <summary>How far apart the entrances lie along the main axis (0–1).</summary>
        private static float EntranceSpread(PlazaGeometry geometry, PlazaMovement movement)
        {
            var spread = 0f;
            for (var i = 0; i < movement.Entrances.Count; i++)
            for (var j = i + 1; j < movement.Entrances.Count; j++)
                spread = math.max(spread, math.abs(math.dot(movement.Entrances[i]
                    - movement.Entrances[j], geometry.MajorAxis)));
            return math.saturate(spread / math.max(1f, geometry.MajorSpan));
        }

        /// <summary>1 when a desire line runs at 45° to the main axis.</summary>
        private static float Diagonality(PlazaGeometry geometry, PlazaMovement movement)
        {
            var diagonal = 0f;
            for (var i = 0; i < movement.DesireLines.Count; i++)
            {
                var line = movement.DesireLines[i];
                var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                var cosine = math.abs(math.dot(direction, geometry.MajorAxis));
                diagonal = math.max(diagonal, 1f - math.abs(cosine * cosine * 2f - 1f));
            }
            return diagonal;
        }

        /// <summary>
        /// Mirror symmetry of the outline across its main axes (0–1): the
        /// mean distance of mirrored vertices to the boundary.
        /// </summary>
        private static float Symmetry(PlazaGeometry geometry)
        {
            var best = 0f;
            foreach (var normal in new[] { geometry.MajorAxis, geometry.MinorAxis })
            {
                var offset = math.dot(geometry.BoxCenter, normal);
                var error = 0f;
                for (var i = 0; i < geometry.Polygon.Count; i++)
                {
                    var p = geometry.Polygon[i];
                    var mirrored = p - normal * (2f * (math.dot(p, normal) - offset));
                    error += math.sqrt(PolygonMath.DistanceToBoundarySquared(mirrored,
                        geometry.Polygon));
                }
                error /= geometry.Polygon.Count;
                best = math.max(best, 1f - math.saturate(error
                    / math.max(1f, geometry.MinorSpan * 0.1f)));
            }
            return best;
        }

        // ----------------------------------------------------- room fitting

        /// <summary>What a fitted room has to satisfy.</summary>
        private sealed class RoomRequest
        {
            internal PlazaCoreShape[] Shapes;
            internal List<float2> Axes = new List<float2>();
            internal List<float2> Centres = new List<float2>();
            internal List<float> Aspects = new List<float>();
            internal float Rim;
            internal float MinimumWidth;
            internal float TargetArea;
            internal float MinimumArea;
            /// <summary>Rooms to keep a gap to.</summary>
            internal List<PlazaRoom> Avoid = new List<PlazaRoom>();
            internal float AvoidGap;
            /// <summary>Every outline point must satisfy this, if set.</summary>
            internal Func<float2, bool> Constraint;
            /// <summary>Grammar- or topology-specific preference added to the score.</summary>
            internal Func<PlazaShape, float> Bias;
            internal uint Salt;
        }

        /// <summary>
        /// Searches primitives, axes, centres and proportions for the best
        /// room: inside the polygon with the rim, clear of other rooms,
        /// at least the minimum width and area.
        /// </summary>
        private static bool FitRoom(Context context, RoomRequest request, out PlazaShape best)
        {
            var geometry = context.Geometry;
            best = default;
            var scored = new List<(PlazaShape shape, float score)>();
            foreach (var kind in request.Shapes)
            {
                var polygonBased = kind == PlazaCoreShape.BoundaryAligned
                    || kind == PlazaCoreShape.ConvexPolygon;
                if (polygonBased && geometry.Convexity < 0.95f) continue;
                foreach (var axis in request.Axes)
                foreach (var center in request.Centres)
                foreach (var aspect in polygonBased ? new List<float> { 1f } : request.Aspects)
                {
                    var shape = new PlazaShape
                    {
                        Kind = kind, Center = center, Axis = axis,
                        Skew = kind == PlazaCoreShape.Parallelogram ? 0.45f : 0f,
                        Phase = kind == PlazaCoreShape.Organic
                            ? OrganicPhase(context, center, axis) : 0f,
                    };
                    if (polygonBased)
                    {
                        shape.Template = kind == PlazaCoreShape.BoundaryAligned
                            ? geometry.Polygon : geometry.Hull;
                        shape.TemplateAnchor = center;
                    }
                    if (!GrowToFit(context, request, ref shape, aspect)) continue;
                    var outline = shape.Outline();
                    var area = PlazaShape.Area(outline);
                    if (area < request.MinimumArea) continue;
                    if (PlazaShape.MinimumWidth(outline) < request.MinimumWidth) continue;
                    var score = ScoreRoom(context, shape, outline, area, request.TargetArea);
                    if (request.Bias != null) score += request.Bias(shape);
                    scored.Add((shape, score));
                }
            }
            if (scored.Count == 0) return false;

            // Near-equal candidates are a matter of taste: the seed decides.
            scored.Sort((a, b) => b.score.CompareTo(a.score));
            var top = scored[0].score;
            var close = 1;
            while (close < scored.Count && scored[close].score >= top - 0.02f) close++;
            var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed,
                0x3c6ef372u ^ request.Salt));
            best = scored[random.NextInt(0, math.min(close, 4))].shape;
            return true;
        }

        private static bool GrowToFit(Context context, RoomRequest request,
            ref PlazaShape shape, float aspect)
        {
            var polygonBased = shape.Kind == PlazaCoreShape.BoundaryAligned
                || shape.Kind == PlazaCoreShape.ConvexPolygon;
            float low = 0f, high = polygonBased ? 1f : context.Geometry.MajorSpan * 0.5f;
            for (var step = 0; step < 14; step++)
            {
                var middle = (low + high) * 0.5f;
                Resize(ref shape, middle, aspect, polygonBased);
                if (Fits(context, request, shape)) low = middle;
                else high = middle;
            }
            if (low <= 0.001f) return false;
            Resize(ref shape, low, aspect, polygonBased);
            var area = PlazaShape.Area(shape.Outline());
            if (area > request.TargetArea)
                Resize(ref shape, low * math.sqrt(request.TargetArea / area), aspect,
                    polygonBased);
            return true;
        }

        private static void Resize(ref PlazaShape shape, float size, float aspect,
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

        private static bool Fits(Context context, RoomRequest request, PlazaShape shape)
        {
            var geometry = context.Geometry;
            var outline = shape.Outline();
            if (outline.Count < 3) return false;
            var rimSquared = request.Rim * request.Rim;
            var dense = PlazaShape.Densify(outline, math.max(1f, geometry.CellSize * 2f));
            for (var i = 0; i < dense.Count; i++)
            {
                var point = dense[i];
                if (!geometry.Contains(point)
                    || PolygonMath.DistanceToBoundarySquared(point, geometry.Polygon)
                        < rimSquared) return false;
                if (request.Constraint != null && !request.Constraint(point)) return false;
                for (var r = 0; r < request.Avoid.Count; r++)
                {
                    var other = request.Avoid[r];
                    if (other.Shape.Contains(point, other.Boundary)
                        || DistanceToOutline(point, other.Boundary) < request.AvoidGap)
                        return false;
                }
            }
            // A notch of a concave outline may reach between outline samples.
            for (var i = 0; i < geometry.Polygon.Count; i++)
                if (shape.Contains(geometry.Polygon[i], outline)) return false;
            for (var r = 0; r < request.Avoid.Count; r++)
                if (shape.Contains(request.Avoid[r].Center, outline)) return false;
            return true;
        }

        /// <summary>
        /// Generic room quality: free centre, visible from the entrances,
        /// on the walking lines, close to the intended size.
        /// </summary>
        private static float ScoreRoom(Context context, PlazaShape shape,
            List<float2> outline, float area, float targetArea)
        {
            var geometry = context.Geometry;
            var movement = context.Movement;
            var free = geometry.DistanceToBoundary(shape.Center)
                / math.max(0.1f, geometry.MaximumDistance);
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
            var relation = movement.At(shape.Center);
            var count = 1;
            for (var i = 0; i < outline.Count; i += math.max(1, outline.Count / 16))
            {
                relation += movement.At(math.lerp(shape.Center, outline[i], 0.5f));
                count++;
            }
            relation /= count;
            var areaFit = math.saturate(1f - math.abs(area - targetArea)
                / math.max(1f, targetArea));
            return 0.40f * free + 0.25f * visible + 0.20f * relation + 0.15f * areaFit;
        }

        /// <summary>
        /// An organic square reaches out towards where people arrive and
        /// recedes where the gardens get their depth.
        /// </summary>
        private static float OrganicPhase(Context context, float2 center, float2 axis)
        {
            var pull = float2.zero;
            foreach (var entrance in context.Movement.Entrances)
                pull += math.normalizesafe(entrance - center);
            if (math.lengthsq(pull) < 0.25f)
                return new Unity.Mathematics.Random(Seeds.Mix(context.Seed, 0x6a09e667u))
                    .NextFloat(0f, 2f * math.PI);
            var normal = new float2(-axis.y, axis.x);
            return math.atan2(math.dot(pull, normal), math.dot(pull, axis));
        }

        internal static float2 Rotate(float2 direction, float angle)
        {
            var c = math.cos(angle);
            var s = math.sin(angle);
            return new float2(direction.x * c - direction.y * s,
                direction.x * s + direction.y * c);
        }

        internal static float DistanceToOutline(float2 point, IReadOnlyList<float2> outline)
        {
            var best = float.MaxValue;
            for (var i = 0; i < outline.Count; i++)
                best = math.min(best, PolygonMath.DistanceToSegmentSquared(point,
                    outline[i], outline[(i + 1) % outline.Count]));
            return math.sqrt(best);
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

        /// <summary>True when the point lies in any room or within gap of it.</summary>
        private static bool InsideAnyRoom(Context context, float2 point, float gap,
            bool includePassages = true)
        {
            foreach (var room in context.Design.Rooms)
            {
                if (!includePassages && room.Role == PlazaRoomRole.Passage) continue;
                if (room.Shape.Contains(point, room.Boundary)) return true;
                if (gap > 0f && DistanceToOutline(point, room.Boundary) < gap) return true;
            }
            return false;
        }

        private static bool NearCorridor(Context context, float2 point, float extra)
        {
            foreach (var corridor in context.Design.MovementCorridors)
                if (PlazaMovement.DistanceToPolyline(point, corridor.Points)
                    < corridor.Width * 0.5f + extra) return true;
            return false;
        }

        private static bool NearEntrance(Context context, float2 point, float radius)
        {
            foreach (var entrance in context.Movement.Entrances)
                if (math.distance(point, entrance) < radius) return true;
            return false;
        }
    }
}
