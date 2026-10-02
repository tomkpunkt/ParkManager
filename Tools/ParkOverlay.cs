// Polygon overlay adapted from ParkingLotTool (GPL-3.0).
// Parking-lot-specific rendering and state were intentionally removed.
using Colossal.Mathematics;
using Game.Rendering;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using ParkManager.Geometry;

namespace ParkManager.Tools
{
    /// <summary>
    /// Stateless projected-overlay renderer for polygon editing, snapping,
    /// gates, path previews and planned furnishing. It never creates persistent
    /// game entities.
    /// </summary>
    internal static class ParkOverlay
    {
        private static readonly Color Edge = new Color(0.20f, 0.85f, 0.42f, 0.95f);
        private static readonly Color PlannerEdge = new Color(0.31f, 0.76f, 0.97f, 0.98f);
        private static readonly Color Preview = new Color(0.35f, 0.78f, 1f, 0.9f);
        private static readonly Color Point = new Color(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color Active = new Color(1f, 0.72f, 0.15f, 1f);
        private static readonly Color Close = new Color(0.25f, 1f, 0.45f, 1f);
        private static readonly Color Entrance = new Color(0.2f, 0.75f, 1f, 1f);
        private static readonly Color Path = new Color(0.95f, 0.78f, 0.2f, 0.95f);
        private static readonly Color BuildIssue = new Color(1f, 0.2f, 0.16f, 1f);
        private static readonly Color LakeShore = new Color(0.08f, 0.38f, 0.85f, 1f);
        private static readonly Color LakeWater = new Color(0.20f, 0.55f, 1f, 0.55f);
        private static readonly Color AnimalSpawner = new Color(0.95f, 0.45f, 0.15f, 1f);
        private static readonly Color Tree = new Color(0.12f, 0.72f, 0.24f, 0.9f);
        private static readonly Color Bush = new Color(0.32f, 0.88f, 0.34f, 0.9f);
        private static readonly Color Bench = new Color(0.48f, 0.25f, 0.09f, 0.98f);
        private static readonly Color Lamp = new Color(1f, 0.88f, 0.12f, 1f);
        private static readonly Color TrashBin = new Color(0.42f, 0.31f, 0.22f, 1f);
        private static readonly Color Fence = new Color(0.86f, 0.72f, 0.48f, 0.9f);
        private static readonly Color SnapRoad = new Color(1f, 0.62f, 0.18f, 1f);
        private static readonly Color SnapObject = new Color(0.75f, 0.65f, 0.91f, 1f);
        private static readonly Color SnapArea = new Color(0.25f, 1f, 0.45f, 1f);
        private static readonly Color SnapDirection = new Color(0.72f, 0.46f, 1f, 1f);
        private static readonly Color SnapGuideColor = new Color(1f, 0.72f, 0.15f, 1f);
        private static readonly Color SnapZone = new Color(0.40f, 0.83f, 0.80f, 1f);

        internal static void Draw(OverlayRenderSystem.Buffer buffer,
            IReadOnlyList<float3> polygon, bool closed, bool hasCursor,
            float3 cursor, bool canClose, int hoverPoint, int dragPoint,
            int hoverEdge, bool plannerMode,
            IReadOnlyList<float3> entrances, int hoverEntrance,
            IReadOnlyList<float3> pathPreview, ParkDecorationPlan decorations,
            bool plaza,
            IReadOnlyList<float3> buildIssues,
            ParkToolSystem.SnapKind snap,
            bool hasSnapGuide, Line3.Segment snapGuide)
        {
            var outline = plannerMode ? PlannerEdge : Edge;

            if (hasCursor && hasSnapGuide)
                buffer.DrawDashedLine(SnapGuideColor, SnapGuideColor, 0f,
                    OverlayRenderSystem.StyleFlags.Projected, snapGuide,
                    0.35f, 2.5f, 1.5f);

            if (hasCursor && snap != ParkToolSystem.SnapKind.None)
                Circle(buffer, SnapColor(snap), cursor, 4.8f);

            for (var i = 1; i < polygon.Count; i++)
                Line(buffer, outline, polygon[i - 1], polygon[i], 0.55f);

            if (closed && polygon.Count > 2)
                Line(buffer, outline, polygon[polygon.Count - 1], polygon[0], 0.55f);
            else if (hasCursor && polygon.Count > 0)
                buffer.DrawDashedLine(canClose ? Close : Preview,
                    canClose ? Close : Preview, 0f,
                    OverlayRenderSystem.StyleFlags.Projected,
                    new Line3.Segment(polygon[polygon.Count - 1],
                        canClose ? polygon[0] : cursor),
                    0.4f, 2.5f, 1.5f);

            if (closed && hoverEdge >= 0 && hoverEdge < polygon.Count)
                Line(buffer, Active, polygon[hoverEdge],
                    polygon[(hoverEdge + 1) % polygon.Count], 1.05f);

            for (var i = 0; i < polygon.Count; i++)
            {
                var active = i == hoverPoint || i == dragPoint;
                var closeTarget = !closed && canClose && i == 0;
                Circle(buffer, closeTarget ? Close : active ? Active : Point,
                    polygon[i], active || closeTarget ? 3.2f : 2.2f);
            }

            if (hasCursor && polygon.Count == 0)
                Circle(buffer, Preview, cursor, 2.2f);

            for (var i = 0; i + 1 < pathPreview.Count; i += 2)
                Line(buffer, Path, pathPreview[i], pathPreview[i + 1], 1.25f);

            for (var i = 0; i < buildIssues.Count; i++)
                Circle(buffer, BuildIssue, buildIssues[i], 8f);

            DrawDecorations(buffer, decorations, plaza,
                polygon.Count > 0 ? polygon[0].y + 0.15f : 0.15f);

            for (var i = 0; i < entrances.Count; i++)
                Circle(buffer, i == hoverEntrance ? Active : Entrance,
                    entrances[i], i == hoverEntrance ? 5.5f : 4.5f);
        }

        /// <summary>
        /// Marks every element of a built park in the remove mode: amber while
        /// hovered, red once the park is selected for removal.
        /// </summary>
        internal static void DrawParkHighlight(OverlayRenderSystem.Buffer buffer,
            IReadOnlyList<float3> lines, IReadOnlyList<float3> points,
            bool selected)
        {
            var color = selected ? BuildIssue : Active;
            for (var i = 0; i + 1 < lines.Count; i += 2)
                Line(buffer, color, lines[i], lines[i + 1], 1.1f);
            for (var i = 0; i < points.Count; i++)
                Circle(buffer, color, points[i], 2.6f);
        }

        private static void DrawDecorations(OverlayRenderSystem.Buffer buffer,
            ParkDecorationPlan plan, bool plaza, float height)
        {
            if (plan == null) return;
            DrawLake(buffer, plan.Lake, height);
            for (var i = 0; i < plan.Placements.Count; i++)
            {
                var item = plan.Placements[i];
                var center = new float3(item.Position.x, height, item.Position.y);
                switch (item.Kind)
                {
                    case ParkDecorationKind.Tree:
                        Circle(buffer, Tree, center, item.Size);
                        break;
                    case ParkDecorationKind.Bush:
                        Circle(buffer, Bush, center, item.Size);
                        break;
                    case ParkDecorationKind.Lamp:
                        Circle(buffer, Lamp, center, item.Size);
                        break;
                    case ParkDecorationKind.Bench:
                        Rectangle(buffer, Bench, center,
                            item.Rotation + (plaza ? math.PI * 0.5f : 0f),
                            3f, 1.15f);
                        break;
                    case ParkDecorationKind.TrashBin:
                        Rectangle(buffer, TrashBin, center, item.Rotation, 1.2f, 1.2f);
                        break;
                    case ParkDecorationKind.AnimalSpawner:
                        Circle(buffer, AnimalSpawner, center, 4f);
                        Circle(buffer, AnimalSpawner, center, 1.2f);
                        break;
                    case ParkDecorationKind.PlazaCenter:
                        Circle(buffer, Lamp, center, math.max(3f, item.Size));
                        break;
                    case ParkDecorationKind.Fence:
                        var forward = new float2(math.sin(item.Rotation),
                            math.cos(item.Rotation));
                        var half = new float3(forward.x, 0f, forward.y)
                            * item.Size * 0.5f;
                        Line(buffer, Fence, center - half, center + half, 0.65f);
                        break;
                }
            }
        }

        /// <summary>
        /// Draws the planned lake as a filled blue area. The overlay has no
        /// polygon fill, so close horizontal scanlines clipped to the outline
        /// read as water, framed by a darker shore line.
        /// </summary>
        private static void DrawLake(OverlayRenderSystem.Buffer buffer,
            IReadOnlyList<float2> lake, float height)
        {
            if (lake == null || lake.Count < 3) return;
            PolygonMath.Bounds(lake, out var min, out var max);
            const float spacing = 2.5f;
            var crossings = new List<float>();
            for (var z = min.y + spacing * 0.5f; z < max.y; z += spacing)
            {
                crossings.Clear();
                for (int i = 0, j = lake.Count - 1; i < lake.Count; j = i++)
                {
                    var a = lake[j];
                    var b = lake[i];
                    if ((a.y > z) == (b.y > z)) continue;
                    crossings.Add(a.x + (z - a.y) / (b.y - a.y) * (b.x - a.x));
                }
                crossings.Sort();
                for (var k = 0; k + 1 < crossings.Count; k += 2)
                    Line(buffer, LakeWater, new float3(crossings[k], height, z),
                        new float3(crossings[k + 1], height, z), spacing * 1.05f);
            }
            for (int i = 0, j = lake.Count - 1; i < lake.Count; j = i++)
                Line(buffer, LakeShore, new float3(lake[j].x, height, lake[j].y),
                    new float3(lake[i].x, height, lake[i].y), 0.9f);
        }

        private static void Rectangle(OverlayRenderSystem.Buffer buffer,
            Color color, float3 center, float rotation, float length, float width)
        {
            var forward = new float2(math.sin(rotation), math.cos(rotation));
            var side = new float2(-forward.y, forward.x);
            var f = new float3(forward.x, 0f, forward.y) * length * 0.5f;
            var s = new float3(side.x, 0f, side.y) * width * 0.5f;
            var a = center - f - s;
            var b = center + f - s;
            var c = center + f + s;
            var d = center - f + s;
            Line(buffer, color, a, b, 0.42f);
            Line(buffer, color, b, c, 0.42f);
            Line(buffer, color, c, d, 0.42f);
            Line(buffer, color, d, a, 0.42f);
        }

        private static void Line(OverlayRenderSystem.Buffer buffer, Color color,
            float3 a, float3 b, float width)
            => buffer.DrawLine(color, color, 0f,
                OverlayRenderSystem.StyleFlags.Projected,
                new Line3.Segment(a, b), width, default);

        private static void Circle(OverlayRenderSystem.Buffer buffer, Color color,
            float3 position, float diameter)
            => buffer.DrawCircle(color, new Color(color.r, color.g, color.b, 0.2f),
                0.25f, OverlayRenderSystem.StyleFlags.Projected,
                new float2(0f, 1f), position, diameter);

        private static Color SnapColor(ParkToolSystem.SnapKind kind)
        {
            switch (kind)
            {
                case ParkToolSystem.SnapKind.RoadEdge: return SnapRoad;
                case ParkToolSystem.SnapKind.ObjectSide: return SnapObject;
                case ParkToolSystem.SnapKind.AreaEdge: return SnapArea;
                case ParkToolSystem.SnapKind.Direction: return SnapDirection;
                case ParkToolSystem.SnapKind.Guide: return SnapGuideColor;
                case ParkToolSystem.SnapKind.ZoneGrid: return SnapZone;
                case ParkToolSystem.SnapKind.Crossing: return Color.white;
                default: return Preview;
            }
        }
    }
}
