using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        /// <summary>
        /// Soft quality measures (0–1). They do not reject a design; they
        /// show where it is weak and later steer the choice between
        /// variants. Each grammar is judged by its own idea of identity.
        /// </summary>
        private static void ScoreDesign(Context context)
        {
            var design = context.Design;
            var metrics = design.Metrics;
            metrics.SpatialHierarchy = HierarchyScore(context);
            metrics.GreenComposition = GreenScore(context);
            metrics.EdgeDiversity = EdgeScore(design.Rooms[0]);
            metrics.StyleIdentity = IdentityScore(context);
            metrics.EntranceIntegration = EntranceScore(context);
        }

        private static float HierarchyScore(Context context)
        {
            var areas = new List<float>();
            foreach (var room in context.Design.Rooms)
                if (room.Role == PlazaRoomRole.MainSquare
                    || room.Role == PlazaRoomRole.SecondarySquare
                    || room.Role == PlazaRoomRole.GardenCourt)
                    areas.Add(PlazaShape.Area(room.Boundary));
            areas.Sort((a, b) => b.CompareTo(a));
            if (areas.Count == 1)
            {
                var share = areas[0] / context.Geometry.Area;
                return share >= 0.25f && share <= 0.55f ? 0.85f : 0.55f;
            }
            // The main room should clearly dominate the next one.
            return math.saturate((areas[0] / math.max(1f, areas[1]) - 1f) / 1.2f);
        }

        private static float GreenScore(Context context)
        {
            var zones = context.Design.GreenZones;
            var share = 0f;
            var quality = 0f;
            foreach (var zone in zones)
            {
                var area = PlazaShape.Area(zone.Polygon);
                share += area;
                // Penalise slivers and crumbs: a bed needs real width.
                var width = PlazaShape.MinimumWidth(zone.Polygon);
                quality += math.saturate(width / 4f) * math.saturate(area / 60f);
            }
            share /= context.Geometry.Area;
            var target = (context.Rules.GreenMinimum + context.Rules.GreenMaximum) * 0.5f;
            var range = (context.Rules.GreenMaximum - context.Rules.GreenMinimum) * 0.5f;
            var shareFit = math.saturate(1f - math.max(0f, math.abs(share - target) - range)
                / math.max(0.05f, target));
            var shapeFit = zones.Count > 0 ? quality / zones.Count : 0f;
            var fragmentation = 1f - math.saturate((zones.Count - 6) / 6f);
            return 0.5f * shareFit + 0.3f * shapeFit + 0.2f * fragmentation;
        }

        private static float EdgeScore(PlazaRoom main)
        {
            var roles = new HashSet<PlazaEdgeRole>();
            foreach (var edge in main.Edges) roles.Add(edge.Role);
            switch (roles.Count)
            {
                case 0:
                case 1: return 0.2f;
                case 2: return 0.6f;
                case 3: return 0.9f;
                default: return 1f;
            }
        }

        private static float IdentityScore(Context context)
        {
            var design = context.Design;
            var main = design.Rooms[0];
            var normal = new float2(-main.Axis.y, main.Axis.x);
            // Green balance across the main axis.
            float left = 0f, right = 0f;
            var organic = 0;
            var turned = 0;
            foreach (var zone in design.GreenZones)
            {
                var area = PlazaShape.Area(zone.Polygon);
                var side = math.dot(PlazaGeometry.AreaCentroid(zone.Polygon) - main.Center,
                    normal);
                if (side >= 0f) left += area;
                else right += area;
                if (zone.Polygon.Count >= 12) organic++;
                if (zone.Kind == PlazaGreenKind.Island || zone.Polygon.Count == 4
                    && math.abs(math.dot(math.normalizesafe(zone.Polygon[1] - zone.Polygon[0]),
                        math.normalizesafe(zone.Polygon[3] - zone.Polygon[0]))) > 0.15f)
                    turned++;
            }
            var balance = left + right > 0f
                ? 1f - math.abs(left - right) / (left + right) : 1f;
            switch (design.Grammar)
            {
                case PlazaGrammar.Classic:
                    var aligned = math.max(math.abs(math.dot(main.Axis,
                        context.Geometry.MajorAxis)), math.abs(math.dot(main.Axis,
                        context.Geometry.MinorAxis)));
                    return 0.5f * balance + 0.3f * aligned
                        + 0.2f * (design.TreeRows.Count > 0 ? 1f : 0f);
                case PlazaGrammar.Modern:
                    var asymmetry = 1f - balance;
                    var dynamic = main.Shape.Kind == PlazaCoreShape.Parallelogram
                        || turned > 0 ? 1f : 0f;
                    return 0.5f * math.saturate(asymmetry / 0.4f) + 0.5f * dynamic;
                default:
                    var share = design.GreenZones.Count > 0
                        ? (float)organic / design.GreenZones.Count : 0f;
                    var greenShare = 0f;
                    foreach (var zone in design.GreenZones)
                        greenShare += PlazaShape.Area(zone.Polygon);
                    greenShare /= context.Geometry.Area;
                    return 0.6f * share + 0.4f * math.saturate(greenShare / 0.3f);
            }
        }

        private static float EntranceScore(Context context)
        {
            var design = context.Design;
            if (context.Movement.Entrances.Count == 0) return 1f;
            var focal = design.FocalPoints.Count > 0 ? design.FocalPoints[0].Position
                : design.Rooms[0].Center;
            var total = 0f;
            foreach (var corridor in design.MovementCorridors)
            {
                if (corridor.Entrance < 0) continue;
                var approach = PlazaMovement.PolylineLength(corridor.Points);
                var close = 1f - math.saturate((approach - 12f) / 28f);
                var court = design.Rooms[corridor.Room].Role == PlazaRoomRole.EntranceCourt
                    ? 1f : 0f;
                foreach (var room in design.Rooms)
                    if (room.Role == PlazaRoomRole.EntranceCourt
                        && room.Shape.Contains(context.Movement.Entrances[corridor.Entrance]
                            + float2.zero, room.Boundary)) court = 1f;
                var seen = context.Geometry.SegmentInside(
                    context.Movement.Entrances[corridor.Entrance], focal) ? 1f : 0f;
                total += math.max(close, court) * 0.6f + seen * 0.4f;
            }
            return total / context.Movement.Entrances.Count;
        }
    }
}
