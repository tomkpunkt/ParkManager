using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Self-contained hybrid park path planner. It deliberately has no third-party
    /// runtime dependencies because the CS2 mod loader may treat extra assemblies
    /// as separate mod assets and abort registration before OnLoad is reached.
    /// </summary>
    internal static class ParkPathPlanner
    {
        private const double BoundaryClearance = 5.0;
        private const double PreferredBoundaryClearance = 12.0;
        private const double MinimumGateDirectionCosine = 0.7071067811865476;
        private const double MinimumPathSeparation = 8.0;
        private const double MaximumAcuteBranchCosine = 0.8660254037844386;
        private const int MaximumRoadmapNodes = 420;

        /// <summary>
        /// Internal roadmap vertex with its boundary clearance and optional
        /// gate-approach role. It exists only during one planning run.
        /// </summary>
        private sealed class Node
        {
            internal float2 Position;
            internal double Clearance;
            internal bool GateApproach;
        }

        /// <summary>
        /// Triangle produced by polygon triangulation and the roadmap node
        /// associated with its interior.
        /// </summary>
        private sealed class Triangle
        {
            internal int A;
            internal int B;
            internal int C;
            internal int NodeIndex;
        }

        /// <summary>
        /// Distance-ranked roadmap candidate used while selecting local graph
        /// connections.
        /// </summary>
        private sealed class Candidate
        {
            internal int Index;
            internal double Distance;
        }

        /// <summary>
        /// Mutable result of an internal shortest-path search. A path is valid
        /// only when it contains at least two nodes and has finite cost.
        /// </summary>
        private sealed class Path
        {
            internal readonly List<int> Nodes = new List<int>();
            internal double Cost = double.MaxValue;
            internal bool Found => Nodes.Count > 1 && Cost < double.MaxValue;
        }

        internal static ParkPathPlan Generate(IReadOnlyList<float2> polygonPoints,
            IReadOnlyList<float2> entrancePoints, float2 fallbackInteriorPoint, int seed)
        {
            var output = new List<float2>();
            if (polygonPoints == null || polygonPoints.Count < 3
                || entrancePoints == null || entrancePoints.Count == 0)
                return ParkPathPlan.Empty(seed);

            try
            {
                var polygon = NormalizePolygon(polygonPoints);
                if (polygon.Count < 3) return ParkPathPlan.Empty(seed);

                var area = Math.Abs(SignedArea(polygon));
                if (area < 1.0) return ParkPathPlan.Empty(seed);

                var triangles = Triangulate(polygon, seed);
                var nodes = new List<Node>();
                for (var i = 0; i < entrancePoints.Count; i++)
                {
                    nodes.Add(new Node
                    {
                        Position = entrancePoints[i],
                        Clearance = 0.0
                    });
                }

                AddTriangleNodes(nodes, triangles, polygon);
                var spacing = CalculateSamplingSpacing(polygon, area);
                AddInteriorSamples(nodes, polygon, spacing, entrancePoints.Count, seed);

                if (nodes.Count == entrancePoints.Count)
                {
                    nodes.Add(new Node
                    {
                        Position = fallbackInteriorPoint,
                        Clearance = DistanceToBoundary(fallbackInteriorPoint, polygon)
                    });
                }

                var gateApproaches = AddGateApproachNodes(nodes, polygon,
                    entrancePoints.Count, spacing);

                var graph = new double[nodes.Count, nodes.Count];
                ConnectTriangleDualGraph(graph, nodes, triangles, polygon, seed);
                ConnectInteriorRoadmap(graph, nodes, polygon, entrancePoints.Count, spacing, seed);
                ConnectTerminals(graph, nodes, polygon, entrancePoints.Count,
                    gateApproaches, seed);

                var networkNodes = new HashSet<int>();
                var networkEdges = new HashSet<long>();

                if (entrancePoints.Count == 1)
                {
                    var anchor = SelectCoverageAnchor(nodes, entrancePoints.Count,
                        networkNodes, polygon, seed);
                    if (anchor >= 0)
                    {
                        AddPath(ShortestPath(0, new HashSet<int> { anchor }, graph, null),
                            nodes, networkNodes, networkEdges, output);
                    }
                }
                else
                {
                    BuildTerminalMst(nodes, graph, entrancePoints.Count,
                        networkNodes, networkEdges, output);
                }

                var loopBudget = area > 12000.0 ? 2 : area > 1800.0 ? 1 : 0;
                for (var i = 0; i < loopBudget; i++)
                {
                    if (!AddCoverageLoop(nodes, entrancePoints.Count, graph, polygon,
                        networkNodes, networkEdges, output, seed + i * 7919))
                        break;
                }

                AddStretchLoop(nodes, graph, networkNodes, networkEdges, output);
                return ParkPathPlan.FromSegments(seed, output, entrancePoints);
            }
            catch (Exception exception)
            {
                Mod.Log.Warn("ParkManager path planner failed: " + exception.Message);
                return ParkPathPlan.Empty(seed);
            }
        }

        private static List<float2> NormalizePolygon(IReadOnlyList<float2> source)
        {
            var result = new List<float2>();
            for (var i = 0; i < source.Count; i++)
            {
                if (result.Count == 0 || Distance(result[result.Count - 1], source[i]) > 0.01)
                    result.Add(source[i]);
            }

            if (result.Count > 2 && Distance(result[0], result[result.Count - 1]) < 0.01)
                result.RemoveAt(result.Count - 1);

            var changed = true;
            while (changed && result.Count > 3)
            {
                changed = false;
                for (var i = 0; i < result.Count; i++)
                {
                    var previous = result[(i + result.Count - 1) % result.Count];
                    var current = result[i];
                    var next = result[(i + 1) % result.Count];
                    if (DistancePointToSegment(current, previous, next) > 0.01) continue;
                    result.RemoveAt(i);
                    changed = true;
                    break;
                }
            }

            if (SignedArea(result) < 0.0) result.Reverse();
            return result;
        }

        private static List<Triangle> Triangulate(IReadOnlyList<float2> polygon, int seed)
        {
            var result = new List<Triangle>();
            var remaining = new List<int>();
            var start = PositiveModulo(seed, polygon.Count);
            for (var i = 0; i < polygon.Count; i++)
                remaining.Add((start + i) % polygon.Count);

            var guard = polygon.Count * polygon.Count;
            while (remaining.Count > 3 && guard-- > 0)
            {
                var earFound = false;
                for (var cursor = 0; cursor < remaining.Count; cursor++)
                {
                    var previous = remaining[(cursor + remaining.Count - 1) % remaining.Count];
                    var current = remaining[cursor];
                    var next = remaining[(cursor + 1) % remaining.Count];
                    if (Cross(polygon[previous], polygon[current], polygon[next]) <= 0.0001)
                        continue;

                    var containsVertex = false;
                    for (var test = 0; test < remaining.Count; test++)
                    {
                        var point = remaining[test];
                        if (point == previous || point == current || point == next) continue;
                        if (!PointInTriangle(polygon[point], polygon[previous],
                                polygon[current], polygon[next]))
                            continue;
                        containsVertex = true;
                        break;
                    }

                    if (containsVertex) continue;
                    result.Add(new Triangle { A = previous, B = current, C = next });
                    remaining.RemoveAt(cursor);
                    earFound = true;
                    break;
                }

                if (!earFound) break;
            }

            if (remaining.Count == 3)
            {
                result.Add(new Triangle
                {
                    A = remaining[0], B = remaining[1], C = remaining[2]
                });
            }
            return result;
        }

        private static void AddTriangleNodes(List<Node> nodes, IReadOnlyList<Triangle> triangles,
            IReadOnlyList<float2> polygon)
        {
            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                var point = (polygon[triangle.A] + polygon[triangle.B] + polygon[triangle.C]) / 3f;
                triangle.NodeIndex = nodes.Count;
                nodes.Add(new Node
                {
                    Position = point,
                    Clearance = DistanceToBoundary(point, polygon)
                });
            }
        }

        private static double CalculateSamplingSpacing(IReadOnlyList<float2> polygon, double area)
        {
            GetBounds(polygon, out var minX, out var maxX, out var minY, out var maxY);
            var spacing = Math.Max(16.0, Math.Min(38.0, Math.Sqrt(area) / 5.0));
            var estimated = (maxX - minX) * (maxY - minY) / (spacing * spacing);
            if (estimated > MaximumRoadmapNodes)
                spacing *= Math.Sqrt(estimated / MaximumRoadmapNodes);
            return spacing;
        }

        private static void AddInteriorSamples(List<Node> nodes, IReadOnlyList<float2> polygon,
            double spacing, int terminalCount, int seed)
        {
            GetBounds(polygon, out var minX, out var maxX, out var minY, out var maxY);
            var random = new System.Random(seed ^ 0x2d2816fe);
            var phaseX = spacing * (0.25 + random.NextDouble() * 0.5);
            var phaseY = spacing * (0.25 + random.NextDouble() * 0.5);
            var row = 0;
            for (var y = minY + phaseY; y < maxY; y += spacing)
            {
                var offset = (row++ & 1) == 0 ? 0.0 : spacing * 0.5;
                for (var x = minX + phaseX + offset; x < maxX; x += spacing)
                {
                    if (nodes.Count - terminalCount >= MaximumRoadmapNodes) return;
                    var jitterX = (random.NextDouble() * 2.0 - 1.0) * spacing * 0.16;
                    var jitterY = (random.NextDouble() * 2.0 - 1.0) * spacing * 0.16;
                    var point = new float2((float)(x + jitterX), (float)(y + jitterY));
                    if (!PointInPolygon(point, polygon)) continue;
                    var clearance = DistanceToBoundary(point, polygon);
                    if (clearance < BoundaryClearance) continue;

                    var tooClose = false;
                    for (var i = terminalCount; i < nodes.Count; i++)
                    {
                        if (Distance(point, nodes[i].Position) >= spacing * 0.42) continue;
                        tooClose = true;
                        break;
                    }
                    if (tooClose) continue;
                    nodes.Add(new Node { Position = point, Clearance = clearance });
                }
            }
        }

        private static void GetBounds(IReadOnlyList<float2> polygon, out double minX,
            out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue;
            maxX = double.MinValue;
            minY = double.MaxValue;
            maxY = double.MinValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                minX = Math.Min(minX, polygon[i].x);
                maxX = Math.Max(maxX, polygon[i].x);
                minY = Math.Min(minY, polygon[i].y);
                maxY = Math.Max(maxY, polygon[i].y);
            }
        }

        private static int[] AddGateApproachNodes(List<Node> nodes,
            IReadOnlyList<float2> polygon, int terminalCount, double spacing)
        {
            var result = new int[terminalCount];
            for (var terminal = 0; terminal < terminalCount; terminal++)
            {
                result[terminal] = -1;
                if (!TryGetInwardNormal(nodes[terminal].Position, polygon,
                        out var inwardNormal))
                    continue;

                var preferredDepth = Math.Max(8.0, Math.Min(14.0, spacing * 0.5));
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var depth = preferredDepth * (1.0 - attempt * 0.22);
                    var point = nodes[terminal].Position
                        + inwardNormal * (float)depth;
                    if (!PointInPolygon(point, polygon)) continue;
                    if (!SegmentInsidePolygon(nodes[terminal].Position, point,
                            polygon, true))
                        continue;

                    result[terminal] = nodes.Count;
                    nodes.Add(new Node
                    {
                        Position = point,
                        Clearance = DistanceToBoundary(point, polygon),
                        GateApproach = true
                    });
                    break;
                }
            }
            return result;
        }

        private static void ConnectTriangleDualGraph(double[,] graph, IReadOnlyList<Node> nodes,
            IReadOnlyList<Triangle> triangles, IReadOnlyList<float2> polygon, int seed)
        {
            var owners = new Dictionary<long, int>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var triangle = triangles[i];
                ConnectTriangleEdge(owners, triangle.A, triangle.B, triangle.NodeIndex,
                    graph, nodes, polygon, seed);
                ConnectTriangleEdge(owners, triangle.B, triangle.C, triangle.NodeIndex,
                    graph, nodes, polygon, seed);
                ConnectTriangleEdge(owners, triangle.C, triangle.A, triangle.NodeIndex,
                    graph, nodes, polygon, seed);
            }
        }

        private static void ConnectTriangleEdge(Dictionary<long, int> owners, int a, int b,
            int node, double[,] graph, IReadOnlyList<Node> nodes,
            IReadOnlyList<float2> polygon, int seed)
        {
            var key = EdgeKey(a, b);
            if (owners.TryGetValue(key, out var neighbour))
                Connect(graph, nodes, node, neighbour, polygon, false, seed);
            else
                owners[key] = node;
        }

        private static void ConnectInteriorRoadmap(double[,] graph, IReadOnlyList<Node> nodes,
            IReadOnlyList<float2> polygon, int terminalCount, double spacing, int seed)
        {
            var maximumLink = spacing * 2.35;
            for (var source = terminalCount; source < nodes.Count; source++)
            {
                var candidates = new List<Candidate>();
                for (var target = terminalCount; target < nodes.Count; target++)
                {
                    if (source == target || graph[source, target] > 0.0) continue;
                    var distance = Distance(nodes[source].Position, nodes[target].Position);
                    if (distance > maximumLink) continue;
                    candidates.Add(new Candidate { Index = target, Distance = distance });
                }
                candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                var connected = 0;
                for (var i = 0; i < candidates.Count && connected < 6; i++)
                {
                    var target = candidates[i].Index;
                    if (!SegmentInsidePolygon(nodes[source].Position, nodes[target].Position,
                            polygon, false))
                        continue;
                    Connect(graph, nodes, source, target, polygon, false, seed);
                    connected++;
                }
            }
        }

        private static void ConnectTerminals(double[,] graph, IReadOnlyList<Node> nodes,
            IReadOnlyList<float2> polygon, int terminalCount, IReadOnlyList<int> gateApproaches,
            int seed)
        {
            for (var terminal = 0; terminal < terminalCount; terminal++)
            {
                if (terminal < gateApproaches.Count && gateApproaches[terminal] >= 0)
                {
                    Connect(graph, nodes, terminal, gateApproaches[terminal], polygon,
                        true, seed);
                    continue;
                }

                if (!TryGetInwardNormal(nodes[terminal].Position, polygon,
                        out var inwardNormal))
                    continue;
                var candidates = new List<Candidate>();
                for (var target = terminalCount; target < nodes.Count; target++)
                {
                    if (!IsWithinGateCone(nodes[terminal].Position,
                            nodes[target].Position, inwardNormal))
                        continue;
                    var distance = Distance(nodes[terminal].Position, nodes[target].Position);
                    candidates.Add(new Candidate { Index = target, Distance = distance });
                }
                candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                var connected = 0;
                for (var i = 0; i < candidates.Count && connected < 5; i++)
                {
                    var target = candidates[i].Index;
                    if (!SegmentInsidePolygon(nodes[terminal].Position, nodes[target].Position,
                            polygon, true))
                        continue;
                    Connect(graph, nodes, terminal, target, polygon, true, seed);
                    connected++;
                }
            }
        }

        private static void Connect(double[,] graph, IReadOnlyList<Node> nodes, int a, int b,
            IReadOnlyList<float2> polygon, bool gateConnector, int seed)
        {
            var distance = Distance(nodes[a].Position, nodes[b].Position);
            if (distance <= 0.01) return;

            var cost = distance;
            if (!gateConnector)
            {
                var clearance = Math.Min(nodes[a].Clearance, nodes[b].Clearance);
                var denominator = PreferredBoundaryClearance - BoundaryClearance;
                var penalty = Math.Max(0.0,
                    (PreferredBoundaryClearance - clearance) / denominator);
                cost *= 1.0 + 2.0 * penalty * penalty;
            }
            cost *= EdgeVariation(seed, a, b, gateConnector ? 0.025 : 0.08);

            if (graph[a, b] <= 0.0 || cost < graph[a, b])
                graph[a, b] = graph[b, a] = cost;
        }

        private static void BuildTerminalMst(IReadOnlyList<Node> nodes, double[,] graph,
            int terminalCount, HashSet<int> networkNodes, HashSet<long> networkEdges,
            List<float2> output)
        {
            var connected = new HashSet<int> { 0 };
            while (connected.Count < terminalCount)
            {
                Path bestPath = null;
                var bestTerminal = -1;
                for (var terminal = 0; terminal < terminalCount; terminal++)
                {
                    if (connected.Contains(terminal)) continue;
                    var path = ShortestPath(terminal, connected, graph, null);
                    if (!path.Found || bestPath != null && path.Cost >= bestPath.Cost) continue;
                    bestPath = path;
                    bestTerminal = terminal;
                }
                if (bestPath == null) break;
                AddPath(bestPath, nodes, networkNodes, networkEdges, output);
                connected.Add(bestTerminal);
            }
        }

        private static int SelectCoverageAnchor(IReadOnlyList<Node> nodes, int terminalCount,
            HashSet<int> networkNodes, IReadOnlyList<float2> polygon, int seed)
        {
            var bestNode = -1;
            var bestScore = double.MinValue;
            for (var candidate = terminalCount; candidate < nodes.Count; candidate++)
            {
                if (networkNodes.Contains(candidate) || nodes[candidate].GateApproach) continue;
                var networkDistance = double.MaxValue;
                if (networkNodes.Count == 0)
                {
                    for (var terminal = 0; terminal < terminalCount; terminal++)
                        networkDistance = Math.Min(networkDistance,
                            Distance(nodes[candidate].Position, nodes[terminal].Position));
                }
                else
                {
                    foreach (var networkNode in networkNodes)
                        networkDistance = Math.Min(networkDistance,
                            Distance(nodes[candidate].Position, nodes[networkNode].Position));
                }

                var clearance = DistanceToBoundary(nodes[candidate].Position, polygon);
                var score = (networkDistance + Math.Min(clearance, 30.0) * 0.75)
                    * EdgeVariation(seed, candidate, networkNodes.Count + 7919, 0.08);
                if (score <= bestScore) continue;
                bestScore = score;
                bestNode = candidate;
            }
            return bestNode;
        }

        private static bool AddCoverageLoop(IReadOnlyList<Node> nodes, int terminalCount,
            double[,] graph, IReadOnlyList<float2> polygon, HashSet<int> networkNodes,
            HashSet<long> networkEdges, List<float2> output, int seed)
        {
            if (networkNodes.Count == 0) return false;
            var originalNetwork = new HashSet<int>(networkNodes);
            var anchor = SelectCoverageAnchor(nodes, terminalCount, originalNetwork, polygon, seed);
            if (anchor < 0) return false;

            var first = ShortestPath(anchor, originalNetwork, graph, null);
            if (!first.Found) return false;
            var blocked = new HashSet<long>();
            for (var i = 1; i < first.Nodes.Count; i++)
                blocked.Add(EdgeKey(first.Nodes[i - 1], first.Nodes[i]));

            var secondTargets = new HashSet<int>(originalNetwork);
            secondTargets.Remove(first.Nodes[first.Nodes.Count - 1]);
            if (secondTargets.Count == 0) return false;
            var second = ShortestPath(anchor, secondTargets, graph, blocked);
            if (!second.Found) return false;

            // Validate the complete loop before committing either branch. This
            // prevents a rejected return path from leaving a dangling spur.
            var stagedEdges = new HashSet<long>(networkEdges);
            if (!TryStagePath(first, nodes, stagedEdges)
                || !TryStagePath(second, nodes, stagedEdges))
                return false;

            AddPath(first, nodes, networkNodes, networkEdges, output);
            AddPath(second, nodes, networkNodes, networkEdges, output);
            return true;
        }

        private static void AddStretchLoop(IReadOnlyList<Node> nodes, double[,] graph,
            HashSet<int> networkNodes, HashSet<long> networkEdges, List<float2> output)
        {
            var bestA = -1;
            var bestB = -1;
            var bestStretch = 1.8;
            foreach (var a in networkNodes)
            foreach (var b in networkNodes)
            {
                if (b <= a || graph[a, b] <= 0.0 || networkEdges.Contains(EdgeKey(a, b)))
                    continue;
                var route = NetworkDistance(a, b, nodes, networkEdges);
                if (route >= double.MaxValue) continue;
                var direct = Distance(nodes[a].Position, nodes[b].Position);
                if (direct <= 0.01) continue;
                var stretch = route / direct;
                if (stretch <= bestStretch) continue;
                bestStretch = stretch;
                bestA = a;
                bestB = b;
            }

            if (bestA < 0) return;
            var path = new Path { Cost = graph[bestA, bestB] };
            path.Nodes.Add(bestA);
            path.Nodes.Add(bestB);
            var stagedEdges = new HashSet<long>(networkEdges);
            if (!TryStagePath(path, nodes, stagedEdges)) return;
            AddPath(path, nodes, networkNodes, networkEdges, output);
        }

        private static bool TryStagePath(Path path, IReadOnlyList<Node> nodes,
            HashSet<long> stagedEdges)
        {
            if (path == null || !path.Found) return false;
            for (var i = 1; i < path.Nodes.Count; i++)
            {
                var a = path.Nodes[i - 1];
                var b = path.Nodes[i];
                var key = EdgeKey(a, b);
                if (stagedEdges.Contains(key)) continue;
                if (!HasEnoughSeparation(a, b, nodes, stagedEdges)) return false;
                stagedEdges.Add(key);
            }
            return true;
        }

        private static bool HasEnoughSeparation(int a, int b,
            IReadOnlyList<Node> nodes, HashSet<long> existingEdges)
        {
            foreach (var edge in existingEdges)
            {
                var c = (int)(edge >> 32);
                var d = (int)(edge & 0xffffffffL);
                if (EdgeKey(a, b) == edge) continue;

                var shared = a == c || a == d ? a : b == c || b == d ? b : -1;
                if (shared >= 0)
                {
                    var newOther = shared == a ? b : a;
                    var oldOther = shared == c ? d : c;
                    if (HasAcuteBranch(nodes[shared].Position,
                            nodes[newOther].Position, nodes[oldOther].Position))
                        return false;
                    continue;
                }

                if (SegmentDistance(nodes[a].Position, nodes[b].Position,
                        nodes[c].Position, nodes[d].Position) < MinimumPathSeparation)
                    return false;
            }
            return true;
        }

        private static bool HasAcuteBranch(float2 center, float2 a, float2 b)
        {
            var first = a - center;
            var second = b - center;
            var firstLength = Math.Sqrt((double)first.x * first.x
                + (double)first.y * first.y);
            var secondLength = Math.Sqrt((double)second.x * second.x
                + (double)second.y * second.y);
            if (firstLength <= 0.0001 || secondLength <= 0.0001) return true;
            var cosine = (first.x * second.x + first.y * second.y)
                / (firstLength * secondLength);
            return cosine > MaximumAcuteBranchCosine;
        }

        private static double SegmentDistance(float2 a, float2 b, float2 c, float2 d)
        {
            if (ProperSegmentsIntersect(a, b, c, d)) return 0.0;
            return Math.Min(
                Math.Min(DistancePointToSegment(a, c, d),
                    DistancePointToSegment(b, c, d)),
                Math.Min(DistancePointToSegment(c, a, b),
                    DistancePointToSegment(d, a, b)));
        }

        private static Path ShortestPath(int source, HashSet<int> targets,
            double[,] graph, HashSet<long> blockedEdges)
        {
            var result = new Path();
            if (targets == null || targets.Count == 0) return result;
            var count = graph.GetLength(0);
            var distance = new double[count];
            var previous = new int[count];
            var visited = new bool[count];
            for (var i = 0; i < count; i++)
            {
                distance[i] = double.MaxValue;
                previous[i] = -1;
            }
            distance[source] = 0.0;
            var destination = -1;

            for (var step = 0; step < count; step++)
            {
                var current = -1;
                var best = double.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    if (visited[i] || distance[i] >= best) continue;
                    current = i;
                    best = distance[i];
                }
                if (current < 0) break;
                visited[current] = true;
                if (current != source && targets.Contains(current))
                {
                    destination = current;
                    break;
                }

                for (var next = 0; next < count; next++)
                {
                    var weight = graph[current, next];
                    if (weight <= 0.0 || visited[next]
                        || blockedEdges != null && blockedEdges.Contains(EdgeKey(current, next)))
                        continue;
                    var candidate = best + weight;
                    if (candidate >= distance[next]) continue;
                    distance[next] = candidate;
                    previous[next] = current;
                }
            }

            if (destination < 0) return result;
            result.Cost = distance[destination];
            for (var cursor = destination; cursor >= 0; cursor = previous[cursor])
            {
                result.Nodes.Add(cursor);
                if (cursor == source) break;
            }
            result.Nodes.Reverse();
            return result;
        }

        private static void AddPath(Path path, IReadOnlyList<Node> nodes,
            HashSet<int> networkNodes, HashSet<long> networkEdges, List<float2> output)
        {
            if (path == null || !path.Found) return;
            for (var i = 1; i < path.Nodes.Count; i++)
            {
                var a = path.Nodes[i - 1];
                var b = path.Nodes[i];
                if (networkEdges.Add(EdgeKey(a, b)))
                {
                    output.Add(nodes[a].Position);
                    output.Add(nodes[b].Position);
                }
                networkNodes.Add(a);
                networkNodes.Add(b);
            }
        }

        private static double NetworkDistance(int source, int target,
            IReadOnlyList<Node> nodes, HashSet<long> edges)
        {
            var graph = new double[nodes.Count, nodes.Count];
            foreach (var edge in edges)
            {
                var a = (int)(edge >> 32);
                var b = (int)(edge & 0xffffffffL);
                graph[a, b] = graph[b, a] = Distance(nodes[a].Position, nodes[b].Position);
            }
            return ShortestPath(source, new HashSet<int> { target }, graph, null).Cost;
        }

        private static bool SegmentInsidePolygon(float2 a, float2 b,
            IReadOnlyList<float2> polygon, bool allowBoundaryStart)
        {
            for (var sample = 1; sample < 8; sample++)
            {
                var point = a + (b - a) * (sample / 8f);
                if (!PointInPolygon(point, polygon)) return false;
            }

            for (var i = 0; i < polygon.Count; i++)
            {
                var c = polygon[i];
                var d = polygon[(i + 1) % polygon.Count];
                if (!ProperSegmentsIntersect(a, b, c, d)) continue;
                if (allowBoundaryStart && DistancePointToSegment(a, c, d) < 0.05)
                    continue;
                return false;
            }
            return true;
        }

        private static bool TryGetInwardNormal(float2 gate,
            IReadOnlyList<float2> polygon, out float2 inwardNormal)
        {
            inwardNormal = default;
            var bestDistance = double.MaxValue;
            var bestEdge = -1;
            for (var i = 0; i < polygon.Count; i++)
            {
                var distance = DistancePointToSegment(gate, polygon[i],
                    polygon[(i + 1) % polygon.Count]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestEdge = i;
            }
            if (bestEdge < 0) return false;

            var a = polygon[bestEdge];
            var b = polygon[(bestEdge + 1) % polygon.Count];
            var dx = b.x - a.x;
            var dy = b.y - a.y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (length <= 0.0001) return false;

            // NormalizePolygon guarantees counter-clockwise winding, so the
            // polygon interior is on the left side of every boundary edge.
            inwardNormal = new float2((float)(-dy / length), (float)(dx / length));
            return true;
        }

        private static bool IsWithinGateCone(float2 gate, float2 target,
            float2 inwardNormal)
        {
            var direction = target - gate;
            var length = Math.Sqrt((double)direction.x * direction.x
                + (double)direction.y * direction.y);
            if (length <= 0.0001) return false;
            var cosine = (direction.x * inwardNormal.x + direction.y * inwardNormal.y)
                / length;
            return cosine >= MinimumGateDirectionCosine;
        }

        private static bool PointInPolygon(float2 point, IReadOnlyList<float2> polygon)
        {
            var inside = false;
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                if (DistancePointToSegment(point, a, b) < 0.02) return true;
                var crosses = (a.y > point.y) != (b.y > point.y);
                if (!crosses) continue;
                var intersection = (double)(b.x - a.x) * (point.y - a.y)
                    / (b.y - a.y) + a.x;
                if (point.x < intersection) inside = !inside;
            }
            return inside;
        }

        private static bool PointInTriangle(float2 point, float2 a, float2 b, float2 c)
        {
            var ab = Cross(a, b, point);
            var bc = Cross(b, c, point);
            var ca = Cross(c, a, point);
            const double epsilon = 0.0001;
            return ab >= -epsilon && bc >= -epsilon && ca >= -epsilon;
        }

        private static bool ProperSegmentsIntersect(float2 a, float2 b, float2 c, float2 d)
        {
            var abC = Cross(a, b, c);
            var abD = Cross(a, b, d);
            var cdA = Cross(c, d, a);
            var cdB = Cross(c, d, b);
            const double epsilon = 0.0001;
            return ((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
                && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon));
        }

        private static double DistanceToBoundary(float2 point, IReadOnlyList<float2> polygon)
        {
            var best = double.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
                best = Math.Min(best, DistancePointToSegment(point, polygon[i],
                    polygon[(i + 1) % polygon.Count]));
            return best;
        }

        private static double DistancePointToSegment(float2 point, float2 a, float2 b)
        {
            var dx = (double)b.x - a.x;
            var dy = (double)b.y - a.y;
            var lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= 0.0000001) return Distance(point, a);
            var t = (((double)point.x - a.x) * dx + ((double)point.y - a.y) * dy)
                / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            var px = a.x + dx * t;
            var py = a.y + dy * t;
            var x = point.x - px;
            var y = point.y - py;
            return Math.Sqrt(x * x + y * y);
        }

        private static double Distance(float2 a, float2 b)
        {
            var x = (double)a.x - b.x;
            var y = (double)a.y - b.y;
            return Math.Sqrt(x * x + y * y);
        }

        private static double Cross(float2 a, float2 b, float2 c)
            => ((double)b.x - a.x) * ((double)c.y - a.y)
                - ((double)b.y - a.y) * ((double)c.x - a.x);

        private static double SignedArea(IReadOnlyList<float2> polygon)
        {
            var area = 0.0;
            for (var i = 0; i < polygon.Count; i++)
            {
                var next = (i + 1) % polygon.Count;
                area += (double)polygon[i].x * polygon[next].y
                    - (double)polygon[next].x * polygon[i].y;
            }
            return area * 0.5;
        }

        private static long EdgeKey(int a, int b)
        {
            var low = Math.Min(a, b);
            var high = Math.Max(a, b);
            return ((long)low << 32) | (uint)high;
        }

        private static int PositiveModulo(int value, int modulus)
        {
            var result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static double EdgeVariation(int seed, int a, int b, double amount)
        {
            var low = Math.Min(a, b);
            var high = Math.Max(a, b);
            var hash = unchecked((uint)seed);
            hash ^= unchecked((uint)low * 0x9e3779b9u);
            hash ^= unchecked((uint)high * 0x85ebca6bu);
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            hash ^= hash >> 16;
            var unit = (hash & 0x00ffffffu) / 16777215.0;
            return 1.0 + (unit * 2.0 - 1.0) * amount;
        }
    }
}
