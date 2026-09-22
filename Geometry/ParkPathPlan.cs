using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>Semantic role of a node in a generated park path graph.</summary>
    internal enum ParkPathNodeKind
    {
        Interior,
        Gate,
    }

    /// <summary>
    /// Semantic path class used by preview, scoring and game-entity creation.
    /// </summary>
    internal enum ParkPathEdgeKind
    {
        Secondary,
        Primary,
        GateApproach,
    }

    /// <summary>
    /// Immutable-by-convention graph vertex exported by the path planner.
    /// Positions use the local XZ planning plane.
    /// </summary>
    internal sealed class ParkPathNode
    {
        internal int Id;
        internal float2 Position;
        internal ParkPathNodeKind Kind;
    }

    /// <summary>
    /// Connection between two <see cref="ParkPathNode"/> instances, including
    /// its semantic class and requested design width.
    /// </summary>
    internal sealed class ParkPathEdge
    {
        internal int Id;
        internal int A;
        internal int B;
        internal ParkPathEdgeKind Kind;
        internal float Width;
    }

    /// <summary>
    /// Stable hand-off between procedural planning, overlay preview and game
    /// entity creation. The planner may change without changing the builder.
    /// </summary>
    internal sealed class ParkPathPlan
    {
        internal int Seed { get; }
        internal IReadOnlyList<ParkPathNode> Nodes { get; }
        internal IReadOnlyList<ParkPathEdge> Edges { get; }
        internal float TotalLength { get; }

        private ParkPathPlan(int seed, List<ParkPathNode> nodes,
            List<ParkPathEdge> edges, float totalLength)
        {
            Seed = seed;
            Nodes = nodes;
            Edges = edges;
            TotalLength = totalLength;
        }

        internal static ParkPathPlan Empty(int seed)
            => new ParkPathPlan(seed, new List<ParkPathNode>(),
                new List<ParkPathEdge>(), 0f);

        internal static ParkPathPlan FromSegments(int seed,
            IReadOnlyList<float2> segments, IReadOnlyList<float2> gates)
        {
            if (segments == null || segments.Count < 2) return Empty(seed);

            var nodes = new List<ParkPathNode>();
            var edges = new List<ParkPathEdge>();
            var edgeKeys = new HashSet<long>();
            for (var i = 0; i + 1 < segments.Count; i += 2)
            {
                var a = FindOrAddNode(nodes, segments[i], gates);
                var b = FindOrAddNode(nodes, segments[i + 1], gates);
                if (a == b) continue;
                var low = Math.Min(a, b);
                var high = Math.Max(a, b);
                var key = ((long)(uint)low << 32) | (uint)high;
                if (!edgeKeys.Add(key)) continue;
                var length = math.distance(nodes[a].Position, nodes[b].Position);
                if (length < 1f) continue;
                edges.Add(new ParkPathEdge
                {
                    Id = edges.Count,
                    A = a,
                    B = b,
                    Kind = ParkPathEdgeKind.Secondary,
                    Width = 3f,
                });
            }

            SimplifyNetworkGraph(nodes, edges);
            var totalLength = 0f;
            for (var i = 0; i < edges.Count; i++)
            {
                edges[i].Id = i;
                totalLength += math.distance(nodes[edges[i].A].Position,
                    nodes[edges[i].B].Position);
            }
            ClassifyEdges(nodes, edges);
            return new ParkPathPlan(seed, nodes, edges, totalLength);
        }

        /// <summary>
        /// Removes topology that has no useful visible counterpart in CS2.
        /// Every exported edge becomes a separate NetCourse, therefore even a
        /// mathematically redundant degree-two vertex produces a round network
        /// node. Multi-gate layouts also have no use for non-gate dead branches.
        /// Genuine gates, bends and junctions remain untouched.
        /// </summary>
        private static void SimplifyNetworkGraph(List<ParkPathNode> nodes,
            List<ParkPathEdge> edges)
        {
            if (nodes.Count == 0 || edges.Count == 0) return;

            var gateCount = 0;
            for (var i = 0; i < nodes.Count; i++)
                if (nodes[i].Kind == ParkPathNodeKind.Gate) gateCount++;

            // With two or more entrances the useful graph connects terminals
            // or forms loops. A non-gate leaf is only a visual stub ending in a
            // circular cap. For a single entrance one interior leaf is needed.
            if (gateCount > 1)
            {
                var changed = true;
                while (changed)
                {
                    changed = false;
                    var degree = BuildDegree(nodes.Count, edges);
                    for (var node = 0; node < nodes.Count; node++)
                    {
                        if (degree[node] != 1
                            || nodes[node].Kind == ParkPathNodeKind.Gate) continue;
                        for (var edge = edges.Count - 1; edge >= 0; edge--)
                        {
                            if (edges[edge].A != node && edges[edge].B != node) continue;
                            edges.RemoveAt(edge);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            // Collapse only virtually straight vertices. The tight geometric
            // tolerance prevents a shortcut from visibly changing the plan or
            // leaving the polygon, while eliminating nodes on straight runs.
            var collapsed = true;
            while (collapsed)
            {
                collapsed = false;
                var incident = BuildAdjacency(nodes.Count, edges);
                for (var node = 0; node < nodes.Count; node++)
                {
                    if (nodes[node].Kind == ParkPathNodeKind.Gate
                        || incident[node].Count != 2) continue;
                    var firstIndex = incident[node][0];
                    var secondIndex = incident[node][1];
                    var first = edges[firstIndex];
                    var second = edges[secondIndex];
                    var a = first.A == node ? first.B : first.A;
                    var b = second.A == node ? second.B : second.A;
                    if (a == b || HasEdge(edges, a, b)) continue;

                    var center = nodes[node].Position;
                    var da = math.normalizesafe(nodes[a].Position - center);
                    var db = math.normalizesafe(nodes[b].Position - center);
                    if (math.dot(da, db) > -0.985f
                        || DistanceToSegmentSquared(center,
                            nodes[a].Position, nodes[b].Position) > 0.5625f)
                        continue;

                    var high = Math.Max(firstIndex, secondIndex);
                    var low = Math.Min(firstIndex, secondIndex);
                    edges.RemoveAt(high);
                    edges.RemoveAt(low);
                    edges.Add(new ParkPathEdge
                    {
                        A = a,
                        B = b,
                        Kind = ParkPathEdgeKind.Secondary,
                        Width = 3f,
                    });
                    collapsed = true;
                    break;
                }
            }

            CompactGraph(nodes, edges);
        }

        private static int[] BuildDegree(int nodeCount,
            IReadOnlyList<ParkPathEdge> edges)
        {
            var degree = new int[nodeCount];
            for (var i = 0; i < edges.Count; i++)
            {
                degree[edges[i].A]++;
                degree[edges[i].B]++;
            }
            return degree;
        }

        private static bool HasEdge(IReadOnlyList<ParkPathEdge> edges, int a, int b)
        {
            for (var i = 0; i < edges.Count; i++)
                if (edges[i].A == a && edges[i].B == b
                    || edges[i].A == b && edges[i].B == a) return true;
            return false;
        }

        private static void CompactGraph(List<ParkPathNode> nodes,
            List<ParkPathEdge> edges)
        {
            var used = new bool[nodes.Count];
            for (var i = 0; i < edges.Count; i++)
            {
                used[edges[i].A] = true;
                used[edges[i].B] = true;
            }

            var mapping = new int[nodes.Count];
            var compact = new List<ParkPathNode>();
            for (var i = 0; i < nodes.Count; i++)
            {
                mapping[i] = -1;
                if (!used[i]) continue;
                mapping[i] = compact.Count;
                compact.Add(new ParkPathNode
                {
                    Id = compact.Count,
                    Position = nodes[i].Position,
                    Kind = nodes[i].Kind,
                });
            }

            for (var i = 0; i < edges.Count; i++)
            {
                edges[i].A = mapping[edges[i].A];
                edges[i].B = mapping[edges[i].B];
            }
            nodes.Clear();
            nodes.AddRange(compact);
        }

        internal double NaturalnessScore(IReadOnlyList<float2> polygon)
        {
            if (Nodes.Count == 0 || Edges.Count == 0) return double.MaxValue;
            var adjacency = BuildAdjacency(Nodes.Count, Edges);
            double score = Nodes.Count * 0.4 + TotalLength * 0.015;

            for (var i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                var length = math.distance(Nodes[edge.A].Position,
                    Nodes[edge.B].Position);
                if (length < 8f) score += (8f - length) * 5.0;
                if (length > 65f) score += (length - 65f) * 0.35;
            }

            for (var node = 0; node < Nodes.Count; node++)
            {
                var degree = adjacency[node].Count;
                if (degree == 1 && Nodes[node].Kind != ParkPathNodeKind.Gate)
                    score += 75.0;
                if (degree > 3) score += (degree - 3) * 140.0;
                if (degree < 2) continue;

                var minimumAngle = 180.0;
                for (var a = 0; a < adjacency[node].Count; a++)
                for (var b = a + 1; b < adjacency[node].Count; b++)
                {
                    var va = DirectionFrom(node, Edges[adjacency[node][a]]);
                    var vb = DirectionFrom(node, Edges[adjacency[node][b]]);
                    var cosine = math.clamp(math.dot(va, vb), -1f, 1f);
                    var angle = Math.Acos(cosine) * 180.0 / Math.PI;
                    minimumAngle = Math.Min(minimumAngle, angle);
                }

                if (degree == 2 && minimumAngle < 105.0)
                {
                    var difference = 105.0 - minimumAngle;
                    score += difference * difference * 0.055;
                }
                else if (degree >= 3 && minimumAngle < 50.0)
                {
                    var difference = 50.0 - minimumAngle;
                    score += difference * difference * 0.12;
                }
            }

            var area = Math.Abs(SignedArea(polygon));
            var components = CountComponents(adjacency);
            var loops = Math.Max(0, Edges.Count - Nodes.Count + components);
            var wantedLoops = area > 12000.0 ? 2 : area > 1800.0 ? 1 : 0;
            score += Math.Abs(loops - wantedLoops) * 35.0;
            score += CoveragePenalty(polygon);
            return score;
        }

        internal ParkPathPlan Smooth(IReadOnlyList<float2> polygon,
            IReadOnlyList<float2> gates)
        {
            if (Edges.Count < 2) return this;
            var adjacency = BuildAdjacency(Nodes.Count, Edges);
            var visited = new bool[Edges.Count];
            var output = new List<float2>();

            for (var node = 0; node < Nodes.Count; node++)
            {
                if (adjacency[node].Count == 2
                    && Nodes[node].Kind != ParkPathNodeKind.Gate) continue;
                for (var i = 0; i < adjacency[node].Count; i++)
                    if (!visited[adjacency[node][i]])
                        AppendSmoothedChain(node, adjacency[node][i], adjacency,
                            visited, polygon, output);
            }

            // A component consisting only of a loop has no degree != 2 anchor.
            for (var edge = 0; edge < Edges.Count; edge++)
                if (!visited[edge])
                    AppendSmoothedChain(Edges[edge].A, edge, adjacency, visited,
                        polygon, output);

            var smoothed = FromSegments(Seed, output, gates);
            return smoothed.Edges.Count == 0 ? this : smoothed;
        }

        private void AppendSmoothedChain(int startNode, int startEdge,
            IReadOnlyList<List<int>> adjacency, bool[] visited,
            IReadOnlyList<float2> polygon, List<float2> output)
        {
            var chain = new List<float2> { Nodes[startNode].Position };
            var currentNode = startNode;
            var currentEdge = startEdge;
            var guard = Edges.Count + 1;
            while (guard-- > 0 && !visited[currentEdge])
            {
                visited[currentEdge] = true;
                var edge = Edges[currentEdge];
                var nextNode = edge.A == currentNode ? edge.B : edge.A;
                chain.Add(Nodes[nextNode].Position);
                if (nextNode == startNode
                    || adjacency[nextNode].Count != 2
                    || Nodes[nextNode].Kind == ParkPathNodeKind.Gate) break;
                var first = adjacency[nextNode][0];
                currentEdge = first == currentEdge
                    ? adjacency[nextNode][1] : first;
                currentNode = nextNode;
            }

            var candidate = CornerCut(chain);
            if (!ChainInside(candidate, polygon)) candidate = chain;
            for (var i = 0; i + 1 < candidate.Count; i++)
            {
                output.Add(candidate[i]);
                output.Add(candidate[i + 1]);
            }
        }

        private static List<float2> CornerCut(IReadOnlyList<float2> chain)
        {
            if (chain.Count < 3) return new List<float2>(chain);
            var result = new List<float2> { chain[0] };
            for (var i = 1; i + 1 < chain.Count; i++)
            {
                result.Add(math.lerp(chain[i - 1], chain[i], 0.75f));
                result.Add(math.lerp(chain[i], chain[i + 1], 0.25f));
            }
            result.Add(chain[chain.Count - 1]);
            return result;
        }

        private static bool ChainInside(IReadOnlyList<float2> chain,
            IReadOnlyList<float2> polygon)
        {
            for (var i = 0; i + 1 < chain.Count; i++)
            for (var sample = 0; sample <= 8; sample++)
            {
                var point = math.lerp(chain[i], chain[i + 1], sample / 8f);
                if (!PointInsideOrBoundary(point, polygon)) return false;
            }
            return true;
        }

        private double CoveragePenalty(IReadOnlyList<float2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0.0;
            var min = polygon[0];
            var max = polygon[0];
            for (var i = 1; i < polygon.Count; i++)
            {
                min = math.min(min, polygon[i]);
                max = math.max(max, polygon[i]);
            }

            double total = 0.0;
            var samples = 0;
            const int steps = 8;
            for (var y = 1; y < steps; y++)
            for (var x = 1; x < steps; x++)
            {
                var point = math.lerp(min, max,
                    new float2((float)x / steps, (float)y / steps));
                if (!PointInsideOrBoundary(point, polygon)) continue;
                var nearest = float.MaxValue;
                for (var edge = 0; edge < Edges.Count; edge++)
                    nearest = math.min(nearest, DistanceToSegmentSquared(point,
                        Nodes[Edges[edge].A].Position,
                        Nodes[Edges[edge].B].Position));
                total += Math.Sqrt(nearest);
                samples++;
            }
            return samples == 0 ? 0.0 : total / samples * 1.8;
        }

        private static List<int>[] BuildAdjacency(int nodeCount,
            IReadOnlyList<ParkPathEdge> edges)
        {
            var adjacency = new List<int>[nodeCount];
            for (var i = 0; i < nodeCount; i++) adjacency[i] = new List<int>();
            for (var i = 0; i < edges.Count; i++)
            {
                adjacency[edges[i].A].Add(i);
                adjacency[edges[i].B].Add(i);
            }
            return adjacency;
        }

        private float2 DirectionFrom(int node, ParkPathEdge edge)
        {
            var other = edge.A == node ? edge.B : edge.A;
            return math.normalizesafe(Nodes[other].Position - Nodes[node].Position);
        }

        private int CountComponents(IReadOnlyList<List<int>> adjacency)
        {
            var seen = new bool[adjacency.Count];
            var components = 0;
            for (var start = 0; start < adjacency.Count; start++)
            {
                if (seen[start] || adjacency[start].Count == 0) continue;
                components++;
                var stack = new Stack<int>();
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    for (var i = 0; i < adjacency[node].Count; i++)
                    {
                        var edge = Edges[adjacency[node][i]];
                        var other = edge.A == node ? edge.B : edge.A;
                        if (seen[other]) continue;
                        seen[other] = true;
                        stack.Push(other);
                    }
                }
            }
            return Math.Max(1, components);
        }

        private static double SignedArea(IReadOnlyList<float2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0.0;
            double area = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += (double)a.x * b.y - (double)b.x * a.y;
            }
            return area * 0.5;
        }

        private static bool PointInsideOrBoundary(float2 point,
            IReadOnlyList<float2> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[j];
                var b = polygon[i];
                if (DistanceToSegmentSquared(point, a, b) < 0.01f) return true;
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                       / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static float DistanceToSegmentSquared(float2 point, float2 a,
            float2 b)
        {
            var ab = b - a;
            var length = math.lengthsq(ab);
            if (length < 0.0001f) return math.distancesq(point, a);
            var t = math.clamp(math.dot(point - a, ab) / length, 0f, 1f);
            return math.distancesq(point, a + ab * t);
        }

        private static int FindOrAddNode(List<ParkPathNode> nodes, float2 point,
            IReadOnlyList<float2> gates)
        {
            const float mergeDistanceSquared = 0.04f;
            for (var i = 0; i < nodes.Count; i++)
                if (math.distancesq(nodes[i].Position, point) <= mergeDistanceSquared)
                    return i;

            var kind = ParkPathNodeKind.Interior;
            if (gates != null)
                for (var i = 0; i < gates.Count; i++)
                    if (math.distancesq(gates[i], point) <= 0.25f)
                    {
                        kind = ParkPathNodeKind.Gate;
                        break;
                    }
            nodes.Add(new ParkPathNode
            {
                Id = nodes.Count,
                Position = point,
                Kind = kind,
            });
            return nodes.Count - 1;
        }

        private static void ClassifyEdges(IReadOnlyList<ParkPathNode> nodes,
            List<ParkPathEdge> edges)
        {
            var adjacency = new List<int>[nodes.Count];
            for (var i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();
            for (var i = 0; i < edges.Count; i++)
            {
                adjacency[edges[i].A].Add(i);
                adjacency[edges[i].B].Add(i);
            }

            var discovery = new int[nodes.Count];
            var low = new int[nodes.Count];
            var time = 0;
            var bridges = new HashSet<int>();
            for (var i = 0; i < nodes.Count; i++)
                if (discovery[i] == 0)
                    FindBridges(i, -1, adjacency, edges, discovery, low,
                        ref time, bridges);

            for (var i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                if (nodes[edge.A].Kind == ParkPathNodeKind.Gate
                    || nodes[edge.B].Kind == ParkPathNodeKind.Gate)
                {
                    edge.Kind = ParkPathEdgeKind.GateApproach;
                    edge.Width = 4f;
                }
                else if (bridges.Contains(i))
                {
                    edge.Kind = ParkPathEdgeKind.Primary;
                    edge.Width = 4f;
                }
            }
        }

        private static void FindBridges(int node, int parentEdge,
            IReadOnlyList<List<int>> adjacency, IReadOnlyList<ParkPathEdge> edges,
            int[] discovery, int[] low, ref int time, HashSet<int> bridges)
        {
            discovery[node] = low[node] = ++time;
            var incident = adjacency[node];
            for (var i = 0; i < incident.Count; i++)
            {
                var edgeIndex = incident[i];
                if (edgeIndex == parentEdge) continue;
                var edge = edges[edgeIndex];
                var other = edge.A == node ? edge.B : edge.A;
                if (discovery[other] == 0)
                {
                    FindBridges(other, edgeIndex, adjacency, edges, discovery,
                        low, ref time, bridges);
                    low[node] = Math.Min(low[node], low[other]);
                    if (low[other] > discovery[node]) bridges.Add(edgeIndex);
                }
                else low[node] = Math.Min(low[node], discovery[other]);
            }
        }
    }
}
