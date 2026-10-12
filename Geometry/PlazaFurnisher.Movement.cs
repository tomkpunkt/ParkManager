using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    internal static partial class PlazaFurnisher
    {
        private const float WalkCell = 1f;

        /// <summary>
        /// A furnished plaza must still work: from every entrance, every
        /// room has to be reachable on a path of at least
        /// <see cref="PathWidth"/> between objects and beds. Where it is not,
        /// the least important object next to the natural walking line is
        /// taken out, one at a time. Returns how many objects were removed.
        /// </summary>
        private static int KeepWalkable(Context context, out bool walkable)
        {
            var removed = 0;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var grid = new WalkGrid(context);
                if (!grid.FindBlocked(context, out var from, out var to))
                {
                    walkable = true;
                    return removed;
                }
                if (!RemoveBlocker(context, from, to))
                {
                    walkable = false;
                    return removed;
                }
                removed++;
            }
            walkable = false;
            return removed;
        }

        private static bool RemoveBlocker(Context context, float2 from, float2 to)
        {
            var path = context.Movement.ShortestPath(from, to);
            if (path == null) return false;
            var best = -1;
            var bestPriority = int.MinValue;
            var bestDistance = float.MaxValue;
            for (var i = 0; i < context.Placed.Count; i++)
            {
                var occupied = context.Placed[i];
                if (occupied.Centerpiece) continue;
                var distance = PlazaMovement.DistanceToPolyline(occupied.Position, path);
                if (distance > occupied.Radius + PathWidth * 0.5f + 0.6f) continue;
                var priority = context.Motifs[occupied.Motif].Priority;
                if (priority < bestPriority
                    || priority == bestPriority && distance >= bestDistance) continue;
                best = i;
                bestPriority = priority;
                bestDistance = distance;
            }
            if (best < 0) return false;
            var target = context.Placed[best];
            context.Placed.RemoveAt(best);
            context.Motifs[target.Motif].Placements.RemoveAll(placement =>
                math.distancesq(placement.Position, target.Position) < 1e-6f);
            return true;
        }

        /// <summary>Walkable cells of the plaza at 1 m with the current objects.</summary>
        private sealed class WalkGrid
        {
            private readonly float2 _origin;
            private readonly int _columns;
            private readonly int _rows;
            private readonly bool[] _free;

            internal WalkGrid(Context context)
            {
                var geometry = context.Geometry;
                PolygonMath.Bounds(geometry.Polygon, out var min, out var max);
                _origin = min;
                _columns = (int)math.ceil((max.x - min.x) / WalkCell) + 1;
                _rows = (int)math.ceil((max.y - min.y) / WalkCell) + 1;
                _free = new bool[_columns * _rows];
                for (var index = 0; index < _free.Length; index++)
                {
                    var point = Center(index);
                    _free[index] = geometry.Contains(point)
                        && geometry.DistanceToBoundary(point) >= 0.4f;
                }
                foreach (var zone in context.Design.GreenZones)
                    Mark(zone.Polygon);
                var half = PathWidth * 0.5f;
                foreach (var occupied in context.Placed)
                    MarkDisc(occupied.Position, occupied.Radius + half);
            }

            private float2 Center(int index)
                => _origin + new float2(index % _columns, index / _columns) * WalkCell;

            private int Cell(float2 point)
            {
                var x = (int)math.round((point.x - _origin.x) / WalkCell);
                var y = (int)math.round((point.y - _origin.y) / WalkCell);
                return x < 0 || y < 0 || x >= _columns || y >= _rows ? -1 : y * _columns + x;
            }

            private void Mark(List<float2> polygon)
            {
                PolygonMath.Bounds(polygon, out var min, out var max);
                for (var y = min.y; y <= max.y + WalkCell; y += WalkCell)
                for (var x = min.x; x <= max.x + WalkCell; x += WalkCell)
                {
                    var cell = Cell(new float2(x, y));
                    if (cell >= 0 && PolygonMath.PointInside(Center(cell), polygon))
                        _free[cell] = false;
                }
            }

            private void MarkDisc(float2 center, float radius)
            {
                var reach = (int)math.ceil(radius / WalkCell) + 1;
                var middle = Cell(center);
                if (middle < 0) return;
                var cx = middle % _columns;
                var cy = middle / _columns;
                for (var dy = -reach; dy <= reach; dy++)
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 0 || y < 0 || x >= _columns || y >= _rows) continue;
                    var cell = y * _columns + x;
                    if (math.distance(Center(cell), center) < radius) _free[cell] = false;
                }
            }

            /// <summary>Nearest free cell within a few metres of a point.</summary>
            private int FreeNear(float2 point, float radius)
            {
                var best = -1;
                var bestDistance = float.MaxValue;
                var reach = (int)math.ceil(radius / WalkCell);
                var middle = Cell(point);
                if (middle < 0) return -1;
                for (var dy = -reach; dy <= reach; dy++)
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var x = middle % _columns + dx;
                    var y = middle / _columns + dy;
                    if (x < 0 || y < 0 || x >= _columns || y >= _rows) continue;
                    var cell = y * _columns + x;
                    if (!_free[cell]) continue;
                    var distance = math.distance(Center(cell), point);
                    if (distance > radius || distance >= bestDistance) continue;
                    best = cell;
                    bestDistance = distance;
                }
                return best;
            }

            private bool[] Reach(int start)
            {
                var seen = new bool[_free.Length];
                var queue = new Queue<int>();
                seen[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var x = index % _columns;
                    var y = index / _columns;
                    if (x > 0) Visit(index - 1);
                    if (x + 1 < _columns) Visit(index + 1);
                    if (y > 0) Visit(index - _columns);
                    if (y + 1 < _rows) Visit(index + _columns);
                }
                return seen;

                void Visit(int next)
                {
                    if (!_free[next] || seen[next]) return;
                    seen[next] = true;
                    queue.Enqueue(next);
                }
            }

            /// <summary>
            /// Finds an entrance and a room that are not connected; from and
            /// to are their positions for the repair.
            /// </summary>
            internal bool FindBlocked(Context context, out float2 from, out float2 to)
            {
                from = to = float2.zero;
                var design = context.Design;
                foreach (var entrance in context.Movement.Entrances)
                {
                    var inside = context.Geometry.InsideEntrance(entrance, 1.5f);
                    var start = FreeNear(inside, 4f);
                    if (start < 0) continue;
                    var seen = Reach(start);
                    foreach (var room in design.Rooms)
                    {
                        if (room.Role == PlazaRoomRole.Passage) continue;
                        if (Reaches(seen, room)) continue;
                        from = inside;
                        to = room.Center;
                        return true;
                    }
                }
                return false;
            }

            private bool Reaches(bool[] seen, PlazaRoom room)
            {
                PolygonMath.Bounds(room.Boundary, out var min, out var max);
                var any = false;
                for (var y = min.y; y <= max.y; y += WalkCell)
                for (var x = min.x; x <= max.x; x += WalkCell)
                {
                    var cell = Cell(new float2(x, y));
                    if (cell < 0 || !_free[cell]) continue;
                    if (!room.Shape.Contains(Center(cell), room.Boundary)) continue;
                    any = true;
                    if (seen[cell]) return true;
                }
                // A room without any free cell (fully planted) is not a target.
                return !any;
            }
        }
    }
}
