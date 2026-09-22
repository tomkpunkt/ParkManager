using System;
using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Diagnostic snapshots for the three path-build stages: generated plan,
    /// temporary tool entities, and the permanent Vanilla network after Apply.
    /// The output intentionally uses a stable PATH-DIAG prefix so a faulty
    /// build can be isolated from a complete Player.log with one search.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private const string PathDiagnosticPrefix = "ParkManager PATH-DIAG";
        private const float SuspiciousPathLength = 2.0f;

        private void LogPlannedPathDiagnostics()
        {
            if (_pathPlan == null) return;

            var degrees = new int[_pathPlan.Nodes.Count];
            var minimum = float.MaxValue;
            var maximum = 0f;
            var total = 0f;
            var shortEdges = 0;
            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                degrees[edge.A]++;
                degrees[edge.B]++;
                var length = math.distance(_pathPlan.Nodes[edge.A].Position,
                    _pathPlan.Nodes[edge.B].Position);
                minimum = math.min(minimum, length);
                maximum = math.max(maximum, length);
                total += length;
                if (length < SuspiciousPathLength) shortEdges++;
            }

            var degree0 = 0;
            var degree1 = 0;
            var degree2 = 0;
            var degree3Plus = 0;
            for (var i = 0; i < degrees.Length; i++)
            {
                if (degrees[i] == 0) degree0++;
                else if (degrees[i] == 1) degree1++;
                else if (degrees[i] == 2) degree2++;
                else degree3Plus++;
            }

            var average = _pathPlan.Edges.Count == 0
                ? 0f : total / _pathPlan.Edges.Count;
            if (minimum == float.MaxValue) minimum = 0f;
            Mod.Log.Info($"{PathDiagnosticPrefix} PLAN seed={_pathPlan.Seed} "
                + $"prefab='{_selectedPathPrefabName}' nodes={_pathPlan.Nodes.Count} "
                + $"edges={_pathPlan.Edges.Count} degree[0/1/2/3+]="
                + $"{degree0}/{degree1}/{degree2}/{degree3Plus} "
                + $"length[min/avg/max]={minimum:F2}/{average:F2}/{maximum:F2}m "
                + $"short<{SuspiciousPathLength:F1}m={shortEdges}.");

            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                var a = _pathPlan.Nodes[edge.A];
                var b = _pathPlan.Nodes[edge.B];
                var length = math.distance(a.Position, b.Position);
                Mod.Log.Info($"{PathDiagnosticPrefix} PLAN-EDGE id={edge.Id} "
                    + $"nodes={edge.A}->{edge.B} kinds={a.Kind}/{b.Kind} "
                    + $"degree={degrees[edge.A]}/{degrees[edge.B]} "
                    + $"length={length:F2}m requestedWidth={edge.Width:F2}m "
                    + $"class={edge.Kind}.");
            }
        }

        private void LogTemporaryPathDiagnostics(Entity prefab)
        {
            var edges = 0;
            var nodes = 0;
            var curves = 0;
            var edgeGeometry = 0;
            var compositions = 0;
            var hidden = 0;
            var overridden = 0;
            var missingGeometry = 0;

            using var entities = _tempPathQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != prefab) continue;
                if (EntityManager.HasComponent<Game.Net.Edge>(entity)) edges++;
                else if (EntityManager.HasComponent<Game.Net.Node>(entity)) nodes++;
                if (EntityManager.HasComponent<Game.Net.Curve>(entity)) curves++;
                if (EntityManager.HasComponent<EdgeGeometry>(entity)) edgeGeometry++;
                if (EntityManager.HasComponent<Composition>(entity)) compositions++;
                if (EntityManager.HasComponent<Hidden>(entity)) hidden++;
                if (EntityManager.HasComponent<Overridden>(entity)) overridden++;
                if (EntityManager.HasComponent<Game.Net.Edge>(entity)
                    && (!EntityManager.HasComponent<Game.Net.Curve>(entity)
                        || !EntityManager.HasComponent<EdgeGeometry>(entity)
                        || !EntityManager.HasComponent<Composition>(entity)))
                    missingGeometry++;
            }

            Mod.Log.Info($"{PathDiagnosticPrefix} TEMP prefab='{_selectedPathPrefabName}' "
                + $"edges={edges} nodes={nodes} curves={curves} "
                + $"edgeGeometry={edgeGeometry} compositions={compositions} "
                + $"hidden={hidden} overridden={overridden} "
                + $"edgesMissingGeometry={missingGeometry}.");
        }

        private void LogPermanentPathDiagnostics(Entity park)
        {
            if (park == Entity.Null || !EntityManager.Exists(park)) return;

            var ownedEdges = new HashSet<Entity>();
            var ownedNodes = new HashSet<Entity>();
            using (var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                    if (member.Park != park) continue;
                    if (member.Kind == ParkPathMemberKind.Edge) ownedEdges.Add(entity);
                    else if (member.Kind == ParkPathMemberKind.Node) ownedNodes.Add(entity);
                }
            }

            var missingCurve = 0;
            var missingEdgeGeometry = 0;
            var missingComposition = 0;
            var hiddenEdges = 0;
            var overriddenEdges = 0;
            var shortEdges = 0;
            var minLength = float.MaxValue;
            var maxLength = 0f;
            var totalLength = 0f;
            foreach (var edgeEntity in ownedEdges)
            {
                var hasCurve = EntityManager.HasComponent<Game.Net.Curve>(edgeEntity);
                var hasGeometry = EntityManager.HasComponent<EdgeGeometry>(edgeEntity);
                var hasComposition = EntityManager.HasComponent<Composition>(edgeEntity);
                if (!hasCurve) missingCurve++;
                if (!hasGeometry) missingEdgeGeometry++;
                if (!hasComposition) missingComposition++;
                if (EntityManager.HasComponent<Hidden>(edgeEntity)) hiddenEdges++;
                if (EntityManager.HasComponent<Overridden>(edgeEntity)) overriddenEdges++;

                var length = hasCurve
                    ? EntityManager.GetComponentData<Game.Net.Curve>(edgeEntity).m_Length
                    : -1f;
                if (length >= 0f)
                {
                    minLength = math.min(minLength, length);
                    maxLength = math.max(maxLength, length);
                    totalLength += length;
                    if (length < SuspiciousPathLength) shortEdges++;
                }
            }

            var degree0 = 0;
            var degree1 = 0;
            var degree2 = 0;
            var degree3Plus = 0;
            foreach (var node in ownedNodes)
            {
                var degree = NodeDegree(node);
                if (degree == 0) degree0++;
                else if (degree == 1) degree1++;
                else if (degree == 2) degree2++;
                else degree3Plus++;
            }

            if (minLength == float.MaxValue) minLength = 0f;
            var averageLength = ownedEdges.Count == 0
                ? 0f : totalLength / ownedEdges.Count;
            var summary = $"{PathDiagnosticPrefix} PERMANENT park={park.Index}:"
                + $"{park.Version} edges={ownedEdges.Count} nodes={ownedNodes.Count} "
                + $"degree[0/1/2/3+]={degree0}/{degree1}/{degree2}/{degree3Plus} "
                + $"length[min/avg/max]={minLength:F2}/{averageLength:F2}/"
                + $"{maxLength:F2}m short<{SuspiciousPathLength:F1}m={shortEdges} "
                + $"missing[curve/edgeGeometry/composition]={missingCurve}/"
                + $"{missingEdgeGeometry}/{missingComposition} hidden={hiddenEdges} "
                + $"overridden={overriddenEdges}.";
            if (missingCurve > 0 || missingEdgeGeometry > 0
                || missingComposition > 0 || hiddenEdges > 0
                || overriddenEdges > 0 || shortEdges > 0)
                Mod.Log.Warn(summary);
            else
                Mod.Log.Info(summary);

            foreach (var edgeEntity in ownedEdges)
                LogPermanentEdge(edgeEntity, ownedEdges, ownedNodes);
            foreach (var nodeEntity in ownedNodes)
                LogPermanentNode(nodeEntity, ownedEdges);
        }

        private void LogPermanentEdge(Entity entity, HashSet<Entity> ownedEdges,
            HashSet<Entity> ownedNodes)
        {
            if (!EntityManager.HasComponent<Game.Net.Edge>(entity))
            {
                Mod.Log.Warn($"{PathDiagnosticPrefix} EDGE {EntityLabel(entity)} "
                    + "member is missing Game.Net.Edge.");
                return;
            }

            var edge = EntityManager.GetComponentData<Game.Net.Edge>(entity);
            var curve = EntityManager.HasComponent<Game.Net.Curve>(entity)
                ? EntityManager.GetComponentData<Game.Net.Curve>(entity)
                : default;
            var hasCurve = EntityManager.HasComponent<Game.Net.Curve>(entity);
            var length = hasCurve ? curve.m_Length : -1f;
            var chord = hasCurve ? math.distance(curve.m_Bezier.a, curve.m_Bezier.d) : -1f;
            var composition = EntityManager.HasComponent<Composition>(entity)
                ? EntityManager.GetComponentData<Composition>(entity)
                : default;
            var compositionDescription = EntityManager.HasComponent<Composition>(entity)
                ? "edge=" + DescribeCompositionEntity(composition.m_Edge)
                    + ",start=" + DescribeCompositionEntity(composition.m_StartNode)
                    + ",end=" + DescribeCompositionEntity(composition.m_EndNode)
                : "missing";
            Mod.Log.Info($"{PathDiagnosticPrefix} EDGE {EntityLabel(entity)} "
                + $"ends={EntityLabel(edge.m_Start)}->{EntityLabel(edge.m_End)} "
                + $"ownedEnds={ownedNodes.Contains(edge.m_Start)}/"
                + $"{ownedNodes.Contains(edge.m_End)} degree={NodeDegree(edge.m_Start)}/"
                + $"{NodeDegree(edge.m_End)} length={length:F2}m chord={chord:F2}m "
                + $"curve={hasCurve} edgeGeometry="
                + $"{EntityManager.HasComponent<EdgeGeometry>(entity)} "
                + $"startGeometry={EntityManager.HasComponent<StartNodeGeometry>(entity)} "
                + $"endGeometry={EntityManager.HasComponent<EndNodeGeometry>(entity)} "
                + $"hidden={EntityManager.HasComponent<Hidden>(entity)} "
                + $"overridden={EntityManager.HasComponent<Overridden>(entity)} "
                + $"composition={compositionDescription}.");
        }

        private void LogPermanentNode(Entity entity, HashSet<Entity> ownedEdges)
        {
            var position = EntityManager.HasComponent<Game.Net.Node>(entity)
                ? EntityManager.GetComponentData<Game.Net.Node>(entity).m_Position
                : new float3(float.NaN);
            Mod.Log.Info($"{PathDiagnosticPrefix} NODE {EntityLabel(entity)} "
                + $"position=({position.x:F2},{position.y:F2},{position.z:F2}) "
                + $"degree={NodeDegree(entity)} ownDegree="
                + $"{OwnedNodeDegree(entity, ownedEdges)} "
                + $"hidden={EntityManager.HasComponent<Hidden>(entity)} "
                + $"overridden={EntityManager.HasComponent<Overridden>(entity)}.");
        }

        private int NodeDegree(Entity node)
            => node != Entity.Null && EntityManager.Exists(node)
               && EntityManager.HasBuffer<ConnectedEdge>(node)
                ? EntityManager.GetBuffer<ConnectedEdge>(node, true).Length : -1;

        private int OwnedNodeDegree(Entity node, HashSet<Entity> ownedEdges)
        {
            if (node == Entity.Null || !EntityManager.Exists(node)
                || !EntityManager.HasBuffer<ConnectedEdge>(node)) return -1;
            var result = 0;
            var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (var i = 0; i < connected.Length; i++)
                if (ownedEdges.Contains(connected[i].m_Edge)) result++;
            return result;
        }

        private string DescribeCompositionEntity(Entity composition)
        {
            if (composition == Entity.Null) return "missing";
            if (!EntityManager.Exists(composition)
                || !EntityManager.HasComponent<NetCompositionData>(composition))
                return EntityLabel(composition) + "/no-data";
            var data = EntityManager.GetComponentData<NetCompositionData>(
                composition);
            return EntityLabel(composition) + $"/width={data.m_Width:F2}m/"
                + $"flags={data.m_Flags.m_General}";
        }

        private static string EntityLabel(Entity entity)
            => entity == Entity.Null ? "null" : $"{entity.Index}:{entity.Version}";
    }
}
