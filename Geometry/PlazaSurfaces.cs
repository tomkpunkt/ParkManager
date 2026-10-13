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
        private const float MinimumPavingArea = 6f;
        private const float MinimumPavingWidth = 0.5f;
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
                    if (green.Count < 3 || PlazaShape.Area(green) < MinimumGreenArea
                        || !StrictlySimple(green)) continue;
                    plan.Green.Add(green);
                }
            if (plan.Green.Count == 0)
            {
                plan.Paving.Add(outline);
                return plan;
            }
            // Cuts run across the main square's axis, so seams follow its frame.
            var axis = design.Rooms.Count > 0 ? design.Rooms[0].Axis : new float2(1f, 0f);
            plan.Paving.AddRange(Difference(outline, plan.Green, math.normalizesafe(axis)));
            return plan;
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
