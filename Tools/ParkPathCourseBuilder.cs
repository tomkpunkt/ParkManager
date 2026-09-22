using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using ParkManager.Geometry;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Converts the planner's editable polyline graph into the smaller set of
    /// cubic courses consumed by CS2. Degree-two vertices describe the shape
    /// of a path; they are not automatically network junctions. Keeping them
    /// as individual courses makes the wide park-path prefab render a round
    /// node mesh at every sampling point.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private const float MaximumCourseFitError = 1.5f;
        private const int MaximumCourseFitDepth = 8;

        private sealed class MaterializedPathCourse
        {
            internal Bezier4x3 Curve;
            internal float Length;
            internal int SourcePointCount;
        }

        private List<MaterializedPathCourse> BuildMaterializedPathCourses(
            ref TerrainHeightData heightData, Dictionary<int, float> heights,
            out int chainCount)
        {
            var result = new List<MaterializedPathCourse>();
            var chains = ExtractPathChains();
            chainCount = chains.Count;
            for (var chainIndex = 0; chainIndex < chains.Count; chainIndex++)
            {
                var chain = chains[chainIndex];
                if (chain.Count < 2) continue;
                var points = new List<float3>(chain.Count);
                for (var i = 0; i < chain.Count; i++)
                {
                    var node = chain[i];
                    points.Add(WorldPathPoint(node, _pathPlan.Nodes[node].Position,
                        ref heightData, heights));
                }

                // A lobe may leave and return to the same junction. CS2 does
                // not accept a useful self-edge, so retain one real midpoint
                // node while still collapsing both halves of the lobe.
                if (chain[0] == chain[chain.Count - 1] && chain.Count > 3)
                {
                    var split = chain.Count / 2;
                    AddFittedPathCourses(points.GetRange(0, split + 1), result, 0);
                    AddFittedPathCourses(points.GetRange(split,
                        points.Count - split), result, 0);
                }
                else AddFittedPathCourses(points, result, 0);
            }
            return result;
        }

        private List<List<int>> ExtractPathChains()
        {
            var output = new List<List<int>>();
            if (_pathPlan == null || _pathPlan.Nodes.Count == 0
                || _pathPlan.Edges.Count == 0) return output;

            var adjacency = new List<int>[_pathPlan.Nodes.Count];
            for (var i = 0; i < adjacency.Length; i++)
                adjacency[i] = new List<int>();
            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                adjacency[_pathPlan.Edges[i].A].Add(i);
                adjacency[_pathPlan.Edges[i].B].Add(i);
            }

            var anchors = new bool[_pathPlan.Nodes.Count];
            for (var i = 0; i < anchors.Length; i++)
                anchors[i] = adjacency[i].Count != 2
                    || _pathPlan.Nodes[i].Kind == ParkPathNodeKind.Gate;
            EnsureTwoAnchorsPerComponent(adjacency, anchors);

            var visitedEdges = new bool[_pathPlan.Edges.Count];
            for (var start = 0; start < anchors.Length; start++)
            {
                if (!anchors[start]) continue;
                for (var incidentIndex = 0;
                     incidentIndex < adjacency[start].Count; incidentIndex++)
                {
                    var firstEdge = adjacency[start][incidentIndex];
                    if (visitedEdges[firstEdge]) continue;
                    output.Add(TracePathChain(start, firstEdge, adjacency,
                        anchors, visitedEdges));
                }
            }

            // Defensive fallback for malformed or exotic graph components.
            // Every planner edge must still be materialized exactly once.
            for (var edgeIndex = 0; edgeIndex < visitedEdges.Length; edgeIndex++)
            {
                if (visitedEdges[edgeIndex]) continue;
                var edge = _pathPlan.Edges[edgeIndex];
                visitedEdges[edgeIndex] = true;
                output.Add(new List<int> { edge.A, edge.B });
            }
            return output;
        }

        private List<int> TracePathChain(int startNode, int firstEdge,
            IReadOnlyList<List<int>> adjacency, IReadOnlyList<bool> anchors,
            bool[] visitedEdges)
        {
            var chain = new List<int> { startNode };
            var node = startNode;
            var edgeIndex = firstEdge;
            var guard = _pathPlan.Edges.Count + 1;
            while (guard-- > 0 && !visitedEdges[edgeIndex])
            {
                visitedEdges[edgeIndex] = true;
                var edge = _pathPlan.Edges[edgeIndex];
                var next = edge.A == node ? edge.B : edge.A;
                chain.Add(next);
                if (anchors[next]) break;

                var incident = adjacency[next];
                var candidate = incident[0] == edgeIndex
                    ? incident[1] : incident[0];
                node = next;
                edgeIndex = candidate;
            }
            return chain;
        }

        private void EnsureTwoAnchorsPerComponent(
            IReadOnlyList<List<int>> adjacency, bool[] anchors)
        {
            var seen = new bool[adjacency.Count];
            for (var start = 0; start < adjacency.Count; start++)
            {
                if (seen[start] || adjacency[start].Count == 0) continue;
                var component = new List<int>();
                var stack = new Stack<int>();
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    component.Add(node);
                    for (var i = 0; i < adjacency[node].Count; i++)
                    {
                        var edge = _pathPlan.Edges[adjacency[node][i]];
                        var other = edge.A == node ? edge.B : edge.A;
                        if (seen[other]) continue;
                        seen[other] = true;
                        stack.Push(other);
                    }
                }

                var anchorCount = 0;
                var reference = component[0];
                for (var i = 0; i < component.Count; i++)
                    if (anchors[component[i]])
                    {
                        anchorCount++;
                        reference = component[i];
                    }
                if (anchorCount >= 2) continue;
                if (anchorCount == 0)
                {
                    anchors[reference] = true;
                    anchorCount = 1;
                }

                var farthest = reference;
                var farthestDistance = -1f;
                var origin = _pathPlan.Nodes[reference].Position;
                for (var i = 0; i < component.Count; i++)
                {
                    var candidate = component[i];
                    if (candidate == reference) continue;
                    var distance = math.distancesq(origin,
                        _pathPlan.Nodes[candidate].Position);
                    if (distance <= farthestDistance) continue;
                    farthestDistance = distance;
                    farthest = candidate;
                }
                anchors[farthest] = true;
            }
        }

        private void AddFittedPathCourses(List<float3> points,
            ICollection<MaterializedPathCourse> output, int depth)
        {
            if (points.Count < 2) return;
            var curve = FitPathCourse(points);
            var splitIndex = FindWorstFitPoint(points, curve, out var maximumError);
            if (points.Count <= 2
                || depth >= MaximumCourseFitDepth
                || maximumError <= MaximumCourseFitError
                    && CurveStaysInsidePark(curve))
            {
                output.Add(new MaterializedPathCourse
                {
                    Curve = curve,
                    Length = ApproximateCurveLength(curve),
                    SourcePointCount = points.Count,
                });
                return;
            }

            if (splitIndex <= 0 || splitIndex >= points.Count - 1)
                splitIndex = points.Count / 2;
            AddFittedPathCourses(points.GetRange(0, splitIndex + 1),
                output, depth + 1);
            AddFittedPathCourses(points.GetRange(splitIndex,
                points.Count - splitIndex), output, depth + 1);
        }

        private static Bezier4x3 FitPathCourse(IReadOnlyList<float3> points)
        {
            var start = points[0];
            var end = points[points.Count - 1];
            if (points.Count == 2) return NetUtils.StraightCurve(start, end);

            var polylineLength = 0f;
            for (var i = 1; i < points.Count; i++)
                polylineLength += math.distance(points[i - 1], points[i]);
            var chord = math.distance(start, end);
            var handle = math.min(polylineLength / 3f, chord * 0.55f);
            var startDirection = math.normalizesafe(points[1] - start,
                math.normalizesafe(end - start));
            var endDirection = math.normalizesafe(end - points[points.Count - 2],
                math.normalizesafe(end - start));
            return new Bezier4x3(start, start + startDirection * handle,
                end - endDirection * handle, end);
        }

        private static int FindWorstFitPoint(IReadOnlyList<float3> points,
            Bezier4x3 curve, out float maximumError)
        {
            maximumError = 0f;
            var worst = points.Count / 2;
            for (var i = 1; i + 1 < points.Count; i++)
            {
                var best = float.MaxValue;
                for (var sample = 0; sample <= 32; sample++)
                {
                    var curvePoint = MathUtils.Position(curve, sample / 32f);
                    best = math.min(best, math.distance(points[i], curvePoint));
                }
                if (best <= maximumError) continue;
                maximumError = best;
                worst = i;
            }
            return worst;
        }

        private bool CurveStaysInsidePark(Bezier4x3 curve)
        {
            if (_points == null || _points.Count < 3) return true;
            for (var sample = 0; sample <= 32; sample++)
            {
                var point = MathUtils.Position(curve, sample / 32f).xz;
                if (!PointInsideOrNearPark(point)) return false;
            }
            return true;
        }

        private bool PointInsideOrNearPark(float2 point)
        {
            var inside = false;
            for (int i = 0, j = _points.Count - 1; i < _points.Count; j = i++)
            {
                var a = _points[j];
                var b = _points[i];
                var ab = b - a;
                var length = math.lengthsq(ab);
                var t = length < 0.0001f ? 0f
                    : math.clamp(math.dot(point - a, ab) / length, 0f, 1f);
                if (math.distancesq(point, a + ab * t) <= 0.25f) return true;
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                       / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static float ApproximateCurveLength(Bezier4x3 curve)
        {
            var length = 0f;
            var previous = curve.a;
            for (var sample = 1; sample <= 32; sample++)
            {
                var point = MathUtils.Position(curve, sample / 32f);
                length += math.distance(previous, point);
                previous = point;
            }
            return length;
        }

        private void LogMaterializedCourseDiagnostics(
            IReadOnlyList<MaterializedPathCourse> courses, int chainCount)
        {
            var curved = 0;
            var maximumSourcePoints = 0;
            for (var i = 0; i < courses.Count; i++)
            {
                if (courses[i].SourcePointCount > 2) curved++;
                maximumSourcePoints = Math.Max(maximumSourcePoints,
                    courses[i].SourcePointCount);
            }
            Mod.Log.Info($"ParkManager PATH-DIAG COURSES planEdges="
                + $"{_pathPlan?.Edges.Count ?? 0} chains={chainCount} "
                + $"materializedCourses={courses.Count} curved={curved} "
                + $"removedIntermediateNodes="
                + $"{Math.Max(0, (_pathPlan?.Edges.Count ?? 0) - courses.Count)} "
                + $"maxSourcePoints={maximumSourcePoints} "
                + $"fitTolerance={MaximumCourseFitError:F2}m.");
        }
    }
}
