using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        /// <summary>
        /// Green zones are what remains once the spaces, a paved gap around
        /// them, the corridors and the entrances are kept free. The grid only
        /// decides which areas qualify; their outlines are traced, simplified
        /// and checked for valid topology.
        /// </summary>
        private static void BuildGreenZones(PlazaDesign design, PlazaGeometry geometry,
            StyleRules rules, List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            var columns = geometry.Columns;
            var rows = geometry.Rows;
            var green = new bool[columns * rows];
            for (var index = 0; index < green.Length; index++)
            {
                if (!geometry.Inside[index]) continue;
                var point = geometry.CellCenter(index);
                if (InsideAny(point, spaces, outlines, GreenGap)) continue;
                if (NearAnyCorridor(design, point, 0.75f)) continue;
                if (NearEntrance(design, geometry, point)) continue;
                green[index] = true;
            }

            // Opening removes slivers thinner than two cells.
            var minimumCells = (int)math.ceil(2f / geometry.CellSize);
            green = Dilate(Erode(green, columns, rows, minimumCells), columns, rows,
                minimumCells);
            for (var index = 0; index < green.Length; index++)
                green[index] &= geometry.Inside[index];
            OpenEnclosedHoles(green, columns, rows);

            var minimumArea = math.max(MinimumGreenArea, geometry.Area * 0.02f);
            foreach (var loop in TraceContours(green, columns, rows))
            {
                var world = new List<float2>(loop.Count);
                for (var i = 0; i < loop.Count; i++)
                    world.Add(geometry.Origin + ((float2)loop[i] * 0.5f + 0.5f)
                        * geometry.CellSize);
                if (PolygonMath.SignedArea(world) <= 0.0) continue;
                var rawArea = PlazaShape.Area(world);
                if (rawArea < minimumArea) continue;
                // From smoothest to raw: simplification and smoothing may cut
                // corners, so every option must still keep the spaces free.
                var simplified = Simplify(world, math.max(0.4f, geometry.CellSize * 0.6f));
                var options = new List<List<float2>>();
                if (rules.OrganicGreen) options.Add(Chaikin(simplified));
                options.Add(simplified);
                options.Add(world);
                List<float2> accepted = null;
                for (var i = 0; i < options.Count && accepted == null; i++)
                {
                    var snapped = SnapToBoundary(geometry, options[i],
                        geometry.CellSize * 0.9f);
                    foreach (var candidate in new[] { snapped, options[i] })
                    {
                        if (!IsSimple(candidate)
                            || PlazaShape.Area(candidate) < rawArea * 0.85f
                            || !KeepsClear(design, candidate, spaces, outlines)) continue;
                        accepted = candidate;
                        break;
                    }
                }
                if (accepted != null) design.GreenZones.Add(accepted);
            }
        }

        /// <summary>
        /// A green zone may not reach into a space or across a corridor.
        /// </summary>
        private static bool KeepsClear(PlazaDesign design, List<float2> zone,
            List<PlazaShape> spaces, List<List<float2>> outlines)
        {
            for (var i = 0; i < zone.Count; i++)
                if (InsideAny(zone[i], spaces, outlines, 0f)) return false;
            for (var s = 0; s < outlines.Count; s++)
                foreach (var point in PlazaShape.Densify(outlines[s], 1f))
                    if (PolygonMath.PointInside(point, zone)) return false;
            for (var c = 0; c < design.MovementCorridors.Count; c++)
            {
                var points = design.MovementCorridors[c].Points;
                for (var i = 0; i + 1 < points.Count; i++)
                {
                    var length = math.distance(points[i], points[i + 1]);
                    var pieces = math.max(1, (int)math.ceil(length / 0.5f));
                    for (var k = 0; k <= pieces; k++)
                        if (PolygonMath.PointInside(math.lerp(points[i], points[i + 1],
                                (float)k / pieces), zone)) return false;
                }
            }
            return true;
        }

        private static bool NearAnyCorridor(PlazaDesign design, float2 point, float extra)
        {
            for (var i = 0; i < design.MovementCorridors.Count; i++)
            {
                var corridor = design.MovementCorridors[i];
                if (PlazaMovement.DistanceToPolyline(point, corridor.Points)
                    < corridor.Width * 0.5f + extra) return true;
            }
            return false;
        }

        private static bool NearEntrance(PlazaDesign design, PlazaGeometry geometry,
            float2 point)
        {
            for (var i = 0; i < design.MovementCorridors.Count; i++)
            {
                var corridor = design.MovementCorridors[i];
                if (corridor.Entrance < 0) continue;
                if (math.distance(point, corridor.Points[0]) < EntranceClearance)
                    return true;
            }
            return false;
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
