using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaComposer
    {
        /// <summary>The four sides of a room in its own frame.</summary>
        private enum Side : byte { EndPlus = 0, SidePlus = 1, EndMinus = 2, SideMinus = 3 }

        /// <summary>
        /// Splits every room into its four sides and decides each side on
        /// its own: open where people arrive, trees, green, seating or a
        /// built edge, following topology and grammar.
        /// </summary>
        private static void AssignEdgeRoles(Context context)
        {
            var design = context.Design;
            var random = new Unity.Mathematics.Random(Seeds.Mix(context.Seed, 0x510e527fu));
            for (var r = 0; r < design.Rooms.Count; r++)
            {
                var room = design.Rooms[r];
                if (room.Role == PlazaRoomRole.Passage) continue;
                var edges = SplitIntoSides(room);
                foreach (var edge in edges)
                {
                    edge.Rim = EdgeRim(context, room, edge);
                    edge.Arrival = HasArrival(context, r, edge);
                }
                AssignRoles(context, r, room, edges, ref random);
                room.Edges.AddRange(edges);
            }
        }

        private static List<PlazaRoomEdge> SplitIntoSides(PlazaRoom room)
        {
            var dense = PlazaShape.Densify(room.Boundary, 1f);
            var u = room.Axis;
            var v = new float2(-u.y, u.x);
            var halfLength = math.max(0.5f, room.Shape.Kind == PlazaCoreShape.Strip
                ? 1f : room.Shape.HalfLength);
            var halfWidth = math.max(0.5f, room.Shape.Kind == PlazaCoreShape.Strip
                ? 1f : room.Shape.HalfWidth);
            if (room.Shape.Kind == PlazaCoreShape.BoundaryAligned
                || room.Shape.Kind == PlazaCoreShape.ConvexPolygon)
            {
                // Polygon-based rooms: measure their extent along the frame.
                float maxU = 0.5f, maxV = 0.5f;
                foreach (var p in room.Boundary)
                {
                    maxU = math.max(maxU, math.abs(math.dot(p - room.Center, u)));
                    maxV = math.max(maxV, math.abs(math.dot(p - room.Center, v)));
                }
                halfLength = maxU;
                halfWidth = maxV;
            }
            var sides = new Side[dense.Count];
            for (var i = 0; i < dense.Count; i++)
            {
                var d = dense[i] - room.Center;
                var x = math.dot(d, u) / halfLength;
                var y = math.dot(d, v) / halfWidth;
                sides[i] = math.abs(x) >= math.abs(y)
                    ? (x >= 0f ? Side.EndPlus : Side.EndMinus)
                    : (y >= 0f ? Side.SidePlus : Side.SideMinus);
            }
            // Start at a side change so no run wraps around index 0.
            var start = 0;
            for (var i = 0; i < dense.Count; i++)
                if (sides[i] != sides[(i + dense.Count - 1) % dense.Count])
                {
                    start = i;
                    break;
                }
            var byside = new Dictionary<Side, PlazaRoomEdge>();
            for (var k = 0; k < dense.Count; k++)
            {
                var i = (start + k) % dense.Count;
                if (!byside.TryGetValue(sides[i], out var edge))
                {
                    edge = new PlazaRoomEdge { Outward = SideDirection(sides[i], u, v) };
                    byside[sides[i]] = edge;
                }
                edge.Points.Add(dense[i]);
            }
            var result = new List<PlazaRoomEdge>();
            foreach (Side side in System.Enum.GetValues(typeof(Side)))
                if (byside.TryGetValue(side, out var edge) && edge.Points.Count >= 2)
                    result.Add(edge);
            return result;
        }

        private static float2 SideDirection(Side side, float2 u, float2 v)
        {
            switch (side)
            {
                case Side.EndPlus: return u;
                case Side.EndMinus: return -u;
                case Side.SidePlus: return v;
                default: return -v;
            }
        }

        private static bool IsSide(PlazaRoomEdge edge, PlazaRoom room)
            => math.abs(math.dot(edge.Outward, room.Axis)) < 0.5f;

        /// <summary>Free depth outside the edge up to the plot or another room.</summary>
        private static float EdgeRim(Context context, PlazaRoom room, PlazaRoomEdge edge)
        {
            var total = 0f;
            var samples = 0;
            for (var k = 1; k <= 3; k++)
            {
                var point = edge.Points[edge.Points.Count * k / 4];
                var reach = 0f;
                while (reach < context.Geometry.MajorSpan)
                {
                    var probe = point + edge.Outward * (reach + 0.5f);
                    if (!context.Geometry.Contains(probe)) break;
                    var blocked = false;
                    foreach (var other in context.Design.Rooms)
                        if (other != room && other.Shape.Contains(probe, other.Boundary))
                            blocked = true;
                    if (blocked) break;
                    reach += 0.5f;
                }
                total += reach;
                samples++;
            }
            return total / samples;
        }

        private static bool HasArrival(Context context, int roomIndex, PlazaRoomEdge edge)
        {
            foreach (var corridor in context.Design.MovementCorridors)
            {
                // Corridors end at their room; passages touch both rooms.
                var ends = new[] { corridor.Points[0], corridor.Points[corridor.Points.Count - 1] };
                foreach (var end in ends)
                    if (PlazaMovement.DistanceToPolyline(end, edge.Points) < 3f) return true;
            }
            var room = context.Design.Rooms[roomIndex];
            foreach (var other in room.ConnectedRooms)
            {
                var neighbour = context.Design.Rooms[other];
                if (neighbour.Role == PlazaRoomRole.Passage) continue;
                foreach (var point in edge.Points)
                    if (DistanceToOutline(point, neighbour.Boundary) < 1.5f) return true;
            }
            return false;
        }

        private static void AssignRoles(Context context, int roomIndex, PlazaRoom room,
            List<PlazaRoomEdge> edges, ref Unity.Mathematics.Random random)
        {
            var grammar = context.Design.Grammar;
            var linear = context.Design.Topology == PlazaTopology.Linear;
            // Sides sorted by depth: the deeper side carries green in asymmetric designs.
            var sides = edges.FindAll(edge => IsSide(edge, room));
            sides.Sort((a, b) => b.Rim.CompareTo(a.Rim));
            var ends = edges.FindAll(edge => !IsSide(edge, room));

            switch (room.Role)
            {
                case PlazaRoomRole.EntranceCourt:
                    foreach (var edge in edges)
                        edge.Role = edge.Arrival || edge.Rim < 1f ? PlazaEdgeRole.Open
                            : PlazaEdgeRole.ArchitecturalEdge;
                    return;
                case PlazaRoomRole.GardenCourt:
                    foreach (var edge in edges)
                        edge.Role = edge.Arrival ? PlazaEdgeRole.Open : PlazaEdgeRole.GreenBorder;
                    return;
                case PlazaRoomRole.SecondarySquare when linear:
                    // A pocket opens onto the linear space and is backed by green.
                    foreach (var edge in edges)
                    {
                        var towardsMain = math.dot(edge.Outward,
                            context.Design.Rooms[0].Center - room.Center) > 0f;
                        edge.Role = towardsMain || edge.Arrival ? PlazaEdgeRole.Open
                            : IsSide(edge, room) ? (grammar == PlazaGrammar.Classic
                                ? PlazaEdgeRole.SeatingEdge : PlazaEdgeRole.GreenBorder)
                            : PlazaEdgeRole.Open;
                    }
                    return;
            }

            foreach (var edge in sides)
                edge.Role = grammar == PlazaGrammar.Natural ? PlazaEdgeRole.GreenBorder
                    : PlazaEdgeRole.TreeRow;
            if (grammar == PlazaGrammar.Modern && sides.Count > 0)
            {
                // Asymmetric: green along the deeper side, one tree row opposite.
                sides[0].Role = PlazaEdgeRole.GreenBorder;
                for (var i = 1; i < sides.Count; i++) sides[i].Role = PlazaEdgeRole.TreeRow;
            }
            if (room.Role == PlazaRoomRole.SecondarySquare && grammar == PlazaGrammar.Classic
                && sides.Count > 1)
            {
                // A smaller formal room: one framed side, one seating side.
                sides[1].Role = PlazaEdgeRole.SeatingEdge;
            }

            var seatingGiven = false;
            foreach (var edge in ends)
            {
                if (linear && room.Role == PlazaRoomRole.MainSquare)
                {
                    edge.Role = PlazaEdgeRole.Open;
                    continue;
                }
                if (edge.Arrival)
                {
                    edge.Role = PlazaEdgeRole.Open;
                    continue;
                }
                if (!seatingGiven)
                {
                    edge.Role = PlazaEdgeRole.SeatingEdge;
                    seatingGiven = true;
                    continue;
                }
                edge.Role = grammar == PlazaGrammar.Classic ? PlazaEdgeRole.SeatingEdge
                    : grammar == PlazaGrammar.Natural ? PlazaEdgeRole.GreenBorder
                    : PlazaEdgeRole.ArchitecturalEdge;
            }

            // Too little depth for planting: a built edge instead of green.
            foreach (var edge in edges)
            {
                if (edge.Role == PlazaEdgeRole.GreenBorder && edge.Rim < GreenGap + 2.5f)
                    edge.Role = edge.Arrival ? PlazaEdgeRole.Open : PlazaEdgeRole.SeatingEdge;
                if (edge.Role == PlazaEdgeRole.SeatingEdge && edge.Rim < 1.5f && !edge.Arrival)
                    edge.Role = PlazaEdgeRole.ArchitecturalEdge;
            }
        }
    }
}
