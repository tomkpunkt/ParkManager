using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Where people would walk on the empty plaza. Desire lines are the
    /// shortest connections between entrances inside the polygon; the
    /// movement field spreads them into a 0–1 heat map on the analysis grid.
    /// </summary>
    internal sealed class PlazaMovement
    {
        /// <summary>Concave corners are rounded by this much on shortest paths.</summary>
        private const float CornerOffset = 0.75f;

        private readonly PlazaGeometry _geometry;
        private readonly List<float2> _nodes = new List<float2>();
        private readonly List<List<int>> _visible = new List<List<int>>();

        internal readonly List<float2> Entrances = new List<float2>();
        internal readonly List<List<float2>> DesireLines = new List<List<float2>>();
        internal readonly List<float> DesireWeights = new List<float>();
        /// <summary>Normalized movement pressure per grid cell.</summary>
        internal readonly float[] Field;
        internal readonly float Sigma;

        internal PlazaMovement(PlazaGeometry geometry, IReadOnlyList<float2> entrances)
        {
            _geometry = geometry;
            BuildVisibilityGraph();
            if (entrances != null)
                for (var i = 0; i < entrances.Count; i++)
                    if (math.all(math.isfinite(entrances[i])))
                        Entrances.Add(geometry.InsideEntrance(entrances[i]));

            if (Entrances.Count >= 2)
            {
                for (var i = 0; i < Entrances.Count; i++)
                for (var j = i + 1; j < Entrances.Count; j++)
                    AddDesireLine(Entrances[i], Entrances[j], 1f);
            }
            else if (Entrances.Count == 1)
            {
                // A single entrance leads into the plaza's widest space.
                AddDesireLine(Entrances[0], geometry.Pole, 1f);
            }

            Sigma = math.clamp(math.sqrt(geometry.Area) / 14f, 2f, 9f);
            Field = new float[geometry.Inside.Length];
            BuildField();
        }

        private void AddDesireLine(float2 from, float2 to, float weight)
        {
            var path = ShortestPath(from, to);
            if (path == null || path.Count < 2) return;
            DesireLines.Add(path);
            DesireWeights.Add(weight);
        }

        private void BuildField()
        {
            var twoSigmaSquared = 2f * Sigma * Sigma;
            var maximum = 0f;
            for (var index = 0; index < Field.Length; index++)
            {
                if (!_geometry.Inside[index]) continue;
                var point = _geometry.CellCenter(index);
                var value = 0f;
                for (var line = 0; line < DesireLines.Count; line++)
                {
                    var distance = DistanceToPolyline(point, DesireLines[line]);
                    value += DesireWeights[line]
                        * math.exp(-distance * distance / twoSigmaSquared);
                }
                Field[index] = value;
                maximum = math.max(maximum, value);
            }
            if (maximum <= 0f) return;
            for (var index = 0; index < Field.Length; index++) Field[index] /= maximum;
        }

        /// <summary>Movement pressure at a point (0 outside the polygon).</summary>
        internal float At(float2 point)
            => _geometry.TryCell(point, out var index) && _geometry.Inside[index]
                ? Field[index] : 0f;

        /// <summary>
        /// Shortest walkable route between two inside points, as a polyline.
        /// Returns null if they cannot be connected.
        /// </summary>
        internal List<float2> ShortestPath(float2 from, float2 to)
        {
            if (_geometry.SegmentInside(from, to)) return new List<float2> { from, to };
            var count = _nodes.Count;
            var distance = new float[count + 2];
            var previous = new int[count + 2];
            var done = new bool[count + 2];
            for (var i = 0; i < distance.Length; i++)
            {
                distance[i] = float.MaxValue;
                previous[i] = -1;
            }
            var start = count;
            var goal = count + 1;
            distance[start] = 0f;
            var position = new float2[count + 2];
            for (var i = 0; i < count; i++) position[i] = _nodes[i];
            position[start] = from;
            position[goal] = to;
            var startVisible = new bool[count];
            var goalVisible = new bool[count];
            for (var i = 0; i < count; i++)
            {
                startVisible[i] = _geometry.SegmentInside(from, _nodes[i]);
                goalVisible[i] = _geometry.SegmentInside(_nodes[i], to);
            }

            while (true)
            {
                var current = -1;
                var best = float.MaxValue;
                for (var i = 0; i < distance.Length; i++)
                    if (!done[i] && distance[i] < best)
                    {
                        best = distance[i];
                        current = i;
                    }
                if (current < 0) return null;
                if (current == goal) break;
                done[current] = true;
                if (current == start)
                {
                    for (var i = 0; i < count; i++)
                        if (startVisible[i]) Relax(current, i);
                    continue;
                }
                for (var k = 0; k < _visible[current].Count; k++)
                    Relax(current, _visible[current][k]);
                if (goalVisible[current]) Relax(current, goal);
            }

            var path = new List<float2>();
            for (var node = goal; node >= 0; node = previous[node]) path.Add(position[node]);
            path.Reverse();
            return path;

            void Relax(int from2, int to2)
            {
                var candidate = distance[from2] + math.distance(position[from2], position[to2]);
                if (candidate >= distance[to2]) return;
                distance[to2] = candidate;
                previous[to2] = from2;
            }
        }

        private void BuildVisibilityGraph()
        {
            var polygon = _geometry.Polygon;
            for (var i = 0; i < polygon.Count; i++)
            {
                var previous = polygon[(i + polygon.Count - 1) % polygon.Count];
                var current = polygon[i];
                var next = polygon[(i + 1) % polygon.Count];
                // Counter-clockwise polygon: a right turn is a reflex corner.
                if (PlazaGeometry.Cross(current - previous, next - current) >= 0f) continue;
                var inward = math.normalizesafe(
                    _geometry.InwardNormal((i + polygon.Count - 1) % polygon.Count)
                    + _geometry.InwardNormal(i));
                var node = current + inward * CornerOffset;
                if (_geometry.Contains(node)) _nodes.Add(node);
            }
            for (var i = 0; i < _nodes.Count; i++) _visible.Add(new List<int>());
            for (var i = 0; i < _nodes.Count; i++)
            for (var j = i + 1; j < _nodes.Count; j++)
            {
                if (!_geometry.SegmentInside(_nodes[i], _nodes[j])) continue;
                _visible[i].Add(j);
                _visible[j].Add(i);
            }
        }

        internal static float DistanceToPolyline(float2 point, IReadOnlyList<float2> line)
        {
            var best = float.MaxValue;
            for (var i = 0; i + 1 < line.Count; i++)
                best = math.min(best, PolygonMath.DistanceToSegmentSquared(point,
                    line[i], line[i + 1]));
            return math.sqrt(best);
        }

        internal static float PolylineLength(IReadOnlyList<float2> line)
        {
            var length = 0f;
            for (var i = 0; i + 1 < line.Count; i++)
                length += math.distance(line[i], line[i + 1]);
            return length;
        }
    }
}
