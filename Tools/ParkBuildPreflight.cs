using System;
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using ParkManager.Geometry;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>Read-only checks before any CS2 creation definition is emitted.</summary>
    public sealed partial class ParkToolSystem
    {
        private const float PreflightStep = 4f;
        private const float MaximumPathGrade = 0.22f;
        private const float MaximumSurfaceGrade = 0.35f;
        private const float ObstacleClearance = 2f;
        private const float GateRoadExemption = 7f;
        private readonly List<float3> _buildIssues = new List<float3>();
        private string _preflightWarning;

        private bool ValidateBuildSite(out string reason)
        {
            _buildIssues.Clear();
            _preflightWarning = null;
            reason = null;
            var terrain = _terrainSystem.GetHeightData(waitForPending: true);
            var netTree = _netSearchSystem.GetNetSearchTree(true, out var netDeps);
            netDeps.Complete();
            var objectTree = _objectSearchSystem.GetStaticSearchTree(true,
                out var objectDeps);
            objectDeps.Complete();
            using var nets = new NativeList<Entity>(32, Allocator.Temp);
            using var objects = new NativeList<Entity>(32, Allocator.Temp);
            var seenNets = new HashSet<Entity>();
            var seenObjects = new HashSet<Entity>();

            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                var a = _pathPlan.Nodes[edge.A].Position;
                var b = _pathPlan.Nodes[edge.B].Position;
                var length = math.distance(a, b);
                var steps = math.max(1, (int)math.ceil(length / PreflightStep));
                var previousHeight = float.NaN;
                for (var step = 0; step <= steps; step++)
                {
                    var point = math.lerp(a, b, (float)step / steps);
                    var world = new float3(point.x, 0f, point.y);
                    world.y = TerrainUtils.SampleHeight(ref terrain, world);
                    if (!math.isfinite(world.y))
                        return RejectBuildSite(world, "Terrainhöhe nicht verfügbar", out reason);
                    if (step > 0 && math.abs(world.y - previousHeight)
                        / math.max(0.1f, length / steps) > MaximumPathGrade)
                        return NoteBuildWarning(world, "Weg stark geneigt", out reason);
                    previousHeight = world.y;

                    var radius = math.max(ObstacleClearance, edge.Width * 0.5f);
                    var bounds = new Bounds2(point - radius, point + radius);
                    seenNets.Clear();
                    nets.Clear();
                    var netIterator = new SnapEntityIterator
                    {
                        Bounds = bounds,
                        Results = nets,
                    };
                    netTree.Iterate(ref netIterator);
                    for (var n = 0; n < nets.Length; n++)
                    {
                        var entity = nets[n];
                        if (!seenNets.Add(entity)
                            || !EntityManager.HasComponent<Game.Net.Edge>(entity)
                            || EntityManager.HasComponent<Deleted>(entity)
                            || EntityManager.HasComponent<Temp>(entity)
                            || !EntityManager.HasComponent<EdgeGeometry>(entity))
                            continue;
                        var geometry = EntityManager.GetComponentData<EdgeGeometry>(entity);
                        if (DistanceToNetwork(geometry, point) >= radius) continue;
                        if (NearEntrance(point, GateRoadExemption)
                            && EntityManager.HasComponent<Game.Net.Road>(entity))
                            continue;
                        return NoteBuildWarning(world, "bestehendes Netz in der Nähe", out reason);
                    }

                    seenObjects.Clear();
                    objects.Clear();
                    var objectIterator = new SnapEntityIterator
                    {
                        Bounds = bounds,
                        Results = objects,
                    };
                    objectTree.Iterate(ref objectIterator);
                    for (var n = 0; n < objects.Length; n++)
                    {
                        var entity = objects[n];
                        if (!seenObjects.Add(entity)
                            || EntityManager.HasComponent<Deleted>(entity)
                            || EntityManager.HasComponent<Temp>(entity)
                            || !EntityManager.HasComponent<Transform>(entity)
                            || !EntityManager.HasComponent<PrefabRef>(entity))
                            continue;
                        var prefab = EntityManager.GetComponentData<PrefabRef>(entity)
                            .m_Prefab;
                        if (!EntityManager.HasComponent<ObjectGeometryData>(prefab))
                            continue;
                        var center = EntityManager.GetComponentData<Transform>(entity)
                            .m_Position.xz;
                        var geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
                        var size = math.max(math.abs(geometry.m_Bounds.min.xz),
                            math.abs(geometry.m_Bounds.max.xz));
                        if (EntityManager.HasComponent<BuildingData>(prefab))
                            size = math.max(size, (float2)EntityManager
                                .GetComponentData<BuildingData>(prefab).m_LotSize * 4f);
                        if (math.any(math.abs(point - center) > size + radius)) continue;
                        return NoteBuildWarning(world,
                            EntityManager.HasComponent<BuildingData>(prefab)
                                ? "Gebäude in der Nähe" : "Objekt in der Nähe", out reason);
                    }
                }
            }

            // Sample the surface independently of the path network. A steep
            // pocket between paths must not be missed by vertex-only checks.
            var hub = FindInteriorHub();
            var hubWorld = new float3(hub.x, 0f, hub.y);
            hubWorld.y = TerrainUtils.SampleHeight(ref terrain, hubWorld);
            var min = _points[0];
            var max = _points[0];
            for (var i = 1; i < _points.Count; i++)
            {
                min = math.min(min, _points[i]);
                max = math.max(max, _points[i]);
            }
            var surfaceStep = math.max(8f,
                math.sqrt(math.max(1f, (max.x - min.x) * (max.y - min.y))) / 64f);
            for (var z = min.y; z <= max.y; z += surfaceStep)
            for (var x = min.x; x <= max.x; x += surfaceStep)
            {
                var point = new float2(x, z);
                if (!PointInside(point)) continue;
                var world = new float3(x, 0f, z);
                world.y = TerrainUtils.SampleHeight(ref terrain, world);
                if (!math.isfinite(world.y))
                    return RejectBuildSite(world, "Terrainhöhe nicht verfügbar", out reason);
                var east = point + new float2(surfaceStep, 0f);
                var north = point + new float2(0f, surfaceStep);
                if (PointInside(east) && SurfaceGrade(point, east, world.y,
                        ref terrain) > MaximumSurfaceGrade
                    || PointInside(north) && SurfaceGrade(point, north, world.y,
                        ref terrain) > MaximumSurfaceGrade)
                    return NoteBuildWarning(world, "Parkfläche stark geneigt", out reason);
            }
            for (var i = 0; i < _points.Count; i++)
            {
                var vertex = _points[i];
                var distance = math.distance(vertex, hub);
                if (distance < 0.1f) continue;
                var sample = new float3(vertex.x, 0f, vertex.y);
                sample.y = TerrainUtils.SampleHeight(ref terrain, sample);
                if (!math.isfinite(sample.y) || !math.isfinite(hubWorld.y)
                    || math.abs(sample.y - hubWorld.y) / distance
                       > MaximumSurfaceGrade)
                    return NoteBuildWarning(sample, "Parkfläche stark geneigt", out reason);
            }
            return true;
        }

        private static float SurfaceGrade(float2 a, float2 b, float height,
            ref TerrainHeightData terrain)
        {
            var other = new float3(b.x, 0f, b.y);
            other.y = TerrainUtils.SampleHeight(ref terrain, other);
            return math.isfinite(other.y)
                ? math.abs(other.y - height) / math.distance(a, b)
                : float.PositiveInfinity;
        }

        private bool RejectBuildSite(float3 position, string problem,
            out string reason)
        {
            _buildIssues.Add(position);
            reason = $"Bauprüfung: {problem} bei X {position.x:F0}, Z {position.z:F0}.";
            Mod.Log.Warn($"ParkManager preflight rejected seed "
                + $"{_decorationPlan?.Seed ?? _pathPlan.Seed}: "
                + reason);
            return false;
        }

        private bool NoteBuildWarning(float3 position, string problem,
            out string reason)
        {
            _buildIssues.Add(position);
            reason = $"Hinweis: {problem} bei X {position.x:F0}, Z {position.z:F0}. "
                + "Bau wird trotzdem versucht.";
            _preflightWarning = reason;
            Mod.Log.Info($"ParkManager advisory preflight seed "
                + $"{_decorationPlan?.Seed ?? _pathPlan.Seed}: {reason}");
            return true;
        }

        private bool ValidateDecorationSite(out string reason)
        {
            _buildIssues.Clear();
            _preflightWarning = null;
            reason = null;
            var tree = _objectSearchSystem.GetStaticSearchTree(true, out var deps);
            deps.Complete();
            using var objects = new NativeList<Entity>(16, Allocator.Temp);
            for (var i = 0; i < _decorationPlan.Placements.Count; i++)
            {
                var placement = _decorationPlan.Placements[i];
                if (placement.Kind == ParkDecorationKind.Fence) continue;
                var point = placement.Position;
                var radius = math.max(0.6f, placement.Size * 0.5f);
                objects.Clear();
                var iterator = new SnapEntityIterator
                {
                    Bounds = new Bounds2(point - radius, point + radius),
                    Results = objects,
                };
                tree.Iterate(ref iterator);
                for (var n = 0; n < objects.Length; n++)
                {
                    var entity = objects[n];
                    if (EntityManager.HasComponent<Deleted>(entity)
                        || EntityManager.HasComponent<Temp>(entity)
                        || !EntityManager.HasComponent<Transform>(entity)
                        || !EntityManager.HasComponent<PrefabRef>(entity)) continue;
                    if (EntityManager.HasComponent<ParkPathMember>(entity)
                        && EntityManager.GetComponentData<ParkPathMember>(entity).Park
                           == _lastBuildRecord) continue;
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity)
                        .m_Prefab;
                    if (!EntityManager.HasComponent<ObjectGeometryData>(prefab))
                        continue;
                    var geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
                    var size = math.max(math.abs(geometry.m_Bounds.min.xz),
                        math.abs(geometry.m_Bounds.max.xz));
                    var center = EntityManager.GetComponentData<Transform>(entity)
                        .m_Position.xz;
                    if (math.any(math.abs(point - center) > size + radius)) continue;
                    return NoteBuildWarning(new float3(point.x, 0f, point.y),
                        "vorhandenes Objekt an Ausstattungsposition", out reason);
                }
            }
            return true;
        }

        private bool NearEntrance(float2 point, float radius)
        {
            for (var i = 0; i < _entrances.Count; i++)
                if (math.distancesq(point, _entrances[i].xz) <= radius * radius)
                    return true;
            return false;
        }

        private static float DistanceToNetwork(EdgeGeometry geometry, float2 point)
        {
            var best = float.MaxValue;
            best = math.min(best, MathUtils.Distance(geometry.m_Start.m_Left.xz,
                point, out _));
            best = math.min(best, MathUtils.Distance(geometry.m_Start.m_Right.xz,
                point, out _));
            best = math.min(best, MathUtils.Distance(geometry.m_End.m_Left.xz,
                point, out _));
            best = math.min(best, MathUtils.Distance(geometry.m_End.m_Right.xz,
                point, out _));
            return best;
        }
    }
}
