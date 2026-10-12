using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Spatial fingerprint of a plaza polygon: shape measures, the oriented
    /// main axes and a distance field on a grid whose cell size follows the
    /// plot size. The grid is for analysis only; designed shapes stay exact
    /// geometry. Free of CS2 types so the batch harness can test it.
    /// </summary>
    internal sealed class PlazaGeometry
    {
        private const float MinimumCellSize = 0.5f;
        private const float MaximumCellSize = 3f;
        /// <summary>Target number of cells across the square root of the area.</summary>
        private const float CellsAcrossRootArea = 64f;

        internal readonly List<float2> Polygon;
        internal readonly List<float2> Hull;
        internal readonly float Area;
        internal readonly float Perimeter;
        /// <summary>4πA/U²: 1 for a circle, small for ragged outlines.</summary>
        internal readonly float Compactness;
        /// <summary>Area / convex hull area.</summary>
        internal readonly float Convexity;
        /// <summary>Long / short side of the minimum-area bounding box.</summary>
        internal readonly float Elongation;
        internal readonly float2 Centroid;
        internal readonly float2 BoxCenter;
        internal readonly float2 MajorAxis;
        internal readonly float2 MinorAxis;
        internal readonly float MajorSpan;
        internal readonly float MinorSpan;

        internal readonly float CellSize;
        internal readonly float2 Origin;
        internal readonly int Columns;
        internal readonly int Rows;
        internal readonly bool[] Inside;
        /// <summary>Distance of each inside cell centre to the boundary.</summary>
        internal readonly float[] Distance;
        internal readonly float MaximumDistance;
        /// <summary>Inside point farthest from the boundary (pole of inaccessibility).</summary>
        internal readonly float2 Pole;

        private PlazaGeometry(List<float2> polygon)
        {
            Polygon = polygon;
            Area = (float)Math.Abs(PolygonMath.SignedArea(polygon));
            for (var i = 0; i < polygon.Count; i++)
                Perimeter += math.distance(polygon[i], polygon[(i + 1) % polygon.Count]);
            Compactness = Perimeter > 0f ? 4f * math.PI * Area / (Perimeter * Perimeter) : 0f;
            Hull = ConvexHull(polygon);
            var hullArea = (float)Math.Abs(PolygonMath.SignedArea(Hull));
            Convexity = hullArea > 0f ? math.saturate(Area / hullArea) : 0f;
            Centroid = AreaCentroid(polygon);
            MinimumBox(Hull, out MajorAxis, out MajorSpan, out MinorSpan, out BoxCenter);
            MinorAxis = new float2(-MajorAxis.y, MajorAxis.x);
            Elongation = MinorSpan > 0.01f ? MajorSpan / MinorSpan : 1f;

            PolygonMath.Bounds(polygon, out var min, out var max);
            CellSize = math.clamp(math.sqrt(math.max(1f, Area)) / CellsAcrossRootArea,
                MinimumCellSize, MaximumCellSize);
            Origin = min;
            Columns = math.max(1, (int)math.ceil((max.x - min.x) / CellSize));
            Rows = math.max(1, (int)math.ceil((max.y - min.y) / CellSize));
            Inside = new bool[Columns * Rows];
            Distance = new float[Columns * Rows];
            var best = -1f;
            Pole = Centroid;
            for (var index = 0; index < Inside.Length; index++)
            {
                var point = CellCenter(index);
                if (!PolygonMath.PointInside(point, polygon)) continue;
                Inside[index] = true;
                var distance = math.sqrt(PolygonMath.DistanceToBoundarySquared(point, polygon));
                Distance[index] = distance;
                if (distance <= best) continue;
                best = distance;
                Pole = point;
            }
            MaximumDistance = math.max(0f, best);
        }

        /// <summary>Analyses polygon; returns null for degenerate input.</summary>
        internal static PlazaGeometry Analyze(IReadOnlyList<float2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return null;
            var points = new List<float2>(polygon.Count);
            for (var i = 0; i < polygon.Count; i++)
            {
                if (!math.all(math.isfinite(polygon[i]))) return null;
                if (points.Count > 0 && math.distancesq(points[points.Count - 1],
                        polygon[i]) < 0.0001f) continue;
                points.Add(polygon[i]);
            }
            if (points.Count > 3 && math.distancesq(points[0],
                    points[points.Count - 1]) < 0.0001f)
                points.RemoveAt(points.Count - 1);
            if (points.Count < 3 || Math.Abs(PolygonMath.SignedArea(points)) < 1.0)
                return null;
            // One orientation for every inward/outward computation.
            if (PolygonMath.SignedArea(points) < 0.0) points.Reverse();
            return new PlazaGeometry(points);
        }

        internal float2 CellCenter(int index)
            => Origin + new float2(index % Columns + 0.5f, index / Columns + 0.5f) * CellSize;

        internal bool TryCell(float2 point, out int index)
        {
            var cell = (int2)math.floor((point - Origin) / CellSize);
            index = cell.y * Columns + cell.x;
            return cell.x >= 0 && cell.y >= 0 && cell.x < Columns && cell.y < Rows;
        }

        internal bool Contains(float2 point)
            => PolygonMath.PointInside(point, Polygon);

        internal float DistanceToBoundary(float2 point)
            => math.sqrt(PolygonMath.DistanceToBoundarySquared(point, Polygon));

        /// <summary>
        /// True when the straight segment stays inside the polygon (touching
        /// the boundary counts as inside). Used for visibility and paths.
        /// </summary>
        internal bool SegmentInside(float2 a, float2 b)
        {
            if (!PolygonMath.PointInsideOrBoundary(a, Polygon, 0.05f)
                || !PolygonMath.PointInsideOrBoundary(b, Polygon, 0.05f)) return false;
            for (var i = 0; i < Polygon.Count; i++)
                if (ProperlyIntersect(a, b, Polygon[i], Polygon[(i + 1) % Polygon.Count]))
                    return false;
            // A chord through a reflex vertex touches but never crosses an edge.
            for (var step = 1; step < 4; step++)
                if (!PolygonMath.PointInsideOrBoundary(math.lerp(a, b, step / 4f),
                        Polygon, 0.05f)) return false;
            return true;
        }

        /// <summary>Inward unit normal of edge i (polygon is counter-clockwise).</summary>
        internal float2 InwardNormal(int edge)
        {
            var direction = math.normalizesafe(Polygon[(edge + 1) % Polygon.Count]
                - Polygon[edge]);
            return new float2(-direction.y, direction.x);
        }

        /// <summary>Closest boundary edge to a point.</summary>
        internal int NearestEdge(float2 point)
        {
            var best = float.MaxValue;
            var edge = 0;
            for (var i = 0; i < Polygon.Count; i++)
            {
                var distance = PolygonMath.DistanceToSegmentSquared(point, Polygon[i],
                    Polygon[(i + 1) % Polygon.Count]);
                if (distance >= best) continue;
                best = distance;
                edge = i;
            }
            return edge;
        }

        /// <summary>
        /// A point just inside the polygon behind an entrance on the outline,
        /// so paths and sight lines start in the walkable area.
        /// </summary>
        internal float2 InsideEntrance(float2 entrance, float depth = 0.75f)
        {
            var edge = NearestEdge(entrance);
            var on = PolygonMath.ClosestPointOnSegment(entrance, Polygon[edge],
                Polygon[(edge + 1) % Polygon.Count]);
            var normal = InwardNormal(edge);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var candidate = on + normal * depth / (1 << attempt);
                if (Contains(candidate)) return candidate;
            }
            return on;
        }

        /// <summary>
        /// Distance-field maxima at least minimumDistance from the boundary
        /// and separation apart, strongest first: candidate room centres.
        /// </summary>
        internal List<float2> RoomCentres(float minimumDistance, float separation)
        {
            var candidates = new List<int>();
            for (var index = 0; index < Inside.Length; index++)
                if (Inside[index] && Distance[index] >= minimumDistance
                    && IsLocalMaximum(index)) candidates.Add(index);
            candidates.Sort((a, b) => Distance[b].CompareTo(Distance[a]));
            var result = new List<float2>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var point = CellCenter(candidates[i]);
                var separated = true;
                for (var j = 0; j < result.Count && separated; j++)
                    separated = math.distance(point, result[j]) >= separation;
                if (separated) result.Add(point);
            }
            return result;
        }

        private bool IsLocalMaximum(int index)
        {
            var x = index % Columns;
            var y = index / Columns;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Columns || ny >= Rows) continue;
                var neighbour = ny * Columns + nx;
                if (Inside[neighbour] && Distance[neighbour] > Distance[index] + 0.0001f)
                    return false;
            }
            return true;
        }

        private static bool ProperlyIntersect(float2 a, float2 b, float2 c, float2 d)
        {
            var d1 = Cross(d - c, a - c);
            var d2 = Cross(d - c, b - c);
            var d3 = Cross(b - a, c - a);
            var d4 = Cross(b - a, d - a);
            const float epsilon = 0.0001f;
            return (d1 > epsilon && d2 < -epsilon || d1 < -epsilon && d2 > epsilon)
                && (d3 > epsilon && d4 < -epsilon || d3 < -epsilon && d4 > epsilon);
        }

        internal static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        internal static List<float2> ConvexHull(IReadOnlyList<float2> points)
        {
            var sorted = new List<float2>(points);
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var hull = new List<float2>(sorted.Count * 2);
            for (var pass = 0; pass < 2; pass++)
            {
                var start = hull.Count;
                for (var i = 0; i < sorted.Count; i++)
                {
                    var point = sorted[pass == 0 ? i : sorted.Count - 1 - i];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 1]
                        - hull[hull.Count - 2], point - hull[hull.Count - 2]) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(point);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        internal static float2 AreaCentroid(IReadOnlyList<float2> polygon)
        {
            double twiceArea = 0.0, x = 0.0, y = 0.0;
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
                var sum = float2.zero;
                for (var i = 0; i < polygon.Count; i++) sum += polygon[i];
                return sum / polygon.Count;
            }
            return new float2((float)(x / (3.0 * twiceArea)), (float)(y / (3.0 * twiceArea)));
        }

        /// <summary>Minimum-area bounding box over the hull edge directions.</summary>
        private static void MinimumBox(IReadOnlyList<float2> hull, out float2 major,
            out float majorSpan, out float minorSpan, out float2 center)
        {
            major = new float2(1f, 0f);
            majorSpan = minorSpan = 0f;
            center = float2.zero;
            var bestArea = float.MaxValue;
            for (var i = 0; i < hull.Count; i++)
            {
                var axis = math.normalizesafe(hull[(i + 1) % hull.Count] - hull[i]);
                if (math.lengthsq(axis) < 0.5f) continue;
                var normal = new float2(-axis.y, axis.x);
                float minA = float.MaxValue, maxA = float.MinValue;
                float minB = float.MaxValue, maxB = float.MinValue;
                for (var j = 0; j < hull.Count; j++)
                {
                    var a = math.dot(hull[j], axis);
                    var b = math.dot(hull[j], normal);
                    minA = math.min(minA, a); maxA = math.max(maxA, a);
                    minB = math.min(minB, b); maxB = math.max(maxB, b);
                }
                var area = (maxA - minA) * (maxB - minB);
                if (area >= bestArea - 0.0001f) continue;
                bestArea = area;
                var spanA = maxA - minA;
                var spanB = maxB - minB;
                center = axis * ((minA + maxA) * 0.5f) + normal * ((minB + maxB) * 0.5f);
                if (spanA >= spanB)
                {
                    major = axis; majorSpan = spanA; minorSpan = spanB;
                }
                else
                {
                    major = normal; majorSpan = spanB; minorSpan = spanA;
                }
            }
            // A stable sign keeps designs deterministic for mirrored inputs.
            if (major.x < -0.0001f || math.abs(major.x) <= 0.0001f && major.y < 0f)
                major = -major;
        }
    }
}
