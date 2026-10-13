using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Ground surfaces of a composed plaza, ready to build: green areas, the
    /// squares (every room but the passages) in their own paving so the
    /// composition reads in the game, and the surrounding paving as the rest
    /// of the plot. None overlap (overlapping surfaces render unpredictably
    /// in the game), and since surfaces have no holes, each paving is split
    /// into as few simple pieces as possible.
    /// </summary>
    internal sealed class PlazaSurfacePlan
    {
        /// <summary>The surrounding paving: plot minus squares and green.</summary>
        internal readonly List<List<float2>> Paving = new List<List<float2>>();
        /// <summary>The squares' own paving, inner beds cut out; empty when not separated.</summary>
        internal readonly List<List<float2>> Square = new List<List<float2>>();
        internal readonly List<List<float2>> Green = new List<List<float2>>();
    }

    internal static class PlazaSurfaceComposer
    {
        private const float Epsilon = 1e-3f;
        /// <summary>Smaller green would only make crumbs and slivers in the paving.</summary>
        private const float MinimumGreenArea = 6f;
        private const float MinimumPavingArea = 1f;
        private const float MinimumPavingWidth = 0.2f;
        /// <summary>Shortest shared edge two paving pieces are merged along.</summary>
        private const float MinimumSeam = 0.5f;
        /// <summary>Shorter edges are dropped; they are invisible and fragile in the game.</summary>
        internal const float MinimumEdge = 0.05f;

        /// <param name="squares">Give the squares their own paving; otherwise
        /// squares and surroundings share one.</param>
        internal static PlazaSurfacePlan Compose(IReadOnlyList<float2> plot, PlazaDesign design,
            bool squares = true)
        {
            var plan = new PlazaSurfacePlan();
            var outline = Counterclockwise(plot);
            if (design != null)
                foreach (var zone in design.GreenZones)
                {
                    // A tenth of a metre is invisible; fewer corners keep the
                    // paving cuts and the game's area nodes few.
                    var green = DropShortEdges(Clean(PlazaComposer.Simplify(
                        Counterclockwise(zone.Polygon), 0.1f)));
                    green = DropSpikes(green);
                    // Flush to the edge only where nobody walks or arrives.
                    var flush = SnapToPlot(green, outline);
                    if (KeepsAccess(flush, design) && KeepsOutOfRooms(flush, green, design))
                        green = flush;
                    if (green.Count < 3 || PlazaShape.Area(green) < MinimumGreenArea
                        || !StrictlySimple(green)) continue;
                    plan.Green.Add(green);
                }
            var axis = math.normalizesafe(design != null && design.Rooms.Count > 0
                ? design.Rooms[0].Axis : new float2(1f, 0f));
            var rooms = new List<List<float2>>();
            if (squares && design != null)
                foreach (var room in design.Rooms)
                {
                    if (room.Role == PlazaRoomRole.Passage) continue;
                    var ring = DropShortEdges(Clean(Counterclockwise(room.Boundary)));
                    if (ring.Count >= 3 && StrictlySimple(ring)) rooms.Add(ring);
                }
            // Each square minus the beds inside it and the squares before it
            // (a pocket may overlap the space it opens onto).
            for (var r = 0; r < rooms.Count; r++)
            {
                var room = rooms[r];
                var inner = new List<List<float2>>();
                foreach (var green in plan.Green)
                    if (Overlaps(room, green)) inner.Add(green);
                for (var earlier = 0; earlier < r; earlier++)
                    if (Overlaps(room, rooms[earlier])) inner.Add(rooms[earlier]);
                plan.Square.AddRange(inner.Count == 0 ? new List<List<float2>> { room }
                    : LeastSeams(room, inner, axis));
            }
            // The surroundings: the plot minus squares and green.
            // Holes are the square pieces, not the raw rooms: they no longer
            // overlap each other, they only meet.
            var holes = new List<List<float2>>(plan.Green);
            holes.AddRange(plan.Square);
            if (holes.Count == 0) plan.Paving.Add(outline);
            else plan.Paving.AddRange(LeastSeams(outline, holes, axis));
            // The game lays a surface's pattern along its first edge: every
            // surface starts with the edge running most along the axis.
            foreach (var list in new[] { plan.Paving, plan.Square, plan.Green })
                for (var i = 0; i < list.Count; i++) list[i] = StartAlong(list[i], axis);
            return plan;
        }

        /// <summary>
        /// Same-material pieces show a seam in the game: cut along or across
        /// the square's axis, whichever leaves less seam.
        /// </summary>
        private static List<List<float2>> LeastSeams(List<float2> outer,
            List<List<float2>> holes, float2 axis)
        {
            // Two ways to split, each along or across the axis: slab cuts at
            // every hole, or grid regions whose borders run through the holes.
            // The split with the fewest sharp corners wins, then the one with
            // the least seam.
            var normal = new float2(-axis.y, axis.x);
            List<List<float2>> best = null;
            var bestAcute = int.MaxValue;
            var bestSeam = float.MaxValue;
            var bestIndex = -1;
            var candidates = new[]
            {
                Difference(outer, holes, axis), Difference(outer, holes, normal),
                GridDifference(outer, holes, axis), GridDifference(outer, holes, normal),
            };
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (candidate.Count == 0 || !Valid(candidate)) continue;
                var acute = AcuteCorners(candidate, outer);
                var seam = SeamLength(candidate);
                if (acute > bestAcute || acute == bestAcute && seam >= bestSeam - 0.5f) continue;
                best = candidate;
                bestAcute = acute;
                bestSeam = seam;
                bestIndex = index;
            }
            LastSplit = bestIndex;
            var slabBest = int.MaxValue;
            for (var i = 0; i < 2; i++) if (Valid(candidates[i])) slabBest = math.min(slabBest, AcuteCorners(candidates[i], outer));
            SlabAcute += slabBest == int.MaxValue ? 0 : slabBest;
            ChosenAcute += bestAcute == int.MaxValue ? 0 : bestAcute;
            // Nothing valid: the slab split along the axis, as before.
            return best ?? candidates[0];
        }

        /// <summary>Which split won last (0, 1 slabs along/across; 2, 3 grid); for statistics.</summary>
        internal static int LastSplit;
        internal static int SlabAcute, ChosenAcute;

        private static bool Valid(List<List<float2>> pieces)
        {
            foreach (var piece in pieces)
            {
                if (!StrictlySimple(piece)) return false;
                for (var i = 0; i < piece.Count; i++)
                    if (math.distance(piece[i], piece[(i + 1) % piece.Count]) < MinimumEdge * 0.99f)
                        return false;
            }
            return true;
        }

        /// <summary>Corners the game draws badly; sharper than this they flicker or break.</summary>
        internal const float SharpCorner = 15f;

        /// <summary>Corners sharper than <see cref="SharpCorner"/> not given by the plot itself.</summary>
        internal static int AcuteCorners(List<List<float2>> pieces, IReadOnlyList<float2> plot)
        {
            var count = 0;
            foreach (var piece in pieces)
                for (var i = 0; i < piece.Count; i++)
                {
                    if (CornerAngle(piece, i) >= SharpCorner) continue;
                    var given = false;
                    foreach (var corner in plot)
                        given |= math.distancesq(corner, piece[i]) < 0.01f;
                    if (!given) count++;
                }
            return count;
        }

        /// <summary>Interior angle in degrees at a corner of a counter-clockwise ring.</summary>
        internal static float CornerAngle(List<float2> ring, int i)
        {
            var previous = ring[(i + ring.Count - 1) % ring.Count] - ring[i];
            var next = ring[(i + 1) % ring.Count] - ring[i];
            var angle = math.degrees(math.atan2(previous.y, previous.x) - math.atan2(next.y, next.x));
            angle = (angle % 360f + 360f) % 360f;
            return PolygonMath.SignedArea(ring) < 0.0 ? 360f - angle : angle;
        }

        /// <summary>
        /// A ring that touches itself at a corner (a hole meets a region
        /// border in a single point) is two rings joined there: split it.
        /// </summary>
        private static List<List<float2>> SplitPinches(List<float2> ring)
        {
            for (var i = 0; i < ring.Count; i++)
            for (var j = i + 2; j < ring.Count; j++)
            {
                if (i == 0 && j == ring.Count - 1) continue;
                if (math.distancesq(ring[i], ring[j]) > Epsilon * Epsilon) continue;
                var first = new List<float2>();
                for (var k = i; k < j; k++) first.Add(ring[k]);
                var second = new List<float2>();
                for (var k = j; k != i; k = (k + 1) % ring.Count) second.Add(ring[k]);
                var result = new List<List<float2>>();
                foreach (var part in new[] { first, second })
                {
                    var cleaned = Clean(part);
                    if (cleaned.Count >= 3 && PolygonMath.SignedArea(cleaned) > 0.0)
                        result.AddRange(SplitPinches(cleaned));
                }
                return result;
            }
            return new List<List<float2>> { ring };
        }

        /// <summary>Grid spacing the region borders snap to.</summary>
        private const float GridSize = 2f;

        /// <summary>
        /// The plot minus the holes, split along grid lines into a few large
        /// regions so that no region encloses a hole: every hole is crossed by
        /// a region border through its middle third. Where a border meets a
        /// hole it cuts the contour steeply, never along it, so no sliver
        /// corner arises. Each region minus its holes is one simple piece.
        /// </summary>
        internal static List<List<float2>> GridDifference(IReadOnlyList<float2> outer,
            IReadOnlyList<List<float2>> holes, float2 axis)
        {
            var normal = new float2(-axis.y, axis.x);
            float2 ToLocal(float2 p) => new float2(math.dot(p, axis), math.dot(p, normal));
            float2 ToWorld(float2 p) => axis * p.x + normal * p.y;
            List<float2> Local(IReadOnlyList<float2> ring)
            {
                var result = new List<float2>(ring.Count);
                foreach (var p in ring) result.Add(ToLocal(p));
                return result;
            }
            var rings = new List<List<float2>> { Local(outer) };
            foreach (var hole in holes) rings.Add(Local(hole));
            PolygonMath.Bounds(rings[0], out var min, out var max);
            var boxes = new List<float4>();
            for (var i = 1; i < rings.Count; i++)
            {
                PolygonMath.Bounds(rings[i], out var low, out var high);
                boxes.Add(new float4(low, high));
            }
            var regions = new List<float4>();
            SplitRegion(new float4(min - 1f, max + 1f), boxes, rings, min, regions, 0);

            var pieces = new List<List<float2>>();
            foreach (var region in regions)
            {
                var clip = new List<float2> { region.xy, new float2(region.z, region.y),
                    region.zw, new float2(region.x, region.w) };
                var clipped = new List<List<float2>>(rings) { clip };
                // A region encloses no hole: its outline is one piece, without
                // helper cuts; if it is not simple after all, the chained
                // trapezoids stand in.
                var chained = Trapezoids(clipped, clipped.Count - 1);
                var outline = Outline(clipped, clipped.Count - 1);
                var simple = outline.Count > 0;
                foreach (var ring in outline) simple &= StrictlySimple(ring);
                // The outline must cover exactly what the trapezoids cover:
                // a region that still encloses a hole, or a ring chained
                // wrongly at a touching point, fails this.
                var expected = 0f;
                foreach (var piece in chained) expected += PlazaShape.Area(piece);
                var covered = 0f;
                foreach (var ring in outline) covered += PlazaShape.Area(ring);
                simple &= math.abs(covered - expected) <= 0.002f * expected + 0.05f;
                pieces.AddRange(simple ? outline : MergeAlongCuts(chained));
            }
            pieces = MergeAlongCuts(pieces);
            var split = new List<List<float2>>();
            foreach (var piece in pieces) split.AddRange(SplitPinches(piece));
            var output = new List<List<float2>>();
            foreach (var piece in split)
            {
                var world = new List<float2>(piece.Count);
                foreach (var p in piece) world.Add(ToWorld(p));
                world = DropShortEdges(Clean(world));
                if (world.Count < 3 || PlazaShape.Area(world) < MinimumPavingArea
                    || PlazaShape.MinimumWidth(world) < MinimumPavingWidth) continue;
                output.Add(world);
            }
            return output;
        }

        /// <summary>
        /// Splits a region along a grid line until it encloses no hole. The
        /// line crosses an enclosed hole in its middle third, crosses every
        /// other hole it meets the same way, and keeps a metre from holes it
        /// passes, so neither grazing cuts nor slivers appear.
        /// </summary>
        private static void SplitRegion(float4 region, List<float4> boxes,
            List<List<float2>> rings, float2 origin, List<float4> regions, int depth)
        {
            var enclosed = new List<float4>();
            foreach (var box in boxes)
                if (box.x > region.x + Epsilon && box.y > region.y + Epsilon
                    && box.z < region.z - Epsilon && box.w < region.w - Epsilon) enclosed.Add(box);
            if (enclosed.Count == 0 || depth > 12)
            {
                regions.Add(region);
                return;
            }
            var bestLine = float.NaN;
            var bestVertical = true;
            var bestScore = float.MinValue;
            foreach (var hole in enclosed)
            foreach (var vertical in new[] { true, false })
            {
                var low = vertical ? hole.x : hole.y;
                var high = vertical ? hole.z : hole.w;
                var start = vertical ? origin.x : origin.y;
                var middle = (low + high) * 0.5f;
                // The grid line nearest the middle, the middle itself, and
                // half-metre steps across the hole's middle three fifths.
                var snapped = start + math.round((middle - start) / GridSize) * GridSize;
                var lines = new List<float> { snapped, middle };
                for (var t = low + (high - low) * 0.2f; t <= high - (high - low) * 0.2f; t += 0.5f)
                    lines.Add(t);
                foreach (var line in lines)
                {
                    // Either test may admit a line: the contour test where bounding
                    // boxes are too coarse, the box test where the plot edge is flat.
                    var score = math.max(LineScore(region, boxes, enclosed, rings, line, vertical),
                        BoxScore(region, boxes, enclosed, line, vertical));
                    if (score <= bestScore) continue;
                    bestScore = score;
                    bestLine = line;
                    bestVertical = vertical;
                }
            }
            if (float.IsNaN(bestLine))
            {
                regions.Add(region);
                return;
            }
            var first = region;
            var second = region;
            if (bestVertical)
            {
                first.z = bestLine;
                second.x = bestLine;
            }
            else
            {
                first.w = bestLine;
                second.y = bestLine;
            }
            SplitRegion(first, boxes, rings, origin, regions, depth + 1);
            SplitRegion(second, boxes, rings, origin, regions, depth + 1);
        }

        private static float BoxScore(float4 region, List<float4> boxes, List<float4> enclosed,
            float line, bool vertical)
        {
            var from = vertical ? region.x : region.y;
            var to = vertical ? region.z : region.w;
            if (line < from + GridSize || line > to - GridSize) return float.MinValue;
            var score = 0f;
            foreach (var box in boxes)
            {
                var low = vertical ? box.x : box.y;
                var high = vertical ? box.z : box.w;
                // Only holes the line meets inside this region matter.
                var across0 = vertical ? box.y : box.x;
                var across1 = vertical ? box.w : box.z;
                var regionLow = vertical ? region.y : region.x;
                var regionHigh = vertical ? region.w : region.z;
                if (across1 < regionLow || across0 > regionHigh) continue;
                var width = high - low;
                if (line > low && line < high)
                {
                    // Crossing near the edge of a hole cuts its contour flat.
                    var offset = math.abs(line - (low + high) * 0.5f) / math.max(Epsilon, width);
                    if (offset > 1f / 6f) return float.MinValue;
                    if (enclosed.Contains(box)) score += 10f;
                    score -= offset;
                }
                else if (math.min(math.abs(line - low), math.abs(line - high)) < 1f)
                    return float.MinValue;
            }
            // Balanced regions.
            return score - math.abs(line - (from + to) * 0.5f) / math.max(1f, to - from);
        }

        /// <summary>Least angle at which a region border may cut a hole's contour.</summary>
        private const float MinimumCrossing = 35f;
        /// <summary>Least angle at which a region border may meet the plot edge.</summary>
        private const float MinimumPlotCrossing = 25f;

        /// <summary>
        /// How good a grid line is as region border: it must cut every hole
        /// it meets inside the region steeply (at the real contour, not the
        /// bounding box), keep a metre from holes it passes, and meet the
        /// plot edge at a fair angle. Splitting enclosed holes scores most,
        /// then steep crossings and balanced halves.
        /// </summary>
        private static float LineScore(float4 region, List<float4> boxes, List<float4> enclosed,
            List<List<float2>> rings, float line, bool vertical)
        {
            var from = vertical ? region.x : region.y;
            var to = vertical ? region.z : region.w;
            if (line < from + GridSize || line > to - GridSize) return float.MinValue;
            var along0 = vertical ? region.y : region.x;
            var along1 = vertical ? region.w : region.z;
            var direction = vertical ? new float2(0f, 1f) : new float2(1f, 0f);
            var score = 0f;
            for (var r = 0; r < rings.Count; r++)
            {
                var ring = rings[r];
                var crossed = false;
                var flattest = 90f;
                var nearest = float.MaxValue;
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    var ca = vertical ? a.x : a.y;
                    var cb = vertical ? b.x : b.y;
                    var pa = vertical ? a.y : a.x;
                    if (pa >= along0 && pa <= along1) nearest = math.min(nearest, math.abs(ca - line));
                    if ((ca - line) * (cb - line) > 0f || ca == cb) continue;
                    var t = (line - ca) / (cb - ca);
                    var at = math.lerp(vertical ? a.y : a.x, vertical ? b.y : b.x, t);
                    if (at < along0 || at > along1) continue;
                    crossed = true;
                    var edge = math.normalizesafe(b - a);
                    var angle = math.degrees(math.acos(math.saturate(math.abs(math.dot(edge, direction)))));
                    flattest = math.min(flattest, angle);
                }
                if (r == 0)
                {
                    if (crossed && flattest < MinimumPlotCrossing) return float.MinValue;
                    continue;
                }
                if (crossed)
                {
                    if (flattest < MinimumCrossing) return float.MinValue;
                    if (enclosed.Contains(boxes[r - 1])) score += 10f;
                    score -= (90f - flattest) / 90f;
                }
                else if (nearest < 1f) return float.MinValue;
            }
            // Balanced regions.
            return score - math.abs(line - (from + to) * 0.5f) / math.max(1f, to - from);
        }

        /// <summary>True when the bounding boxes meet and a corner of one lies in the other.</summary>
        private static bool Overlaps(List<float2> a, List<float2> b)
        {
            PolygonMath.Bounds(a, out var minA, out var maxA);
            PolygonMath.Bounds(b, out var minB, out var maxB);
            if (math.any(maxA < minB) || math.any(maxB < minA)) return false;
            foreach (var point in b) if (PolygonMath.PointInside(point, a)) return true;
            foreach (var point in a) if (PolygonMath.PointInside(point, b)) return true;
            return false;
        }

        /// <summary>Total length of the cut edges (vertical in the cut frame) between pieces.</summary>
        private static float SeamLength(List<List<float2>> pieces)
        {
            // Every seam is shared by two pieces: half the edge length that
            // two pieces have in common.
            var total = 0f;
            for (var a = 0; a < pieces.Count; a++)
            for (var b = a + 1; b < pieces.Count; b++)
                total += Shared(pieces[a], pieces[b]);
            return total;
        }

        private static float Shared(List<float2> p, List<float2> q)
        {
            var length = 0f;
            for (var i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                var mid = (a + b) * 0.5f;
                for (var j = 0; j < q.Count; j++)
                    if (PolygonMath.DistanceToSegmentSquared(mid, q[j], q[(j + 1) % q.Count]) < 1e-4f)
                    {
                        length += math.distance(a, b);
                        break;
                    }
            }
            return length;
        }

        /// <summary>Rotates the ring to start at the edge pointing most along the axis.</summary>
        internal static List<float2> StartAlong(List<float2> ring, float2 axis)
        {
            var best = 0;
            var bestScore = float.MinValue;
            for (var i = 0; i < ring.Count; i++)
            {
                var edge = ring[(i + 1) % ring.Count] - ring[i];
                var length = math.length(edge);
                if (length < MinimumEdge) continue;
                // Pointing along, not just parallel: the opposite direction
                // would turn the pattern by 180 degrees.
                var score = math.dot(edge / length, axis);
                if (score <= bestScore) continue;
                bestScore = score;
                best = i;
            }
            var result = new List<float2>(ring.Count);
            for (var i = 0; i < ring.Count; i++) result.Add(ring[(best + i) % ring.Count]);
            return result;
        }

        /// <summary>Distance below which green reaches the plot edge instead of leaving a strip.</summary>
        private const float SnapDistance = 1.5f;
        /// <summary>Furthest a flat run of green is pulled to the plot edge.</summary>
        private const float LongSnap = 4f;
        /// <summary>Sharper corners are spikes of the traced garden outline.</summary>
        private const float MinimumCornerAngle = 25f;

        /// <summary>
        /// Green that comes within <see cref="SnapDistance"/> of the plot edge
        /// is laid flush against it, corners of the plot included, so no thin
        /// strip of paving (or bare ground) is left between them.
        /// </summary>
        internal static List<float2> SnapToPlot(List<float2> green, List<float2> plot)
        {
            if (green.Count < 3) return green;
            var onEdge = new int[green.Count];
            var position = new float[green.Count];
            var snapped = new List<float2>(green);
            var reach = new float[green.Count];
            for (var i = 0; i < green.Count; i++) reach[i] = SnapDistance;
            // Where the green leaves the edge at a flat angle, the paving
            // between them ends in a sliver point: let the snap reach further
            // there, until the green turns away steeply.
            for (var pass = 0; pass < 3; pass++)
            for (var i = 0; i < green.Count; i++)
            {
                var next = (i + 1) % green.Count;
                var previous = (i + green.Count - 1) % green.Count;
                foreach (var neighbour in new[] { next, previous })
                {
                    var here = math.sqrt(PolygonMath.DistanceToBoundarySquared(green[i], plot));
                    var there = math.sqrt(PolygonMath.DistanceToBoundarySquared(green[neighbour], plot));
                    if (here >= reach[i] || there < reach[neighbour] || there > LongSnap) continue;
                    var run = math.distance(green[i], green[neighbour]);
                    // Rising less than tan(25°) per metre along the edge is flat.
                    if (there - here < run * 0.47f) reach[neighbour] = LongSnap;
                }
            }
            for (var i = 0; i < green.Count; i++)
            {
                onEdge[i] = -1;
                var best = reach[i] * reach[i];
                for (var e = 0; e < plot.Count; e++)
                {
                    var a = plot[e];
                    var b = plot[(e + 1) % plot.Count];
                    var ab = b - a;
                    var t = math.saturate(math.dot(green[i] - a, ab) / math.max(1e-6f, math.lengthsq(ab)));
                    var on = a + ab * t;
                    var distance = math.distancesq(on, green[i]);
                    if (distance >= best) continue;
                    best = distance;
                    onEdge[i] = e;
                    position[i] = t;
                    snapped[i] = on;
                }
            }
            // Follow the plot's corners between two snapped points on different edges.
            var result = new List<float2>();
            var perimeter = 0f;
            for (var e = 0; e < plot.Count; e++) perimeter += math.distance(plot[e], plot[(e + 1) % plot.Count]);
            for (var i = 0; i < snapped.Count; i++)
            {
                result.Add(snapped[i]);
                var next = (i + 1) % snapped.Count;
                if (onEdge[i] < 0 || onEdge[next] < 0 || onEdge[i] == onEdge[next]
                    && position[next] >= position[i]) continue;
                var corners = new List<float2>();
                var walked = 0f;
                var edge = onEdge[i];
                var from = snapped[i];
                for (var guard = 0; guard < plot.Count && edge != onEdge[next]; guard++)
                {
                    var corner = plot[(edge + 1) % plot.Count];
                    walked += math.distance(from, corner);
                    corners.Add(corner);
                    from = corner;
                    edge = (edge + 1) % plot.Count;
                }
                walked += math.distance(from, snapped[next]);
                // Only a short way round; a long one means the green does not hug this stretch.
                if (edge == onEdge[next] && walked < perimeter * 0.25f
                    && walked < math.distance(snapped[i], snapped[next]) * 3f + 4f)
                    result.AddRange(corners);
            }
            result = DropShortEdges(Clean(result));
            return result.Count >= 3 && StrictlySimple(result)
                && PolygonMath.SignedArea(result) > 0.0 ? result : green;
        }

        /// <summary>Free radius kept around an entrance when green is laid to the edge.</summary>
        private const float EntranceKeep = 5f;

        /// <summary>
        /// A green area laid flush to the plot edge may not reach into a
        /// movement corridor nor come close to an entrance.
        /// </summary>
        private static bool KeepsAccess(List<float2> green, PlazaDesign design)
        {
            if (design == null) return true;
            foreach (var corridor in design.MovementCorridors)
            {
                if (corridor.Points.Count < 2) continue;
                foreach (var point in PlazaShape.Densify(green, 0.5f))
                    if (PlazaMovement.DistanceToPolyline(point, corridor.Points)
                        < corridor.Width * 0.5f) return false;
                foreach (var point in corridor.Points)
                    if (PolygonMath.PointInside(point, green)) return false;
            }
            if (design.Movement != null)
                foreach (var entrance in design.Movement.Entrances)
                    if (PolygonMath.PointInside(entrance, green)
                        || PlazaComposer.DistanceToOutline(entrance, green) < EntranceKeep)
                        return false;
            return true;
        }

        /// <summary>Laying green to the edge may not push it into a room it was not in.</summary>
        private static bool KeepsOutOfRooms(List<float2> flush, List<float2> green, PlazaDesign design)
        {
            if (design == null) return true;
            foreach (var room in design.Rooms)
            {
                if (room.Role == PlazaRoomRole.Passage) continue;
                var before = false;
                foreach (var point in green)
                    before |= room.Shape.Contains(point, room.Boundary);
                if (before) continue;
                foreach (var point in PlazaShape.Densify(flush, 0.5f))
                    if (room.Shape.Contains(point, room.Boundary)) return false;
                foreach (var point in room.Boundary)
                    if (PolygonMath.PointInside(point, flush)) return false;
            }
            return true;
        }

        /// <summary>
        /// Removes corners sharper than <see cref="MinimumCornerAngle"/>, and
        /// fills notches as narrow: the paving would run into them as a
        /// sliver point.
        /// </summary>
        internal static List<float2> DropSpikes(List<float2> ring)
        {
            var result = new List<float2>(ring);
            if (PolygonMath.SignedArea(result) < 0.0) result.Reverse();
            for (var guard = 0; guard < ring.Count * 2 && result.Count > 3; guard++)
            {
                var removed = false;
                for (var i = 0; i < result.Count && result.Count > 3; i++)
                {
                    var angle = CornerAngle(result, i);
                    if (angle >= MinimumCornerAngle && angle <= 360f - MinimumCornerAngle)
                        continue;
                    var candidate = new List<float2>(result);
                    candidate.RemoveAt(i);
                    if (!StrictlySimple(candidate)) continue;
                    result = candidate;
                    removed = true;
                    break;
                }
                if (!removed) break;
            }
            return result;
        }

        /// <summary>The plot minus the holes, as simple polygons without overlap.</summary>
        internal static List<List<float2>> Difference(IReadOnlyList<float2> outer,
            IReadOnlyList<List<float2>> holes, float2 axis)
        {
            // Work in the frame of the axis: cuts are vertical lines there.
            var normal = new float2(-axis.y, axis.x);
            float2 ToLocal(float2 p) => new float2(math.dot(p, axis), math.dot(p, normal));
            float2 ToWorld(float2 p) => axis * p.x + normal * p.y;
            var rings = new List<List<float2>> { Local(outer) };
            foreach (var hole in holes) rings.Add(Local(hole));
            List<float2> Local(IReadOnlyList<float2> ring)
            {
                var result = new List<float2>(ring.Count);
                foreach (var p in ring) result.Add(ToLocal(p));
                return result;
            }

            var pieces = Trapezoids(rings);
            var merged = MergeAlongCuts(pieces);
            var output = new List<List<float2>>();
            foreach (var piece in merged)
            {
                var world = new List<float2>(piece.Count);
                foreach (var p in piece) world.Add(ToWorld(p));
                world = DropShortEdges(Clean(world));
                // Slivers along a bed and crumbs in a garden's corner are not
                // paving anyone can use; the terrain shows there instead.
                if (world.Count < 3 || PlazaShape.Area(world) < MinimumPavingArea
                    || PlazaShape.MinimumWidth(world) < MinimumPavingWidth) continue;
                output.Add(world);
            }
            return output;
        }

        // ---------------------------------------------------- decomposition

        private struct Trapezoid
        {
            internal float Left, Right;
            internal float BottomLeft, BottomRight, TopLeft, TopRight;
        }

        /// <summary>
        /// Splits the region inside the first ring and outside all others at
        /// every vertex into trapezoids, then chains trapezoids whose sides
        /// match exactly into x-monotone pieces.
        /// </summary>
        /// <param name="clip">Index of a ring the region must lie inside
        /// (a clip box), or -1; all rings after the first are holes otherwise.</param>
        private static List<List<float2>> Trapezoids(List<List<float2>> rings, int clip = -1)
            => ChainSlabs(Slabs(rings, clip, Cuts(rings)));

        /// <summary>The region split at every vertex into trapezoids, slab by slab.</summary>
        private static List<List<Trapezoid>> Slabs(List<List<float2>> rings, int clip,
            List<float> cuts)
        {
            var slabs = new List<List<Trapezoid>>();
            var crossings = new List<(float middle, float left, float right)>();
            for (var k = 0; k + 1 < cuts.Count; k++)
            {
                var x0 = cuts[k];
                var x1 = cuts[k + 1];
                var xm = (x0 + x1) * 0.5f;
                crossings.Clear();
                foreach (var ring in rings)
                    for (var i = 0; i < ring.Count; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % ring.Count];
                        if (math.min(a.x, b.x) > xm || math.max(a.x, b.x) < xm) continue;
                        if (math.abs(b.x - a.x) < Epsilon) continue;
                        // Exactly at a corner, the corner itself: neighbouring
                        // slabs then agree bit for bit on the shared side.
                        float At(float x) => x == a.x ? a.y : x == b.x ? b.y
                            : a.y + (x - a.x) * (b.y - a.y) / (b.x - a.x);
                        crossings.Add((At(xm), At(x0), At(x1)));
                    }
                crossings.Sort((p, q) => p.middle.CompareTo(q.middle));
                var slab = new List<Trapezoid>();
                for (var i = 0; i + 1 < crossings.Count; i++)
                {
                    var probe = new float2(xm, (crossings[i].middle + crossings[i + 1].middle) * 0.5f);
                    if (!Inside(rings, probe, clip)) continue;
                    slab.Add(new Trapezoid
                    {
                        Left = x0, Right = x1,
                        BottomLeft = crossings[i].left, BottomRight = crossings[i].right,
                        TopLeft = crossings[i + 1].left, TopRight = crossings[i + 1].right,
                    });
                }
                slabs.Add(slab);
            }
            return slabs;
        }

        private static List<float> Cuts(List<List<float2>> rings)
        {
            var xs = new List<float>();
            foreach (var ring in rings)
                foreach (var p in ring) xs.Add(p.x);
            xs.Sort();
            var cuts = new List<float>();
            foreach (var x in xs)
                if (cuts.Count == 0 || x - cuts[cuts.Count - 1] > Epsilon) cuts.Add(x);
            return cuts;
        }

        /// <summary>
        /// The outline of a region that encloses no hole: all its trapezoids
        /// united by striking the vertical sides they share (each inner side
        /// appears once in each direction), then chaining what is left. No
        /// helper cut survives, so no corner of one appears.
        /// </summary>
        private static List<List<float2>> Outline(List<List<float2>> rings, int clip)
        {
            var slabs = Slabs(rings, clip, Cuts(rings));
            var edges = new List<(float2 from, float2 to)>();
            // Vertical sides per cut line: +1 up (right side), -1 down (left side).
            var sides = new Dictionary<float, List<(float low, float high, int sign)>>();
            void Side(float x, float low, float high, int sign)
            {
                if (high - low <= 0f) return;
                if (!sides.TryGetValue(x, out var list)) sides[x] = list = new List<(float, float, int)>();
                list.Add((low, high, sign));
            }
            foreach (var slab in slabs)
            foreach (var t in slab)
            {
                var bl = new float2(t.Left, t.BottomLeft);
                var br = new float2(t.Right, t.BottomRight);
                var tr = new float2(t.Right, t.TopRight);
                var tl = new float2(t.Left, t.TopLeft);
                if (math.distancesq(bl, br) > 0f) edges.Add((bl, br));
                if (math.distancesq(tr, tl) > 0f) edges.Add((tr, tl));
                Side(t.Right, t.BottomRight, t.TopRight, 1);
                Side(t.Left, t.BottomLeft, t.TopLeft, -1);
            }
            foreach (var pair in sides)
            {
                var ys = new List<float>();
                foreach (var (low, high, _) in pair.Value)
                {
                    ys.Add(low);
                    ys.Add(high);
                }
                ys.Sort();
                for (var i = 0; i + 1 < ys.Count; i++)
                {
                    var low = ys[i];
                    var high = ys[i + 1];
                    if (high - low <= 0f) continue;
                    var middle = (low + high) * 0.5f;
                    var net = 0;
                    foreach (var (from, to, sign) in pair.Value)
                        if (middle > from && middle < to) net += sign;
                    if (net > 0) edges.Add((new float2(pair.Key, low), new float2(pair.Key, high)));
                    else if (net < 0) edges.Add((new float2(pair.Key, high), new float2(pair.Key, low)));
                }
            }
            // Chain the boundary edges into rings.
            var next = new Dictionary<float2, List<float2>>();
            foreach (var (from, to) in edges)
            {
                if (!next.TryGetValue(from, out var list)) next[from] = list = new List<float2>();
                list.Add(to);
            }
            var rings2 = new List<List<float2>>();
            foreach (var start in new List<float2>(next.Keys))
                while (next.TryGetValue(start, out var outgoing) && outgoing.Count > 0)
                {
                    var ring = new List<float2> { start };
                    var current = start;
                    for (var guard = 0; guard < edges.Count + 1; guard++)
                    {
                        if (!next.TryGetValue(current, out var options) || options.Count == 0) break;
                        var to = options[options.Count - 1];
                        options.RemoveAt(options.Count - 1);
                        if (to.Equals(start)) break;
                        ring.Add(to);
                        current = to;
                    }
                    var cleaned = Clean(ring);
                    if (cleaned.Count >= 3 && PolygonMath.SignedArea(cleaned) > 0.0) rings2.Add(cleaned);
                }
            return rings2;
        }

        private static List<List<float2>> ChainSlabs(List<List<Trapezoid>> slabs)
        {
            // Chain trapezoids through sides that match end to end.
            var pieces = new List<List<float2>>();
            var used = new List<bool[]>();
            foreach (var slab in slabs) used.Add(new bool[slab.Count]);
            for (var k = 0; k < slabs.Count; k++)
            for (var t = 0; t < slabs[k].Count; t++)
            {
                if (used[k][t]) continue;
                var chain = new List<Trapezoid>();
                var slab = k;
                var index = t;
                while (true)
                {
                    used[slab][index] = true;
                    var trapezoid = slabs[slab][index];
                    chain.Add(trapezoid);
                    if (slab + 1 >= slabs.Count
                        || trapezoid.TopRight - trapezoid.BottomRight < Epsilon) break;
                    var next = -1;
                    for (var n = 0; n < slabs[slab + 1].Count; n++)
                    {
                        var candidate = slabs[slab + 1][n];
                        if (used[slab + 1][n]
                            || math.abs(candidate.BottomLeft - trapezoid.BottomRight) > Epsilon
                            || math.abs(candidate.TopLeft - trapezoid.TopRight) > Epsilon) continue;
                        next = n;
                        break;
                    }
                    if (next < 0) break;
                    slab++;
                    index = next;
                }
                var bottom = new List<float2>();
                var top = new List<float2>();
                foreach (var trapezoid in chain)
                {
                    bottom.Add(new float2(trapezoid.Left, trapezoid.BottomLeft));
                    top.Add(new float2(trapezoid.Left, trapezoid.TopLeft));
                }
                var last = chain[chain.Count - 1];
                bottom.Add(new float2(last.Right, last.BottomRight));
                top.Add(new float2(last.Right, last.TopRight));
                top.Reverse();
                bottom.AddRange(top);
                var piece = Clean(bottom);
                if (piece.Count >= 3) pieces.Add(piece);
            }
            return pieces;
        }

        private static bool Inside(List<List<float2>> rings, float2 point, int clip = -1)
        {
            if (!PolygonMath.PointInside(point, rings[0])) return false;
            for (var i = 1; i < rings.Count; i++)
                if (PolygonMath.PointInside(point, rings[i]) != (i == clip)) return false;
            return true;
        }

        // ------------------------------------------------------------ merge

        /// <summary>
        /// Greedily joins pieces that share a stretch of a cut line, as long
        /// as the union stays a simple polygon: a union that would wrap
        /// around a hole touches itself and is rejected, so no slits appear.
        /// </summary>
        private static List<List<float2>> MergeAlongCuts(List<List<float2>> pieces)
        {
            // A pair that cannot be joined stays so until one of them changes.
            var failed = new HashSet<(List<float2>, List<float2>)>();
            var merged = true;
            while (merged)
            {
                merged = false;
                // Largest first: big calm pieces absorb the small ones.
                pieces.Sort((a, b) => PlazaShape.Area(b).CompareTo(PlazaShape.Area(a)));
                for (var a = 0; a < pieces.Count && !merged; a++)
                for (var b = 0; b < pieces.Count && !merged; b++)
                {
                    if (a == b || failed.Contains((pieces[a], pieces[b]))) continue;
                    var union = Join(pieces[a], pieces[b]);
                    if (union == null)
                    {
                        failed.Add((pieces[a], pieces[b]));
                        continue;
                    }
                    pieces[a] = union;
                    pieces.RemoveAt(b);
                    merged = true;
                }
            }
            return pieces;
        }

        /// <summary>
        /// Union of two counter-clockwise pieces that touch along a vertical
        /// cut: p's edge runs up the line, q's edge runs down it. Returns
        /// null when they share no such stretch or the union is not simple.
        /// </summary>
        private static List<float2> Join(List<float2> p, List<float2> q)
        {
            for (var i = 0; i < p.Count; i++)
            {
                var p0 = p[i];
                var p1 = p[(i + 1) % p.Count];
                if (math.abs(p0.x - p1.x) > Epsilon || p1.y <= p0.y) continue;
                for (var j = 0; j < q.Count; j++)
                {
                    var q0 = q[j];
                    var q1 = q[(j + 1) % q.Count];
                    if (math.abs(q0.x - q1.x) > Epsilon || math.abs(q0.x - p0.x) > Epsilon
                        || q1.y >= q0.y) continue;
                    var low = math.max(p0.y, q1.y);
                    var high = math.min(p1.y, q0.y);
                    if (high - low < MinimumSeam) continue;
                    var x = p0.x;
                    var union = new List<float2>();
                    for (var k = 0; k <= i; k++) union.Add(p[k]);
                    union.Add(new float2(x, low));
                    // Around q from the lower end of its edge back to its upper end.
                    for (var k = 1; k <= q.Count; k++) union.Add(q[(j + k) % q.Count]);
                    union.Add(new float2(x, high));
                    for (var k = i + 1; k < p.Count; k++) union.Add(p[k]);
                    union = Clean(union);
                    if (union.Count >= 3 && OnlySeamContact(p, q, x, low, high)
                        && math.abs(PlazaShape.Area(union) - PlazaShape.Area(p)
                            - PlazaShape.Area(q)) < 0.01f * (PlazaShape.Area(union) + 1f))
                        return union;
                }
            }
            return null;
        }

        // ---------------------------------------------------------- helpers

        /// <summary>
        /// Two simple pieces with disjoint interiors give a simple union when
        /// the seam is their only contact. Any other touching corner or
        /// shared stretch would turn into a pinch or a slit.
        /// </summary>
        private static bool OnlySeamContact(List<float2> p, List<float2> q, float x,
            float low, float high)
        {
            bool OnSeam(float2 point) => math.abs(point.x - x) < Epsilon
                && point.y > low - Epsilon && point.y < high + Epsilon;
            bool Touches(List<float2> points, List<float2> ring)
            {
                foreach (var point in points)
                {
                    if (OnSeam(point)) continue;
                    for (var i = 0; i < ring.Count; i++)
                        if (PolygonMath.DistanceToSegmentSquared(point, ring[i],
                                ring[(i + 1) % ring.Count]) < Epsilon * Epsilon) return true;
                }
                return false;
            }
            return !Touches(q, p) && !Touches(p, q);
        }

        /// <summary>
        /// Simple in the strict sense the game needs: no two edges cross,
        /// touch or run along each other, except neighbours at their shared
        /// corner.
        /// </summary>
        internal static bool StrictlySimple(IReadOnlyList<float2> ring)
        {
            var count = ring.Count;
            if (count < 3) return false;
            for (var i = 0; i < count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % count];
                for (var j = i + 1; j < count; j++)
                {
                    var c = ring[j];
                    var d = ring[(j + 1) % count];
                    var adjacent = j == i + 1 || i == 0 && j == count - 1;
                    if (adjacent)
                    {
                        // Neighbours may only share their corner: no fold-back.
                        var shared = j == i + 1 ? b : a;
                        var other = j == i + 1 ? d : c;
                        var own = j == i + 1 ? a : b;
                        if (math.abs(PlazaGeometry.Cross(own - shared, other - shared)) < 1e-6f
                            && math.dot(own - shared, other - shared) > 0f) return false;
                        continue;
                    }
                    if (SegmentDistance(a, b, c, d) < Epsilon) return false;
                }
            }
            return true;
        }

        private static float SegmentDistance(float2 a, float2 b, float2 c, float2 d)
        {
            if (Crosses(a, b, c, d)) return 0f;
            return math.sqrt(math.min(math.min(
                    PolygonMath.DistanceToSegmentSquared(a, c, d),
                    PolygonMath.DistanceToSegmentSquared(b, c, d)),
                math.min(PolygonMath.DistanceToSegmentSquared(c, a, b),
                    PolygonMath.DistanceToSegmentSquared(d, a, b))));
        }

        private static bool Crosses(float2 a, float2 b, float2 c, float2 d)
        {
            var d1 = PlazaGeometry.Cross(d - c, a - c);
            var d2 = PlazaGeometry.Cross(d - c, b - c);
            var d3 = PlazaGeometry.Cross(b - a, c - a);
            var d4 = PlazaGeometry.Cross(b - a, d - a);
            return (d1 > 0f && d2 < 0f || d1 < 0f && d2 > 0f)
                && (d3 > 0f && d4 < 0f || d3 < 0f && d4 > 0f);
        }

        /// <summary>
        /// Removes corners at edges shorter than <see cref="MinimumEdge"/>,
        /// the one whose removal changes the area least, while the ring
        /// stays simple.
        /// </summary>
        internal static List<float2> DropShortEdges(List<float2> ring)
        {
            var result = new List<float2>(ring);
            for (var guard = 0; guard < ring.Count && result.Count > 3; guard++)
            {
                var removed = false;
                for (var i = 0; i < result.Count && result.Count > 3; i++)
                {
                    var next = (i + 1) % result.Count;
                    if (math.distance(result[i], result[next]) >= MinimumEdge) continue;
                    foreach (var drop in new[] { i, next })
                    {
                        var candidate = new List<float2>(result);
                        candidate.RemoveAt(drop);
                        if (!StrictlySimple(candidate)) continue;
                        result = candidate;
                        removed = true;
                        break;
                    }
                    if (removed) break;
                }
                if (!removed) break;
            }
            return result;
        }

        private static List<float2> Counterclockwise(IReadOnlyList<float2> ring)
        {
            var result = new List<float2>(ring);
            if (PolygonMath.SignedArea(result) < 0.0) result.Reverse();
            return result;
        }

        /// <summary>Drops repeated and collinear points (a cleaned ring may be empty).</summary>
        internal static List<float2> Clean(List<float2> ring)
        {
            var result = new List<float2>(ring);
            var changed = true;
            while (changed && result.Count >= 3)
            {
                changed = false;
                for (var i = 0; i < result.Count && result.Count >= 3; i++)
                {
                    var previous = result[(i + result.Count - 1) % result.Count];
                    var current = result[i];
                    var next = result[(i + 1) % result.Count];
                    var duplicate = math.distancesq(previous, current) < Epsilon * Epsilon;
                    var cross = PlazaGeometry.Cross(current - previous, next - current);
                    var span = math.distance(previous, next);
                    // Collinear: no turn, including a spike that doubles back.
                    var straight = math.abs(cross) <= Epsilon * math.max(1f, span);
                    if (!duplicate && !straight) continue;
                    result.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
            return result.Count >= 3 ? result : new List<float2>();
        }
    }
}
