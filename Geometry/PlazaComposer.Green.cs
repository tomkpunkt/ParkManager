using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        /// <summary>Green share of the plot already placed, against the grammar's target.</summary>
        private sealed class GreenBudget
        {
            internal float Used;
            internal float Minimum;
            internal float Maximum;
            internal float Left => Maximum - Used;
        }

        /// <summary>
        /// Green is designed, not left over: beds along the edges chosen for
        /// green, formal structure inside large squares, and organic gardens
        /// for the natural grammar, all within the grammar's green budget.
        /// Everything else stays paved.
        /// </summary>
        private static void ComposeGreen(Context context)
        {
            var design = context.Design;
            var geometry = context.Geometry;
            var budget = new GreenBudget
            {
                Minimum = geometry.Area * context.Rules.GreenMinimum,
                Maximum = geometry.Area * context.Rules.GreenMaximum,
            };
            var natural = design.Grammar == PlazaGrammar.Natural;
            var order = new List<int>();
            for (var r = 0; r < design.Rooms.Count; r++)
                if (design.Rooms[r].Role != PlazaRoomRole.Passage) order.Add(r);
            order.Sort((a, b) => design.Rooms[b].Importance.CompareTo(design.Rooms[a].Importance));

            if (natural) AddGardens(context, budget);
            else
                foreach (var r in order)
                foreach (var edge in design.Rooms[r].Edges)
                    if (edge.Role == PlazaEdgeRole.GreenBorder)
                        AddEdgeBeds(context, r, edge, budget, context.Rules.BedDepth);

            AddInnerBeds(context, budget);

            // Below the target: lawns behind the tree rows first, then behind
            // seating and built edges. Open edges always stay paved.
            foreach (var role in new[] { PlazaEdgeRole.TreeRow, PlazaEdgeRole.SeatingEdge,
                         PlazaEdgeRole.ArchitecturalEdge })
            {
                if (natural || budget.Used >= budget.Minimum) break;
                foreach (var r in order)
                foreach (var edge in design.Rooms[r].Edges)
                    if (edge.Role == role && edge.Rim >= 6f && budget.Used < budget.Minimum)
                        AddEdgeBeds(context, r, edge, budget, context.Rules.BedDepth);
            }

            // Still short: deliberate green in the large paved remainders.
            if (budget.Used < budget.Minimum) FillLeftovers(context, budget);
        }

        /// <summary>
        /// A bed parallel to a room edge, a paved walk away from it, split
        /// wherever a corridor or an entrance crosses the rim.
        /// </summary>
        private static void AddEdgeBeds(Context context, int roomIndex, PlazaRoomEdge edge,
            GreenBudget budget, float maximumDepth)
        {
            if (budget.Left < 15f) return;
            var room = context.Design.Rooms[roomIndex];
            var outward = edge.Outward;
            var tangent = new float2(-outward.y, outward.x);
            var center = room.Center;
            float tMin = float.MaxValue, tMax = float.MinValue, oMax = float.MinValue;
            foreach (var point in edge.Points)
            {
                var d = point - center;
                tMin = math.min(tMin, math.dot(d, tangent));
                tMax = math.max(tMax, math.dot(d, tangent));
                oMax = math.max(oMax, math.dot(d, outward));
            }
            var depth = math.min(maximumDepth, edge.Rim - GreenGap - 1f);
            if (depth < 2.5f) return;
            var start = oMax + GreenGap;
            var modern = context.Design.Grammar == PlazaGrammar.Modern;
            var length = tMax - tMin;
            var half = length * (modern ? 0.46f : 0.41f);
            var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed,
                0x9b05688cu ^ (uint)(roomIndex * 7 + (int)(math.atan2(outward.y, outward.x) * 10f))));
            var middle = (tMin + tMax) * 0.5f
                + (modern ? (random.NextBool() ? 1f : -1f) * length * 0.05f : 0f);
            var shear = modern ? 0.6f : 0f;

            float2 At(float t, float o) => center + tangent * (t + shear * (o - start))
                + outward * o;

            // Shrink until the bed lies inside the plot.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var inside = true;
                foreach (var t in new[] { middle - half, middle, middle + half })
                foreach (var o in new[] { start, start + depth })
                {
                    var point = At(t, o);
                    if (context.Geometry.Contains(point)
                        && context.Geometry.DistanceToBoundary(point) >= 0.8f) continue;
                    inside = false;
                }
                if (inside) break;
                if (attempt % 2 == 0) depth *= 0.85f;
                else half *= 0.88f;
            }
            if (depth < 2.5f) return;

            // Free stretches along the edge become separate beds.
            const float step = 1f;
            var steps = (int)math.floor(half * 2f / step);
            var runStart = float.NaN;
            // One extra step past the end closes the last free stretch.
            for (var i = 0; i <= steps + 1; i++)
            {
                var t = middle - half + i * step;
                var free = i <= steps;
                for (var k = 0; k <= 2 && free; k++)
                    free = BedPointFree(context, At(t, start + depth * k / 2f));
                if (free && float.IsNaN(runStart)) runStart = t;
                if (free || float.IsNaN(runStart)) continue;
                var runEnd = t - step;
                AddBed(context, roomIndex, budget, At, runStart, runEnd, start, depth,
                    PlazaGreenKind.Border);
                runStart = float.NaN;
            }
        }

        private static void AddBed(Context context, int roomIndex, GreenBudget budget,
            System.Func<float, float, float2> at, float t0, float t1, float o0, float depth,
            PlazaGreenKind kind)
        {
            if (t1 - t0 < 4f || budget.Left < 15f) return;
            // Respect the budget by shortening the bed.
            var area = (t1 - t0) * depth;
            if (area > budget.Left)
            {
                var shorter = budget.Left / depth;
                if (shorter < 4f) return;
                var cut = (t1 - t0 - shorter) * 0.5f;
                t0 += cut;
                t1 -= cut;
            }
            var zone = new PlazaGreenZone { Kind = kind, Room = roomIndex };
            zone.Polygon.Add(at(t0, o0));
            zone.Polygon.Add(at(t1, o0));
            zone.Polygon.Add(at(t1, o0 + depth));
            zone.Polygon.Add(at(t0, o0 + depth));
            if (PolygonMath.SignedArea(zone.Polygon) < 0.0) zone.Polygon.Reverse();
            if (!KeepsClear(context, zone.Polygon, kind != PlazaGreenKind.Border)) return;
            context.Design.GreenZones.Add(zone);
            budget.Used += PlazaShape.Area(zone.Polygon);
        }

        private static bool BedPointFree(Context context, float2 point)
            => context.Geometry.Contains(point)
                && context.Geometry.DistanceToBoundary(point) >= 0.8f
                && !InsideAnyRoom(context, point, GreenGap * 0.9f)
                && !NearCorridor(context, point, 1f)
                && !NearEntrance(context, point, EntranceClearance)
                && !InGreen(context, point);

        /// <summary>
        /// Large squares get structure inside: formal lawn panels flanking a
        /// free central band (classic), one long planting island (modern) or
        /// an organic garden (natural). The open square stays connected.
        /// </summary>
        private static void AddInnerBeds(Context context, GreenBudget budget)
        {
            var design = context.Design;
            if (design.Rooms.Count == 0 || budget.Left < 30f) return;
            var main = design.Rooms[0];
            var kind = main.Shape.Kind;
            if (kind != PlazaCoreShape.Rectangle && kind != PlazaCoreShape.ChamferedRectangle
                && kind != PlazaCoreShape.Parallelogram && kind != PlazaCoreShape.Ellipse)
                return;
            var length = main.Shape.HalfLength;
            var width = main.Shape.HalfWidth;
            if (PlazaShape.Area(main.Boundary) < 1600f || width * 2f < 34f) return;
            var u = main.Axis;
            var v = new float2(-u.y, u.x);
            var c = main.Center;
            var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed, 0x1f83d9abu));

            switch (design.Grammar)
            {
                case PlazaGrammar.Classic:
                {
                    var panels = length >= 35f ? 2 : 1;
                    var gap = length * 0.14f;
                    var panelLength = (length * 1.2f - gap * (panels - 1)) / panels;
                    for (var side = -1; side <= 1; side += 2)
                    for (var p = 0; p < panels; p++)
                    {
                        var t0 = -length * 0.6f + p * (panelLength + gap);
                        AddInnerQuad(context, budget, c, u, v, t0, t0 + panelLength,
                            side * width * 0.38f, side * width * 0.68f, 0f,
                            PlazaGreenKind.Parterre);
                    }
                    break;
                }
                case PlazaGrammar.Modern:
                {
                    var side = random.NextBool() ? 1f : -1f;
                    var shift = (random.NextBool() ? 1f : -1f) * length * 0.12f;
                    var islandWidth = math.clamp(width * 0.16f, 3f, 8f);
                    var offset = side * width * 0.42f;
                    AddInnerQuad(context, budget, c, u, v, shift - length * 0.55f,
                        shift + length * 0.55f, offset - islandWidth * 0.5f,
                        offset + islandWidth * 0.5f, 0.5f, PlazaGreenKind.Island);
                    break;
                }
                default:
                {
                    var su = random.NextBool() ? 1f : -1f;
                    var sv = random.NextBool() ? 1f : -1f;
                    var centre = c + u * (length * 0.42f * su) + v * (width * 0.32f * sv);
                    var garden = new List<float2>();
                    for (var i = 0; i < 12; i++)
                    {
                        var angle = 2f * math.PI * i / 12f;
                        var wobble = 1f + 0.12f * math.sin(angle * 3f + su);
                        garden.Add(centre + u * (math.cos(angle) * length * 0.26f * wobble)
                            + v * (math.sin(angle) * width * 0.22f * wobble));
                    }
                    AddInnerPolygon(context, budget, Chaikin(garden), PlazaGreenKind.Garden);
                    break;
                }
            }
        }

        private static void AddInnerQuad(Context context, GreenBudget budget, float2 c,
            float2 u, float2 v, float t0, float t1, float s0, float s1, float shear,
            PlazaGreenKind kind)
        {
            var depth = math.abs(s1 - s0);
            float2 At(float t, float s) => c + u * (t + shear * (s - s0)) + v * s;
            var quad = new List<float2> { At(t0, s0), At(t1, s0), At(t1, s1), At(t0, s1) };
            if (PolygonMath.SignedArea(quad) < 0.0) quad.Reverse();
            if (PlazaShape.Area(quad) > budget.Left)
            {
                var keep = budget.Left / math.max(1f, depth);
                if (keep < 6f) return;
                var cut = (t1 - t0 - keep) * 0.5f;
                quad = new List<float2> { At(t0 + cut, s0), At(t1 - cut, s0),
                    At(t1 - cut, s1), At(t0 + cut, s1) };
                if (PolygonMath.SignedArea(quad) < 0.0) quad.Reverse();
            }
            AddInnerPolygon(context, budget, quad, kind);
        }

        /// <summary>Adds a bed inside the main square, well clear of its edge.</summary>
        private static void AddInnerPolygon(Context context, GreenBudget budget,
            List<float2> polygon, PlazaGreenKind kind)
        {
            var main = context.Design.Rooms[0];
            foreach (var point in PlazaShape.Densify(polygon, 1f))
            {
                if (!main.Shape.Contains(point, main.Boundary)
                    || DistanceToOutline(point, main.Boundary) < 2.5f
                    || NearCorridor(context, point, 4f)) return;
            }
            var area = PlazaShape.Area(polygon);
            if (area > budget.Left || !IsSimple(polygon)) return;
            var zone = new PlazaGreenZone { Kind = kind, Room = 0 };
            zone.Polygon.AddRange(polygon);
            context.Design.GreenZones.Add(zone);
            budget.Used += area;
        }

        /// <summary>
        /// Natural grammar: organic gardens behind the green edges, traced
        /// from the grid, smoothed and checked like every other bed.
        /// </summary>
        private static void AddGardens(Context context, GreenBudget budget)
        {
            var geometry = context.Geometry;
            var design = context.Design;
            var columns = geometry.Columns;
            var rows = geometry.Rows;
            var green = new bool[columns * rows];
            for (var index = 0; index < green.Length; index++)
            {
                if (!geometry.Inside[index]) continue;
                var point = geometry.CellCenter(index);
                if (InsideAnyRoom(context, point, GreenGap + 0.5f)
                    || NearCorridor(context, point, 1f)
                    || NearEntrance(context, point, EntranceClearance)
                    || geometry.Distance[index] < 0.8f) continue;
                green[index] = NearestEdgeRole(context, point) == PlazaEdgeRole.GreenBorder;
            }
            var radius = (int)math.ceil(2f / geometry.CellSize);
            green = Dilate(Erode(green, columns, rows, radius), columns, rows, radius);
            for (var index = 0; index < green.Length; index++) green[index] &= geometry.Inside[index];
            OpenEnclosedHoles(green, columns, rows);

            var loops = new List<List<float2>>();
            foreach (var loop in TraceContours(green, columns, rows))
            {
                var world = new List<float2>(loop.Count);
                foreach (var p in loop)
                    world.Add(geometry.Origin + ((float2)p * 0.5f + 0.5f) * geometry.CellSize);
                if (PolygonMath.SignedArea(world) > 0.0 && PlazaShape.Area(world) >= 30f)
                    loops.Add(world);
            }
            loops.Sort((a, b) => PlazaShape.Area(b).CompareTo(PlazaShape.Area(a)));
            foreach (var world in loops)
            {
                var rawArea = PlazaShape.Area(world);
                if (rawArea > budget.Left) continue;
                var simplified = Simplify(world, math.max(0.4f, geometry.CellSize * 0.6f));
                var options = new List<List<float2>> { Chaikin(Chaikin(simplified)),
                    Chaikin(simplified), simplified, world };
                foreach (var option in options)
                {
                    var snapped = SnapToBoundary(geometry, option, geometry.CellSize * 0.9f);
                    var accepted = false;
                    foreach (var candidate in new[] { snapped, option })
                    {
                        if (!IsSimple(candidate)
                            || PlazaShape.Area(candidate) < rawArea * 0.8f
                            || !KeepsClear(context, candidate, false)) continue;
                        var zone = new PlazaGreenZone
                        {
                            Kind = PlazaGreenKind.Garden,
                            Room = NearestRoom(context, AreaCentre(candidate)),
                        };
                        zone.Polygon.AddRange(candidate);
                        design.GreenZones.Add(zone);
                        budget.Used += PlazaShape.Area(candidate);
                        accepted = true;
                        break;
                    }
                    if (accepted) break;
                }
            }
        }

        /// <summary>
        /// Large paved remainders far from every room read as unplanned. Below
        /// the green target, each becomes one deliberately shaped green area:
        /// a lawn panel (classic), a sheared bed (modern) or a garden
        /// (natural), fitted into the free space like a room.
        /// </summary>
        private static void FillLeftovers(Context context, GreenBudget budget)
        {
            var geometry = context.Geometry;
            var free = new bool[geometry.Inside.Length];
            for (var index = 0; index < free.Length; index++)
            {
                if (!geometry.Inside[index] || geometry.Distance[index] < 1f) continue;
                var point = geometry.CellCenter(index);
                free[index] = !InsideAnyRoom(context, point, GreenGap + 1f)
                    && !NearCorridor(context, point, 1.5f)
                    && !NearEntrance(context, point, EntranceClearance + 1f)
                    && !NearGreen(context, point, 1.5f);
            }
            bool FreeAt(float2 point)
                => geometry.TryCell(point, out var cell) && free[cell];

            var cellArea = geometry.CellSize * geometry.CellSize;
            foreach (var component in Components(free, geometry.Columns, geometry.Rows))
            {
                if (budget.Used >= budget.Minimum || budget.Left < 30f) return;
                if (component.Count * cellArea < 120f) continue;
                // The freest point of the remainder anchors the figure.
                var best = component[0];
                var bestClearance = -1f;
                foreach (var index in component)
                {
                    var clearance = ClearanceInside(geometry, free, index);
                    if (clearance <= bestClearance) continue;
                    bestClearance = clearance;
                    best = index;
                }
                var grammar = context.Design.Grammar;
                var request = new RoomRequest
                {
                    Shapes = grammar == PlazaGrammar.Classic ? new[] { PlazaCoreShape.Rectangle }
                        : grammar == PlazaGrammar.Modern
                            ? new[] { PlazaCoreShape.Parallelogram }
                            : new[] { PlazaCoreShape.Ellipse },
                    Rim = 0.8f, MinimumWidth = 4f, MinimumArea = 30f,
                    TargetArea = math.min(budget.Left, math.min(component.Count * cellArea * 0.7f,
                        budget.Minimum - budget.Used + 40f)),
                    Constraint = FreeAt, Salt = 5u,
                };
                request.Centres.Add(geometry.CellCenter(best));
                request.Axes.Add(geometry.MajorAxis);
                request.Axes.Add(geometry.MinorAxis);
                if (grammar == PlazaGrammar.Modern)
                    request.Axes.Add(Rotate(geometry.MajorAxis, math.radians(25f)));
                request.Aspects.Add(1f);
                request.Aspects.Add(0.6f);
                request.Aspects.Add(0.4f);
                if (!FitRoom(context, request, out var shape)) continue;
                var polygon = shape.Outline();
                if (!IsSimple(polygon) || !KeepsClear(context, polygon, false)) continue;
                var zone = new PlazaGreenZone
                {
                    Kind = PlazaGreenKind.Garden,
                    Room = NearestRoom(context, shape.Center),
                };
                zone.Polygon.AddRange(polygon);
                context.Design.GreenZones.Add(zone);
                budget.Used += PlazaShape.Area(polygon);
            }
        }

        private static bool NearGreen(Context context, float2 point, float distance)
        {
            foreach (var zone in context.Design.GreenZones)
                if (PolygonMath.PointInside(point, zone.Polygon)
                    || DistanceToOutline(point, zone.Polygon) < distance) return true;
            return false;
        }

        /// <summary>Connected components of a mask, largest first.</summary>
        private static List<List<int>> Components(bool[] mask, int columns, int rows)
        {
            var seen = new bool[mask.Length];
            var result = new List<List<int>>();
            for (var start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                seen[start] = true;
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    component.Add(index);
                    var x = index % columns;
                    var y = index / columns;
                    foreach (var next in new[] { x > 0 ? index - 1 : -1,
                                 x + 1 < columns ? index + 1 : -1,
                                 y > 0 ? index - columns : -1,
                                 y + 1 < rows ? index + columns : -1 })
                    {
                        if (next < 0 || !mask[next] || seen[next]) continue;
                        seen[next] = true;
                        queue.Enqueue(next);
                    }
                }
                result.Add(component);
            }
            result.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count)
                : a[0].CompareTo(b[0]));
            return result;
        }

        /// <summary>Distance in metres from a free cell to the nearest blocked cell.</summary>
        private static float ClearanceInside(PlazaGeometry geometry, bool[] free, int index)
        {
            var x = index % geometry.Columns;
            var y = index / geometry.Columns;
            for (var radius = 1; radius < 64; radius++)
            for (var d = -radius; d <= radius; d++)
                foreach (var (cx, cy) in new[] { (x + d, y - radius), (x + d, y + radius),
                             (x - radius, y + d), (x + radius, y + d) })
                    if (cx < 0 || cy < 0 || cx >= geometry.Columns || cy >= geometry.Rows
                        || !free[cy * geometry.Columns + cx])
                        return radius * geometry.CellSize;
            return 64f * geometry.CellSize;
        }

        private static float2 AreaCentre(List<float2> polygon)
            => PlazaGeometry.AreaCentroid(polygon);

        private static int NearestRoom(Context context, float2 point)
        {
            var best = 0;
            var bestDistance = float.MaxValue;
            for (var r = 0; r < context.Design.Rooms.Count; r++)
            {
                var distance = DistanceToOutline(point, context.Design.Rooms[r].Boundary);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = r;
            }
            return best;
        }

        /// <summary>Role of the room edge closest to a point.</summary>
        private static PlazaEdgeRole NearestEdgeRole(Context context, float2 point)
        {
            var role = PlazaEdgeRole.Open;
            var best = float.MaxValue;
            foreach (var room in context.Design.Rooms)
            foreach (var edge in room.Edges)
            {
                var distance = PlazaMovement.DistanceToPolyline(point, edge.Points);
                if (distance >= best) continue;
                best = distance;
                role = edge.Role;
            }
            return role;
        }

        private static bool InGreen(Context context, float2 point)
        {
            foreach (var zone in context.Design.GreenZones)
                if (PolygonMath.PointInside(point, zone.Polygon)) return true;
            return false;
        }

        /// <summary>
        /// Green may not reach into a room (unless it is an inner bed of the
        /// main square), across a corridor or into other green.
        /// </summary>
        private static bool KeepsClear(Context context, List<float2> zone, bool inner)
        {
            var rooms = context.Design.Rooms;
            for (var r = 0; r < rooms.Count; r++)
            {
                if (inner && r == 0) continue;
                var room = rooms[r];
                foreach (var point in zone)
                    if (room.Shape.Contains(point, room.Boundary)) return false;
                foreach (var point in PlazaShape.Densify(room.Boundary, 1f))
                    if (PolygonMath.PointInside(point, zone)) return false;
            }
            foreach (var corridor in context.Design.MovementCorridors)
            {
                var points = corridor.Points;
                for (var i = 0; i + 1 < points.Count; i++)
                {
                    var pieces = math.max(1, (int)math.ceil(math.distance(points[i],
                        points[i + 1]) / 0.5f));
                    for (var k = 0; k <= pieces; k++)
                        if (PolygonMath.PointInside(math.lerp(points[i], points[i + 1],
                                (float)k / pieces), zone)) return false;
                }
            }
            foreach (var other in context.Design.GreenZones)
            {
                foreach (var point in zone)
                    if (PolygonMath.PointInside(point, other.Polygon)) return false;
                foreach (var point in other.Polygon)
                    if (PolygonMath.PointInside(point, zone)) return false;
            }
            return true;
        }

        private static bool[] Erode(bool[] mask, int columns, int rows, int radius)
            => Morph(mask, columns, rows, radius, true);

        private static bool[] Dilate(bool[] mask, int columns, int rows, int radius)
            => Morph(mask, columns, rows, radius, false);

        /// <summary>Square-kernel erosion or dilation, separable per axis.</summary>
        private static bool[] Morph(bool[] mask, int columns, int rows, int radius,
            bool erode)
        {
            var temp = new bool[mask.Length];
            var result = new bool[mask.Length];
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var value = erode;
                for (var dx = -radius; dx <= radius && value == erode; dx++)
                {
                    var nx = x + dx;
                    var cell = nx >= 0 && nx < columns && mask[y * columns + nx];
                    if (cell != erode) value = !erode;
                }
                temp[y * columns + x] = value;
            }
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var value = erode;
                for (var dy = -radius; dy <= radius && value == erode; dy++)
                {
                    var ny = y + dy;
                    var cell = ny >= 0 && ny < rows && temp[ny * columns + x];
                    if (cell != erode) value = !erode;
                }
                result[y * columns + x] = value;
            }
            return result;
        }

        /// <summary>
        /// A green zone must be a simple polygon. Free cells enclosed by green
        /// (a paved island) are connected to the outside by a straight cut.
        /// </summary>
        private static void OpenEnclosedHoles(bool[] green, int columns, int rows)
        {
            for (var guard = 0; guard < 16; guard++)
            {
                var reached = new bool[green.Length];
                var queue = new Queue<int>();
                for (var x = 0; x < columns; x++)
                {
                    Seed(x, 0);
                    Seed(x, rows - 1);
                }
                for (var y = 0; y < rows; y++)
                {
                    Seed(0, y);
                    Seed(columns - 1, y);
                }
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var x = index % columns;
                    var y = index / columns;
                    Visit(x + 1, y);
                    Visit(x - 1, y);
                    Visit(x, y + 1);
                    Visit(x, y - 1);
                }
                var hole = -1;
                for (var index = 0; index < green.Length && hole < 0; index++)
                    if (!green[index] && !reached[index]) hole = index;
                if (hole < 0) return;
                // Cut towards the nearest grid side.
                var hx = hole % columns;
                var hy = hole / columns;
                var distances = new[] { hx, columns - 1 - hx, hy, rows - 1 - hy };
                var side = 0;
                for (var i = 1; i < 4; i++) if (distances[i] < distances[side]) side = i;
                var step = side == 0 ? new int2(-1, 0) : side == 1 ? new int2(1, 0)
                    : side == 2 ? new int2(0, -1) : new int2(0, 1);
                for (var p = new int2(hx, hy); p.x >= 0 && p.y >= 0 && p.x < columns
                    && p.y < rows; p += step)
                {
                    var index = p.y * columns + p.x;
                    if (reached[index]) break;
                    green[index] = false;
                }

                void Seed(int x, int y)
                {
                    var index = y * columns + x;
                    if (green[index] || reached[index]) return;
                    reached[index] = true;
                    queue.Enqueue(index);
                }

                void Visit(int x, int y)
                {
                    if (x < 0 || y < 0 || x >= columns || y >= rows) return;
                    Seed(x, y);
                }
            }
        }

        /// <summary>
        /// Marching squares over cell centres. Returns closed loops in doubled
        /// cell-index coordinates; outer loops are counter-clockwise.
        /// </summary>
        private static List<List<int2>> TraceContours(bool[] mask, int columns, int rows)
        {
            bool At(int x, int y) => x >= 0 && y >= 0 && x < columns && y < rows
                && mask[y * columns + x];
            var next = new Dictionary<long, int2>();
            long Key(int2 p) => ((long)p.x << 32) ^ (uint)p.y;
            void Segment(int2 from, int2 to) => next[Key(from)] = to;

            for (var y = -1; y < rows; y++)
            for (var x = -1; x < columns; x++)
            {
                var code = (At(x, y) ? 1 : 0) | (At(x + 1, y) ? 2 : 0)
                    | (At(x + 1, y + 1) ? 4 : 0) | (At(x, y + 1) ? 8 : 0);
                if (code == 0 || code == 15) continue;
                var bottom = new int2(2 * x + 1, 2 * y);
                var right = new int2(2 * x + 2, 2 * y + 1);
                var top = new int2(2 * x + 1, 2 * y + 2);
                var left = new int2(2 * x, 2 * y + 1);
                // Inside stays on the left of every segment.
                switch (code)
                {
                    case 1: Segment(bottom, left); break;
                    case 2: Segment(right, bottom); break;
                    case 3: Segment(right, left); break;
                    case 4: Segment(top, right); break;
                    case 5: Segment(bottom, left); Segment(top, right); break;
                    case 6: Segment(top, bottom); break;
                    case 7: Segment(top, left); break;
                    case 8: Segment(left, top); break;
                    case 9: Segment(bottom, top); break;
                    case 10: Segment(right, bottom); Segment(left, top); break;
                    case 11: Segment(right, top); break;
                    case 12: Segment(left, right); break;
                    case 13: Segment(bottom, right); break;
                    case 14: Segment(left, bottom); break;
                }
            }

            var loops = new List<List<int2>>();
            var used = new HashSet<long>();
            // Sorted keys keep the loop order deterministic.
            var keys = new List<long>(next.Keys);
            keys.Sort();
            foreach (var startKey in keys)
            {
                if (used.Contains(startKey)) continue;
                var loop = new List<int2>();
                var key = startKey;
                var guard = next.Count + 1;
                while (guard-- > 0 && used.Add(key))
                {
                    var to = next[key];
                    loop.Add(new int2((int)(key >> 32), (int)(uint)key));
                    key = Key(to);
                    if (!next.ContainsKey(key)) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        /// <summary>Douglas–Peucker for a closed loop.</summary>
        private static List<float2> Simplify(List<float2> loop, float tolerance)
        {
            if (loop.Count < 5) return new List<float2>(loop);
            // Split at the two mutually farthest points for a stable result.
            var first = 0;
            var far = 0;
            var best = -1f;
            for (var i = 0; i < loop.Count; i++)
            {
                var distance = math.distancesq(loop[0], loop[i]);
                if (distance <= best) continue;
                best = distance;
                far = i;
            }
            var keep = new bool[loop.Count];
            keep[first] = keep[far] = true;
            Mark(loop, first, far, tolerance, keep);
            Mark(loop, far, loop.Count, tolerance, keep);
            var result = new List<float2>();
            for (var i = 0; i < loop.Count; i++) if (keep[i]) result.Add(loop[i]);
            return result;
        }

        private static void Mark(List<float2> loop, int start, int end, float tolerance,
            bool[] keep)
        {
            if (end - start < 2) return;
            var a = loop[start];
            var b = loop[end % loop.Count];
            var worst = -1;
            var worstDistance = tolerance * tolerance;
            for (var i = start + 1; i < end; i++)
            {
                var distance = PolygonMath.DistanceToSegmentSquared(loop[i], a, b);
                if (distance <= worstDistance) continue;
                worstDistance = distance;
                worst = i;
            }
            if (worst < 0) return;
            keep[worst] = true;
            Mark(loop, start, worst, tolerance, keep);
            Mark(loop, worst, end, tolerance, keep);
        }

        /// <summary>One round of corner cutting for organic outlines.</summary>
        private static List<float2> Chaikin(List<float2> loop)
        {
            var result = new List<float2>(loop.Count * 2);
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                result.Add(math.lerp(a, b, 0.25f));
                result.Add(math.lerp(a, b, 0.75f));
            }
            return result;
        }

        /// <summary>Moves points close to the plot boundary onto it.</summary>
        private static List<float2> SnapToBoundary(PlazaGeometry geometry,
            List<float2> loop, float distance)
        {
            var result = new List<float2>(loop.Count);
            var polygon = geometry.Polygon;
            for (var i = 0; i < loop.Count; i++)
            {
                var point = loop[i];
                var edge = geometry.NearestEdge(point);
                var on = PolygonMath.ClosestPointOnSegment(point, polygon[edge],
                    polygon[(edge + 1) % polygon.Count]);
                result.Add(math.distance(on, point) <= distance ? on : point);
            }
            // Snapping can merge neighbours; drop duplicates.
            for (var i = result.Count - 1; i > 0 && result.Count > 3; i--)
                if (math.distancesq(result[i], result[i - 1]) < 0.01f) result.RemoveAt(i);
            return result;
        }

        /// <summary>True when no two non-adjacent edges intersect.</summary>
        internal static bool IsSimple(IReadOnlyList<float2> loop)
        {
            var count = loop.Count;
            if (count < 3) return false;
            for (var i = 0; i < count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % count];
                for (var j = i + 2; j < count; j++)
                {
                    if (i == 0 && j == count - 1) continue;
                    if (SegmentsTouch(a, b, loop[j], loop[(j + 1) % count])) return false;
                }
            }
            return true;
        }

        private static bool SegmentsTouch(float2 a, float2 b, float2 c, float2 d)
        {
            var d1 = PlazaGeometry.Cross(d - c, a - c);
            var d2 = PlazaGeometry.Cross(d - c, b - c);
            var d3 = PlazaGeometry.Cross(b - a, c - a);
            var d4 = PlazaGeometry.Cross(b - a, d - a);
            if ((d1 > 0f && d2 < 0f || d1 < 0f && d2 > 0f)
                && (d3 > 0f && d4 < 0f || d3 < 0f && d4 > 0f)) return true;
            return false;
        }
    }
}
