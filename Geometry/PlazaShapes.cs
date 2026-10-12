using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Exact geometric primitives for plaza spaces. A shape is described by
    /// its centre, orientation and half extents and turned into a closed,
    /// counter-clockwise outline only when needed.
    /// </summary>
    internal struct PlazaShape
    {
        internal PlazaCoreShape Kind;
        internal float2 Center;
        /// <summary>Unit direction of the shape's length.</summary>
        internal float2 Axis;
        internal float HalfLength;
        internal float HalfWidth;
        /// <summary>
        /// Uniform scale applied to <see cref="Template"/> for the
        /// polygon-based kinds (convex hull and boundary-aligned).
        /// </summary>
        internal float Scale;
        internal IReadOnlyList<float2> Template;

        private const int EllipseSegments = 48;
        private const float ChamferFraction = 0.22f;

        internal float2 Normal => new float2(-Axis.y, Axis.x);

        internal List<float2> Outline()
        {
            var outline = new List<float2>();
            switch (Kind)
            {
                case PlazaCoreShape.Rectangle:
                    AddCorner(outline, -1f, -1f);
                    AddCorner(outline, 1f, -1f);
                    AddCorner(outline, 1f, 1f);
                    AddCorner(outline, -1f, 1f);
                    break;
                case PlazaCoreShape.ChamferedRectangle:
                    var cut = math.min(HalfLength, HalfWidth) * ChamferFraction * 2f;
                    var u = Axis;
                    var v = Normal;
                    var a = HalfLength;
                    var b = HalfWidth;
                    outline.Add(Center + u * (-a + cut) - v * b);
                    outline.Add(Center + u * (a - cut) - v * b);
                    outline.Add(Center + u * a - v * (b - cut));
                    outline.Add(Center + u * a + v * (b - cut));
                    outline.Add(Center + u * (a - cut) + v * b);
                    outline.Add(Center + u * (-a + cut) + v * b);
                    outline.Add(Center - u * a + v * (b - cut));
                    outline.Add(Center - u * a - v * (b - cut));
                    break;
                case PlazaCoreShape.Ellipse:
                    for (var i = 0; i < EllipseSegments; i++)
                    {
                        var angle = 2f * math.PI * i / EllipseSegments;
                        outline.Add(Center + Axis * (math.cos(angle) * HalfLength)
                            + Normal * (math.sin(angle) * HalfWidth));
                    }
                    break;
                default:
                    if (Template == null) break;
                    for (var i = 0; i < Template.Count; i++)
                        outline.Add(Center + (Template[i] - TemplateAnchor) * Scale);
                    break;
            }
            return outline;
        }

        /// <summary>Point in the template that maps onto <see cref="Center"/>.</summary>
        internal float2 TemplateAnchor;

        private void AddCorner(List<float2> outline, float su, float sv)
            => outline.Add(Center + Axis * (su * HalfLength) + Normal * (sv * HalfWidth));

        /// <summary>Exact containment test for this shape.</summary>
        internal bool Contains(float2 point, IReadOnlyList<float2> outline)
        {
            if (Kind == PlazaCoreShape.Ellipse)
            {
                var d = point - Center;
                var x = math.dot(d, Axis) / math.max(0.001f, HalfLength);
                var y = math.dot(d, Normal) / math.max(0.001f, HalfWidth);
                return x * x + y * y <= 1f;
            }
            return PolygonMath.PointInside(point, outline);
        }

        /// <summary>Outline with extra points on long edges, for fit tests.</summary>
        internal static List<float2> Densify(IReadOnlyList<float2> outline, float step)
        {
            var result = new List<float2>(outline.Count * 2);
            for (var i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                var pieces = math.max(1, (int)math.ceil(math.distance(a, b) / step));
                for (var k = 0; k < pieces; k++) result.Add(math.lerp(a, b, (float)k / pieces));
            }
            return result;
        }

        /// <summary>Smallest calliper width over 18 directions.</summary>
        internal static float MinimumWidth(IReadOnlyList<float2> outline)
        {
            var best = float.MaxValue;
            for (var i = 0; i < 18; i++)
            {
                var angle = math.PI * i / 18f;
                var direction = new float2(math.cos(angle), math.sin(angle));
                float min = float.MaxValue, max = float.MinValue;
                for (var k = 0; k < outline.Count; k++)
                {
                    var p = math.dot(outline[k], direction);
                    min = math.min(min, p);
                    max = math.max(max, p);
                }
                best = math.min(best, max - min);
            }
            return best;
        }

        internal static float Area(IReadOnlyList<float2> outline)
            => (float)System.Math.Abs(PolygonMath.SignedArea(outline));
    }
}
