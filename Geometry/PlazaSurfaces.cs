using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Ground surfaces of a composed plaza, ready to build: green areas as
    /// their own surfaces and the paving as the rest of the plot. The two
    /// never overlap (overlapping surfaces render unpredictably in the
    /// game), and since surfaces have no holes, the paving is split into as
    /// few simple pieces as possible. Rooms are not separate surfaces: the
    /// main square, side rooms and passages share one paving; their limits
    /// stay in the <see cref="PlazaDesign"/>.
    /// </summary>
    internal sealed class PlazaSurfacePlan
    {
        internal readonly List<List<float2>> Paving = new List<List<float2>>();
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

        internal static PlazaSurfacePlan Compose(IReadOnlyList<float2> plot, PlazaDesign design)
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
                    if (KeepsAccess(flush, design)) green = flush;
                    if (green.Count < 3 || PlazaShape.Area(green) < MinimumGreenArea
                        || !StrictlySimple(green)) continue;
                    plan.Green.Add(green);
                }
            var axis = math.normalizesafe(design != null && design.Rooms.Count > 0
                ? design.Rooms[0].Axis : new float2(1f, 0f));
            if (plan.Green.Count == 0) plan.Paving.Add(outline);
            else
            {
                // Same-material pieces show a seam in the game: cut along or
                // across the square's axis, whichever leaves less seam.
                var along = Difference(outline, plan.Green, axis);
                var across = Difference(outline, plan.Green, new float2(-axis.y, axis.x));
                plan.Paving.AddRange(SeamLength(across) < SeamLength(along) - 0.5f ? across : along);
            }
            // The game lays a surface's pattern along its first edge: every
            // surface starts with the edge running most along the axis.
            for (var i = 0; i < plan.Paving.Count; i++) plan.Paving[i] = StartAlong(plan.Paving[i], axis);
            for (var i = 0; i < plan.Green.Count; i++) plan.Green[i] = StartAlong(plan.Green[i], axis);
            return plan;
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
            for (var i = 0; i < green.Count; i++)
            {
                onEdge[i] = -1;
                var best = SnapDistance * SnapDistance;
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

        /// <summary>Removes corners sharper than <see cref="MinimumCornerAngle"/>.</summary>
        internal static List<float2> DropSpikes(List<float2> ring)
        {
            var result = new List<float2>(ring);
            var limit = math.cos(math.radians(MinimumCornerAngle));
            for (var guard = 0; guard < ring.Count && result.Count > 3; guard++)
            {
                var removed = false;
                for (var i = 0; i < result.Count && result.Count > 3; i++)
                {
                    var previous = result[(i + result.Count - 1) % result.Count] - result[i];
                    var next = result[(i + 1) % result.Count] - result[i];
                    if (math.dot(math.normalizesafe(previous), math.normalizesafe(next)) < limit)
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
        private static List<List<float2>> Trapezoids(List<List<float2>> rings)
        {
            var xs = new List<float>();
            foreach (var ring in rings)
                foreach (var p in ring) xs.Add(p.x);
            xs.Sort();
            var cuts = new List<float>();
            foreach (var x in xs)
                if (cuts.Count == 0 || x - cuts[cuts.Count - 1] > Epsilon) cuts.Add(x);

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
                        float At(float x) => a.y + (x - a.x) * (b.y - a.y) / (b.x - a.x);
                        crossings.Add((At(xm), At(x0), At(x1)));
                    }
                crossings.Sort((p, q) => p.middle.CompareTo(q.middle));
                var slab = new List<Trapezoid>();
                for (var i = 0; i + 1 < crossings.Count; i++)
                {
                    var probe = new float2(xm, (crossings[i].middle + crossings[i + 1].middle) * 0.5f);
                    if (!Inside(rings, probe)) continue;
                    slab.Add(new Trapezoid
                    {
                        Left = x0, Right = x1,
                        BottomLeft = crossings[i].left, BottomRight = crossings[i].right,
                        TopLeft = crossings[i + 1].left, TopRight = crossings[i + 1].right,
                    });
                }
                slabs.Add(slab);
            }

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

        private static bool Inside(List<List<float2>> rings, float2 point)
        {
            if (!PolygonMath.PointInside(point, rings[0])) return false;
            for (var i = 1; i < rings.Count; i++)
                if (PolygonMath.PointInside(point, rings[i])) return false;
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
