using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        private const float PassageWidth = 5f;
        private const float GardenCourtWidth = 14f;

        private static bool BuildRooms(Context context, PlazaTopology topology)
        {
            switch (topology)
            {
                case PlazaTopology.Linear: return BuildLinear(context);
                case PlazaTopology.Sequence: return BuildSequence(context);
                default: return BuildCentral(context);
            }
        }

        private static PlazaRoom AddRoom(Context context, PlazaShape shape,
            PlazaRoomRole role, float importance)
        {
            var room = new PlazaRoom
            {
                Role = role, Importance = importance, Shape = shape,
                Center = shape.Center, Axis = shape.Axis,
            };
            room.Boundary.AddRange(shape.Outline());
            context.Design.Rooms.Add(room);
            return room;
        }

        private static void Link(Context context, int a, int b)
        {
            var rooms = context.Design.Rooms;
            if (!rooms[a].ConnectedRooms.Contains(b)) rooms[a].ConnectedRooms.Add(b);
            if (!rooms[b].ConnectedRooms.Contains(a)) rooms[b].ConnectedRooms.Add(a);
        }

        // ---------------------------------------------------------- central

        private static bool BuildCentral(Context context)
        {
            var geometry = context.Geometry;
            var request = MainRequest(context, geometry.Area * context.Rules.CentralShare,
                geometry.Area * 0.2f);
            AddGrammarCentres(context, request.Centres);
            if (!FitRoom(context, request, out var shape)) return false;
            AddRoom(context, shape, PlazaRoomRole.MainSquare, 1f);
            return true;
        }

        /// <summary>Main-square request with the grammar's shapes and preferences.</summary>
        private static RoomRequest MainRequest(Context context, float target, float minimum)
        {
            var geometry = context.Geometry;
            var grammar = context.Design.Grammar;
            var shapes = new List<PlazaCoreShape>(context.Rules.Shapes);
            // A round plot may carry a round classic square.
            if (grammar == PlazaGrammar.Classic && geometry.Compactness > 0.86f)
                shapes.Add(PlazaCoreShape.Ellipse);
            var request = new RoomRequest
            {
                Shapes = shapes.ToArray(), Rim = context.Rim,
                MinimumWidth = context.MinimumWidth, TargetArea = target,
                MinimumArea = minimum, Salt = 1u,
            };
            request.Axes.Add(geometry.MajorAxis);
            request.Axes.Add(geometry.MinorAxis);
            if (grammar == PlazaGrammar.Modern)
            {
                request.Axes.Add(Rotate(geometry.MajorAxis, math.radians(25f)));
                request.Axes.Add(Rotate(geometry.MajorAxis, math.radians(-25f)));
                if (context.Movement.DesireLines.Count > 0)
                {
                    var line = context.Movement.DesireLines[
                        StrongestDesireLine(context.Movement)];
                    var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                    if (math.lengthsq(direction) > 0.5f) request.Axes.Add(direction);
                }
            }
            var plot = math.clamp(geometry.MinorSpan / math.max(1f, geometry.MajorSpan),
                0.15f, 1f);
            request.Aspects.Add(plot);
            if (plot > 0.7f) request.Aspects.Add(1f);
            if (grammar != PlazaGrammar.Classic)
            {
                request.Aspects.Add(math.min(1f, plot * 1.25f));
                request.Aspects.Add(math.max(0.35f, plot * 0.7f));
            }
            if (geometry.Convexity < 0.9f)
            {
                request.Aspects.Add(0.4f);
                request.Aspects.Add(0.6f);
                request.Aspects.Add(0.85f);
            }
            request.Bias = shape => GrammarBias(context, shape);
            return request;
        }

        private static void AddGrammarCentres(Context context, List<float2> centres)
        {
            var geometry = context.Geometry;
            void Add(float2 point)
            {
                if (!geometry.Contains(point)) return;
                foreach (var existing in centres)
                    if (math.distancesq(existing, point) < 1f) return;
                centres.Add(point);
            }
            Add(geometry.BoxCenter);
            Add(geometry.Centroid);
            Add(geometry.Pole);
            if (context.Design.Grammar == PlazaGrammar.Modern)
            {
                var offset = geometry.MajorAxis * (geometry.MajorSpan * 0.1f);
                Add(geometry.Pole + offset);
                Add(geometry.Pole - offset);
                var pull = float2.zero;
                foreach (var entrance in context.Movement.Entrances) pull += entrance;
                if (context.Movement.Entrances.Count > 0)
                    Add(math.lerp(geometry.Pole, pull / context.Movement.Entrances.Count, 0.2f));
            }
            var rooms = geometry.RoomCentres(geometry.MaximumDistance * 0.6f,
                geometry.MaximumDistance);
            for (var i = 0; i < rooms.Count && i < 3; i++) Add(rooms[i]);
        }

        /// <summary>Grammar preference for a fitted room.</summary>
        private static float GrammarBias(Context context, PlazaShape shape)
        {
            var geometry = context.Geometry;
            var offset = math.distance(shape.Center, geometry.BoxCenter)
                / math.max(1f, geometry.MinorSpan * 0.5f);
            var aligned = math.max(math.abs(math.dot(shape.Axis, geometry.MajorAxis)),
                math.abs(math.dot(shape.Axis, geometry.MinorAxis)));
            switch (context.Design.Grammar)
            {
                case PlazaGrammar.Classic:
                    // Formal: on the plot's centre, along its axes.
                    return 0.18f * (1f - math.saturate(offset)) + 0.06f * aligned;
                case PlazaGrammar.Modern:
                    // Controlled asymmetry and a turned or sheared figure.
                    return 0.1f * math.saturate(1f - math.abs(offset - 0.3f) / 0.3f)
                        + 0.06f * (1f - aligned)
                        + (shape.Kind == PlazaCoreShape.Parallelogram ? 0.06f : 0f);
                default:
                    // Soft, asymmetric contours rather than a perfect circle.
                    return shape.Kind == PlazaCoreShape.Organic ? 0.06f : 0f;
            }
        }

        // ----------------------------------------------------------- linear

        /// <summary>
        /// A long space along the dominant direction, flanked by small
        /// pockets for staying: symmetric pairs (classic), alternating
        /// (modern) or a single one (natural).
        /// </summary>
        private static bool BuildLinear(Context context)
        {
            var geometry = context.Geometry;
            var axes = new List<float2>();
            if (geometry.Elongation >= 1.4f) axes.Add(geometry.MajorAxis);
            if (context.Movement.DesireLines.Count > 0)
            {
                var line = context.Movement.DesireLines[StrongestDesireLine(context.Movement)];
                var direction = math.normalizesafe(line[line.Count - 1] - line[0]);
                if (math.lengthsq(direction) > 0.5f) axes.Add(direction);
            }
            if (axes.Count == 0) axes.Add(geometry.MajorAxis);

            // The widest band that still reads as linear (length ≥ 2.5 × width).
            foreach (var share in new[] { 0.4f, 0.32f, 0.25f })
            {
                var width = math.clamp(context.Usable * share, MinimumCoreWidth, 22f);
                context.Design.Rooms.Clear();
                if (BuildLinearBand(context, axes, width)) return true;
            }
            context.Design.Rooms.Clear();
            return false;
        }

        private static bool BuildLinearBand(Context context, List<float2> axes, float width)
        {
            var geometry = context.Geometry;
            var kind = context.Design.Grammar == PlazaGrammar.Classic
                ? PlazaCoreShape.Rectangle
                : context.Design.Grammar == PlazaGrammar.Modern
                    ? PlazaCoreShape.Parallelogram : PlazaCoreShape.ChamferedRectangle;
            var request = new RoomRequest { Rim = 1.5f };
            var centres = new List<float2>();
            AddGrammarCentres(context, centres);
            if (context.Movement.DesireLines.Count > 0)
            {
                var line = context.Movement.DesireLines[StrongestDesireLine(context.Movement)];
                var middle = math.lerp(line[0], line[line.Count - 1], 0.5f);
                if (geometry.Contains(middle)) centres.Add(middle);
            }

            var best = default(PlazaShape);
            var bestLength = 0f;
            foreach (var axis in axes)
            foreach (var center in centres)
            {
                var shape = new PlazaShape
                {
                    Kind = kind, Center = center, Axis = axis, HalfWidth = width * 0.5f,
                    Skew = kind == PlazaCoreShape.Parallelogram ? 0.3f : 0f,
                };
                float low = 0f, high = geometry.MajorSpan * 0.5f;
                for (var step = 0; step < 14; step++)
                {
                    shape.HalfLength = (low + high) * 0.5f;
                    if (Fits(context, request, shape)) low = shape.HalfLength;
                    else high = shape.HalfLength;
                }
                // Prefer the longer space; the centre on the plot breaks ties.
                var length = low - 0.01f * math.distance(center, geometry.BoxCenter);
                if (length <= bestLength) continue;
                shape.HalfLength = low;
                bestLength = length;
                best = shape;
            }
            // A linear space runs through most of the plot.
            if (best.HalfLength * 2f < width * 2.5f
                || best.HalfLength * 2f < geometry.MajorSpan * 0.55f) return false;
            AddRoom(context, best, PlazaRoomRole.MainSquare, 1f);

            // Pockets along the sides.
            var placements = context.Design.Grammar == PlazaGrammar.Classic
                ? new[] { (-0.45f, 1f), (-0.45f, -1f), (0.45f, 1f), (0.45f, -1f) }
                : context.Design.Grammar == PlazaGrammar.Modern
                    ? new[] { (-0.4f, 1f), (0.35f, -1f) }
                    : new[] { (0.1f, 1f) };
            var pocketLength = math.clamp(best.HalfLength * 0.16f, 4f, 9f);
            foreach (var (along, side) in placements)
            {
                var depth = SideDepth(context, best, side);
                var halfWidth = math.clamp(depth * 0.35f, 3f, 6f);
                if (depth < halfWidth * 2f + 2f) continue;
                var pocket = new PlazaShape
                {
                    Kind = context.Design.Grammar == PlazaGrammar.Natural
                        ? PlazaCoreShape.Ellipse : PlazaCoreShape.Rectangle,
                    Axis = best.Axis, HalfLength = pocketLength, HalfWidth = halfWidth,
                    Center = best.Center + best.Axis * (best.HalfLength * along)
                        + best.Normal * (side * (best.HalfWidth + halfWidth - 0.5f)),
                };
                var pocketRequest = new RoomRequest { Rim = 1.5f };
                foreach (var room in context.Design.Rooms)
                    if (room.Role != PlazaRoomRole.MainSquare) pocketRequest.Avoid.Add(room);
                pocketRequest.AvoidGap = 2f;
                if (!Fits(context, pocketRequest, pocket)) continue;
                AddRoom(context, pocket, PlazaRoomRole.SecondarySquare, 0.3f);
                Link(context, 0, context.Design.Rooms.Count - 1);
            }
            return true;
        }

        /// <summary>Free depth from a room's long side to the plot boundary.</summary>
        private static float SideDepth(Context context, PlazaShape shape, float side)
        {
            var start = shape.Center + shape.Normal * (side * shape.HalfWidth);
            var reach = 0f;
            while (reach < context.Geometry.MinorSpan
                && context.Geometry.Contains(start + shape.Normal * (side * (reach + 0.5f))))
                reach += 0.5f;
            return reach;
        }

        // --------------------------------------------------------- sequence

        /// <summary>
        /// Two or more distinct rooms of clearly different weight. Concave
        /// plots use their own rooms; a compact plot is divided along its
        /// main axis into a main square and a smaller second room.
        /// </summary>
        private static bool BuildSequence(Context context)
        {
            var geometry = context.Geometry;
            var rooms = geometry.RoomCentres(geometry.MaximumDistance * 0.45f,
                geometry.MaximumDistance * 1.5f);
            if (rooms.Count >= 2 && SequenceInRooms(context, rooms)) return true;
            context.Design.Rooms.Clear();
            return SequenceBySplit(context);
        }

        private static bool SequenceInRooms(Context context, List<float2> centres)
        {
            var geometry = context.Geometry;
            // The main square goes to whichever room suits it best.
            var main = MainRequest(context, geometry.Area * 0.26f, geometry.Area * 0.08f);
            main.Centres.AddRange(centres);
            if (!FitRoom(context, main, out var mainShape)) return false;
            var mainRoom = AddRoom(context, mainShape, PlazaRoomRole.MainSquare, 1f);
            var mainArea = PlazaShape.Area(mainRoom.Boundary);

            var gap = SequenceGap(context);
            for (var i = 0; i < centres.Count && context.Design.Rooms.Count < 3; i++)
            {
                // Other rooms of the plot, clear of the main square.
                if (mainShape.Contains(centres[i], mainRoom.Boundary)
                    || DistanceToOutline(centres[i], mainRoom.Boundary) < gap + 3f) continue;
                var garden = context.Design.Rooms.Count == 2;
                var request = SecondaryRequest(context, centres[i],
                    mainArea * (garden ? 0.3f : 0.45f), garden);
                if (!FitRoom(context, request, out var shape)) continue;
                // A garden court needs room for a garden and a walk around it.
                garden &= PlazaShape.MinimumWidth(shape.Outline()) >= GardenCourtWidth;
                AddRoom(context, shape, garden ? PlazaRoomRole.GardenCourt
                    : PlazaRoomRole.SecondarySquare, garden ? 0.35f : 0.45f);
            }
            if (context.Design.Rooms.Count < 2) return false;
            AddPassages(context);
            return true;
        }

        /// <summary>
        /// A compact plot divided into two rooms with different purposes:
        /// the main square takes the side people arrive on, the second room
        /// the quieter side, as a calmer place to stay. On a compact plot it
        /// is a chamber attached to the square, not a separate figure.
        /// </summary>
        private static bool SequenceBySplit(Context context)
        {
            var geometry = context.Geometry;
            var origin = geometry.BoxCenter;
            var axis = geometry.MajorAxis;
            var length = geometry.MajorSpan;
            var arrivals = Arrivals(context, origin, axis);
            if (geometry.Elongation < 1.3f
                && math.abs(Arrivals(context, origin, geometry.MinorAxis)) > math.abs(arrivals))
            {
                axis = geometry.MinorAxis;
                length = geometry.MinorSpan;
                arrivals = Arrivals(context, origin, axis);
            }
            var chamber = geometry.Elongation < 1.6f && geometry.Convexity >= 0.9f;
            var first = arrivals < 0f ? -1f : 1f;
            foreach (var side in new[] { first, -first })
            {
                context.Design.Rooms.Clear();
                // The main square takes a little more than half of the plot.
                var main = MainRequest(context, geometry.Area * 0.28f, geometry.Area * 0.1f);
                main.MinimumWidth = math.min(main.MinimumWidth, context.Usable * 0.4f);
                main.Centres.Add(origin + axis * (side * length * 0.18f));
                main.Centres.Add(origin + axis * (side * length * 0.12f));
                // Concave plots may cut the fixed points off: also try the
                // point with the most room in this half.
                AddWidestIn(context, main.Centres, p => math.dot(p - origin, axis) * side >= length * 0.05f);
                main.Constraint = p => math.dot(p - origin, axis) * side >= -length * 0.08f;
                if (!FitRoom(context, main, out var mainShape)) continue;
                var mainRoom = AddRoom(context, mainShape, PlazaRoomRole.MainSquare, 1f);
                var mainArea = PlazaShape.Area(mainRoom.Boundary);

                var second = SecondaryRequest(context,
                    origin - axis * (side * length * 0.3f), mainArea * 0.4f, false);
                second.Centres.Add(origin - axis * (side * length * 0.25f));
                AddWidestIn(context, second.Centres, p => math.dot(p - origin, axis) * side <= -length * 0.12f);
                if (chamber) second.AvoidGap = 0.6f;
                second.Constraint = p => math.dot(p - origin, axis) * side <= -length * 0.05f;
                if (!FitRoom(context, second, out var secondShape)) continue;
                AddRoom(context, secondShape, PlazaRoomRole.SecondarySquare, 0.5f);
                AddPassages(context);
                return true;
            }
            context.Design.Rooms.Clear();
            return false;
        }

        /// <summary>Adds the cell centre farthest from the boundary among those the filter accepts.</summary>
        private static void AddWidestIn(Context context, List<float2> centres, System.Func<float2, bool> accept)
        {
            var geometry = context.Geometry;
            var best = -1;
            for (var index = 0; index < geometry.Inside.Length; index++)
            {
                if (!geometry.Inside[index] || best >= 0 && geometry.Distance[index] <= geometry.Distance[best])
                    continue;
                if (accept(geometry.CellCenter(index))) best = index;
            }
            if (best >= 0) centres.Add(geometry.CellCenter(best));
        }

        /// <summary>Sum of the entrances' offsets along the axis: where people arrive.</summary>
        private static float Arrivals(Context context, float2 origin, float2 axis)
        {
            var sum = 0f;
            foreach (var entrance in context.Movement.Entrances)
                sum += math.dot(entrance - origin, axis);
            return sum;
        }

        /// <summary>Gap between rooms of a sequence, room for a passage.</summary>
        private static float SequenceGap(Context context)
            => math.clamp(context.Usable * 0.12f, 3f, 6f);

        /// <summary>A smaller room with a deliberately different figure.</summary>
        private static RoomRequest SecondaryRequest(Context context, float2 centre,
            float target, bool garden)
        {
            var geometry = context.Geometry;
            var grammar = context.Design.Grammar;
            var shapes = grammar == PlazaGrammar.Natural
                ? new[] { PlazaCoreShape.Organic, PlazaCoreShape.Ellipse }
                : garden ? new[] { PlazaCoreShape.Ellipse }
                : grammar == PlazaGrammar.Classic
                    ? new[] { PlazaCoreShape.Rectangle, PlazaCoreShape.Ellipse }
                    : new[] { PlazaCoreShape.Ellipse, PlazaCoreShape.Parallelogram };
            var request = new RoomRequest
            {
                Shapes = shapes, Rim = math.max(1.5f, context.Rim * 0.6f),
                MinimumWidth = MinimumCoreWidth, TargetArea = target,
                MinimumArea = math.min(target * 0.4f, 60f), AvoidGap = SequenceGap(context),
                Salt = garden ? 3u : 2u,
            };
            request.Avoid.AddRange(context.Design.Rooms);
            request.Centres.Add(centre);
            request.Axes.Add(geometry.MajorAxis);
            request.Axes.Add(geometry.MinorAxis);
            request.Aspects.Add(1f);
            request.Aspects.Add(0.7f);
            return request;
        }

        /// <summary>
        /// Connects every room with a passage to the first room on its way
        /// to the main square, so a passage never runs through another room.
        /// </summary>
        private static void AddPassages(Context context)
        {
            var rooms = context.Design.Rooms;
            var count = rooms.Count;
            for (var i = 1; i < count; i++)
            {
                var target = FirstRoomOnTheWay(context, i, count);
                var path = context.Movement.ShortestPath(rooms[i].Center, rooms[target].Center);
                Link(context, target, i);
                if (path == null) continue;
                var clipped = ClipBetween(path, rooms[i], rooms[target]);
                if (clipped.Count < 2 || PlazaMovement.PolylineLength(clipped) < 1f) continue;
                // Shortest paths hug concave corners; a passage needs its
                // full width inside the plot, so try narrower ones too.
                List<float2> strip = null;
                foreach (var width in new[] { PassageWidth, 4f, 3f })
                {
                    var line = KeepFromBoundary(context, clipped, width * 0.5f + 0.3f);
                    var candidate = Strip(line, width);
                    if (candidate == null || !StripInside(context, candidate)) continue;
                    strip = candidate;
                    clipped = line;
                    break;
                }
                if (strip == null) continue;
                var passage = new PlazaRoom
                {
                    Role = PlazaRoomRole.Passage, Importance = 0.15f,
                    Center = clipped[clipped.Count / 2],
                    Axis = math.normalizesafe(clipped[clipped.Count - 1] - clipped[0]),
                    Shape = new PlazaShape
                    {
                        Kind = PlazaCoreShape.Strip, Template = strip, Scale = 1f,
                        Center = float2.zero, TemplateAnchor = float2.zero,
                    },
                };
                passage.Boundary.AddRange(strip);
                rooms.Add(passage);
                Link(context, i, rooms.Count - 1);
                Link(context, target, rooms.Count - 1);
                var corridor = new PlazaCorridor { Width = PassageWidth, Room = target };
                corridor.Points.AddRange(clipped);
                context.Design.MovementCorridors.Add(corridor);
            }
        }

        private static int FirstRoomOnTheWay(Context context, int from, int count)
        {
            var rooms = context.Design.Rooms;
            var path = context.Movement.ShortestPath(rooms[from].Center, rooms[0].Center);
            if (path == null) return 0;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var pieces = math.max(1, (int)math.ceil(math.distance(path[i], path[i + 1])));
                for (var k = 0; k <= pieces; k++)
                {
                    var point = math.lerp(path[i], path[i + 1], (float)k / pieces);
                    for (var r = 0; r < count; r++)
                        if (r != from && rooms[r].Shape.Contains(point, rooms[r].Boundary))
                            return r;
                }
            }
            return 0;
        }

        /// <summary>Moves interior polyline points to at least distance from the boundary.</summary>
        private static List<float2> KeepFromBoundary(Context context, List<float2> line,
            float distance)
        {
            var geometry = context.Geometry;
            var result = new List<float2>(line);
            for (var i = 1; i + 1 < result.Count; i++)
            {
                var point = result[i];
                for (var attempt = 0; attempt < 8
                    && geometry.DistanceToBoundary(point) < distance; attempt++)
                {
                    var edge = geometry.NearestEdge(point);
                    point += geometry.InwardNormal(edge) * 0.4f;
                }
                if (geometry.Contains(point)) result[i] = point;
            }
            return result;
        }

        private static bool StripInside(Context context, List<float2> strip)
        {
            foreach (var point in PlazaShape.Densify(strip, 1f))
                if (!PolygonMath.PointInsideOrBoundary(point, context.Geometry.Polygon, 0.05f))
                    return false;
            return true;
        }

        /// <summary>Part of the path outside both rooms.</summary>
        private static List<float2> ClipBetween(List<float2> path, PlazaRoom from,
            PlazaRoom to)
        {
            var result = new List<float2>();
            var dense = new List<float2>();
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var pieces = math.max(1, (int)math.ceil(math.distance(path[i], path[i + 1]) / 0.5f));
                for (var k = 0; k < pieces; k++)
                    dense.Add(math.lerp(path[i], path[i + 1], (float)k / pieces));
            }
            dense.Add(path[path.Count - 1]);
            foreach (var point in dense)
            {
                if (from.Shape.Contains(point, from.Boundary)
                    || to.Shape.Contains(point, to.Boundary)) continue;
                result.Add(point);
            }
            return SimplifyPolyline(result, 0.3f);
        }

        private static List<float2> SimplifyPolyline(List<float2> line, float tolerance)
        {
            if (line.Count < 3) return line;
            var keep = new bool[line.Count];
            keep[0] = keep[line.Count - 1] = true;
            MarkPolyline(line, 0, line.Count - 1, tolerance, keep);
            var result = new List<float2>();
            for (var i = 0; i < line.Count; i++) if (keep[i]) result.Add(line[i]);
            return result;
        }

        private static void MarkPolyline(List<float2> line, int start, int end,
            float tolerance, bool[] keep)
        {
            if (end - start < 2) return;
            var worst = -1;
            var worstDistance = tolerance * tolerance;
            for (var i = start + 1; i < end; i++)
            {
                var distance = PolygonMath.DistanceToSegmentSquared(line[i], line[start],
                    line[end]);
                if (distance <= worstDistance) continue;
                worstDistance = distance;
                worst = i;
            }
            if (worst < 0) return;
            keep[worst] = true;
            MarkPolyline(line, start, worst, tolerance, keep);
            MarkPolyline(line, worst, end, tolerance, keep);
        }

        /// <summary>Polygon of a polyline widened to width (mitred joins).</summary>
        private static List<float2> Strip(List<float2> line, float width)
        {
            var half = width * 0.5f;
            var left = new List<float2>();
            var right = new List<float2>();
            for (var i = 0; i < line.Count; i++)
            {
                var before = i > 0 ? math.normalizesafe(line[i] - line[i - 1]) : float2.zero;
                var after = i + 1 < line.Count
                    ? math.normalizesafe(line[i + 1] - line[i]) : float2.zero;
                var direction = math.normalizesafe(before + after);
                var normal = new float2(-direction.y, direction.x);
                var scale = 1f;
                if (i > 0 && i + 1 < line.Count)
                    scale = 1f / math.max(0.5f, math.dot(normal, new float2(-after.y, after.x)));
                left.Add(line[i] + normal * (half * scale));
                right.Add(line[i] - normal * (half * scale));
            }
            right.Reverse();
            left.AddRange(right);
            if (PolygonMath.SignedArea(left) < 0.0) left.Reverse();
            return IsSimple(left) ? left : null;
        }

        // ------------------------------------------------- entrance access

        /// <summary>
        /// Every entrance leads into its nearest room. A long approach gets
        /// an entrance court, so the entrance becomes a place of its own.
        /// </summary>
        private static void ConnectEntrances(Context context)
        {
            var design = context.Design;
            var courts = 0;
            for (var e = 0; e < context.Movement.Entrances.Count; e++)
            {
                var entrance = context.Movement.Entrances[e];
                List<float2> best = null;
                var bestRoom = 0;
                var bestLength = float.MaxValue;
                for (var r = 0; r < design.Rooms.Count; r++)
                {
                    var room = design.Rooms[r];
                    if (room.Role == PlazaRoomRole.Passage) continue;
                    var path = context.Movement.ShortestPath(entrance, room.Center);
                    if (path == null) continue;
                    var clipped = new List<float2>();
                    ClipAtRoom(path, room, clipped);
                    var length = PlazaMovement.PolylineLength(clipped)
                        / math.max(0.3f, room.Importance);
                    if (length >= bestLength) continue;
                    bestLength = length;
                    best = clipped;
                    bestRoom = r;
                }
                var corridor = new PlazaCorridor
                {
                    Width = context.Rules.CorridorWidth, Entrance = e, Room = bestRoom,
                };
                if (best == null || best.Count < 2)
                {
                    corridor.Points.Add(entrance);
                    corridor.Points.Add(entrance);
                }
                else corridor.Points.AddRange(best);
                design.MovementCorridors.Add(corridor);

                var approach = PlazaMovement.PolylineLength(corridor.Points);
                if (approach >= 14f && courts < 2 && design.Grammar != PlazaGrammar.Natural
                    && context.Geometry.Area >= 1500f
                    && TryAddEntranceCourt(context, entrance, bestRoom)) courts++;
            }
        }

        private static bool TryAddEntranceCourt(Context context, float2 entrance, int target)
        {
            var geometry = context.Geometry;
            var edge = geometry.NearestEdge(entrance);
            var inward = geometry.InwardNormal(edge);
            var along = new float2(inward.y, -inward.x);
            var shape = new PlazaShape
            {
                Kind = PlazaCoreShape.Rectangle, Axis = along, HalfLength = 5f,
                HalfWidth = 3.5f, Center = entrance + inward * 4f,
            };
            var request = new RoomRequest { Rim = 0.3f, AvoidGap = 3f };
            request.Avoid.AddRange(context.Design.Rooms);
            if (!Fits(context, request, shape)) return false;
            AddRoom(context, shape, PlazaRoomRole.EntranceCourt, 0.25f);
            Link(context, target, context.Design.Rooms.Count - 1);
            return true;
        }

        /// <summary>Copies path up to the point where it enters the room.</summary>
        private static void ClipAtRoom(List<float2> path, PlazaRoom room, List<float2> result)
        {
            result.Add(path[0]);
            if (room.Shape.Contains(path[0], room.Boundary)) return;
            for (var i = 1; i < path.Count; i++)
            {
                if (!room.Shape.Contains(path[i], room.Boundary))
                {
                    result.Add(path[i]);
                    continue;
                }
                var a = path[i - 1];
                var b = path[i];
                float low = 0f, high = 1f;
                for (var step = 0; step < 20; step++)
                {
                    var middle = (low + high) * 0.5f;
                    if (room.Shape.Contains(math.lerp(a, b, middle), room.Boundary)) high = middle;
                    else low = middle;
                }
                result.Add(math.lerp(a, b, high));
                return;
            }
        }
    }
}
