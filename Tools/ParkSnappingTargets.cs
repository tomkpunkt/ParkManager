// World snap targets adapted from ParkingLotTool (GPL-3.0).
using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    public sealed partial class ParkToolSystem
    {
        private const float RoadSnapSearchRadius = 48f;
        private Game.Net.SearchSystem _netSearchSystem;
        private Game.Areas.SearchSystem _areaSearchSystem;
        private Game.Objects.SearchSystem _objectSearchSystem;
        private Game.Zones.SearchSystem _zoneSearchSystem;

        private struct SnapEntityIterator
            : INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            internal Bounds2 Bounds;
            internal NativeList<Entity> Results;
            public bool Intersect(QuadTreeBoundsXZ bounds)
                => MathUtils.Intersect(bounds.m_Bounds.xz, Bounds);
            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                if (MathUtils.Intersect(bounds.m_Bounds.xz, Bounds))
                    Results.Add(entity);
            }
        }

        private struct SnapAreaIterator
            : INativeQuadTreeIterator<AreaSearchItem, QuadTreeBoundsXZ>
        {
            internal Bounds2 Bounds;
            internal NativeList<Entity> Results;
            public bool Intersect(QuadTreeBoundsXZ bounds)
                => MathUtils.Intersect(bounds.m_Bounds.xz, Bounds);
            public void Iterate(QuadTreeBoundsXZ bounds, AreaSearchItem item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds.xz, Bounds))
                    Results.Add(item.m_Area);
            }
        }

        private struct SnapZoneIterator
            : INativeQuadTreeIterator<Entity, Bounds2>
        {
            internal Bounds2 Bounds;
            internal NativeList<Entity> Results;
            public bool Intersect(Bounds2 bounds)
                => MathUtils.Intersect(bounds, Bounds);
            public void Iterate(Bounds2 bounds, Entity entity)
            {
                if (MathUtils.Intersect(bounds, Bounds)) Results.Add(entity);
            }
        }

        private void InitializeSnappingTargets()
        {
            _netSearchSystem = World.GetOrCreateSystemManaged<Game.Net.SearchSystem>();
            _areaSearchSystem = World.GetOrCreateSystemManaged<Game.Areas.SearchSystem>();
            _objectSearchSystem = World
                .GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            _zoneSearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
        }

        private void CollectRoadEdges(float3 raw, ref SnapCandidate best)
        {
            if (_netSearchSystem == null) return;
            var tree = _netSearchSystem.GetNetSearchTree(true, out var deps);
            deps.Complete();
            using var results = new NativeList<Entity>(16, Allocator.Temp);
            var iterator = new SnapEntityIterator
            {
                Bounds = new Bounds2(raw.xz - RoadSnapSearchRadius,
                    raw.xz + RoadSnapSearchRadius),
                Results = results,
            };
            tree.Iterate(ref iterator);
            var found = best;

            void Consider(Bezier4x3 curve)
            {
                if (!math.all(math.isfinite(curve.a))
                    || !math.all(math.isfinite(curve.d))
                    || math.distancesq(curve.a, curve.d) < 0.01f
                    || MathUtils.Distance(curve.xz, raw.xz, out var t)
                       >= SnapDistance) return;
                var direction = math.normalizesafe(MathUtils.Tangent(curve, t).xz);
                if (math.lengthsq(direction) < 0.5f) return;
                RegisterSnap(ref found, raw, SnapLevelNet, SnapKind.RoadEdge,
                    MathUtils.Position(curve, t), direction);
            }

            void ConsiderNodeCurve(Bezier4x3 curve)
            {
                var direction = MathUtils.StartTangent(curve);
                direction = MathUtils.Normalize(direction, direction.xz);
                direction.y = math.clamp(direction.y, -1f, 1f);
                var end = curve.a + direction * math.dot(curve.d - curve.a, direction);
                if (!math.all(math.isfinite(end))
                    || math.distancesq(curve.a, end) < 0.01f) return;
                var line = new Line3.Segment(curve.a, end);
                if (MathUtils.Distance(line.xz, raw.xz, out var t)
                    >= SnapDistance) return;
                var axis = math.normalizesafe(direction.xz);
                if (math.lengthsq(axis) < 0.5f) return;
                RegisterSnap(ref found, raw, SnapLevelNet, SnapKind.RoadEdge,
                    MathUtils.Position(line, t), axis);
            }

            void ConsiderNodeGeometry(EdgeNodeGeometry geometry)
            {
                if (geometry.m_MiddleRadius > 0f)
                {
                    ConsiderNodeCurve(geometry.m_Left.m_Left);
                    ConsiderNodeCurve(geometry.m_Left.m_Right);
                    ConsiderNodeCurve(geometry.m_Right.m_Left);
                    ConsiderNodeCurve(geometry.m_Right.m_Right);
                }
                else
                {
                    ConsiderNodeCurve(geometry.m_Left.m_Left);
                    ConsiderNodeCurve(geometry.m_Right.m_Right);
                }
            }

            bool Snappable(Entity composition)
                => composition == Entity.Null
                   || !EntityManager.HasComponent<NetCompositionData>(composition)
                   || (EntityManager.GetComponentData<NetCompositionData>(composition)
                       .m_Flags.m_General & CompositionFlags.General.Tunnel) == 0;

            var seen = new HashSet<Entity>();
            for (var i = 0; i < results.Length; i++)
            {
                var entity = results[i];
                if (!seen.Add(entity)
                    || !EntityManager.HasComponent<Game.Net.Edge>(entity)
                    || !EntityManager.HasComponent<Game.Net.Road>(entity)
                    || EntityManager.HasComponent<Owner>(entity)
                    || EntityManager.HasComponent<Temp>(entity)
                    || EntityManager.HasComponent<Deleted>(entity)) continue;
                var composition = EntityManager.HasComponent<Composition>(entity)
                    ? EntityManager.GetComponentData<Composition>(entity) : default;
                if (Snappable(composition.m_Edge)
                    && EntityManager.HasComponent<EdgeGeometry>(entity))
                {
                    var geometry = EntityManager.GetComponentData<EdgeGeometry>(entity);
                    Consider(geometry.m_Start.m_Left);
                    Consider(geometry.m_Start.m_Right);
                    Consider(geometry.m_End.m_Left);
                    Consider(geometry.m_End.m_Right);
                }
                if (EntityManager.HasComponent<StartNodeGeometry>(entity)
                    && Snappable(composition.m_StartNode))
                    ConsiderNodeGeometry(EntityManager
                        .GetComponentData<StartNodeGeometry>(entity).m_Geometry);
                if (EntityManager.HasComponent<EndNodeGeometry>(entity)
                    && Snappable(composition.m_EndNode))
                    ConsiderNodeGeometry(EntityManager
                        .GetComponentData<EndNodeGeometry>(entity).m_Geometry);
            }
            best = found;
        }

        private void CollectAreaEdges(float3 raw, ref SnapCandidate best)
        {
            if (_areaSearchSystem == null) return;
            var tree = _areaSearchSystem.GetSearchTree(true, out var deps);
            deps.Complete();
            using var results = new NativeList<Entity>(16, Allocator.Temp);
            var iterator = new SnapAreaIterator
            {
                Bounds = new Bounds2(raw.xz - SnapDistance,
                    raw.xz + SnapDistance),
                Results = results,
            };
            tree.Iterate(ref iterator);
            var seen = new HashSet<Entity>();
            for (var i = 0; i < results.Length; i++)
            {
                var entity = results[i];
                if (entity == Entity.Null || !seen.Add(entity)
                    || !EntityManager.HasComponent<Game.Areas.Lot>(entity)
                    || EntityManager.HasComponent<Temp>(entity)
                    || EntityManager.HasComponent<Deleted>(entity)
                    || !EntityManager.HasBuffer<Game.Areas.Node>(entity)) continue;
                var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, true);
                if (nodes.Length < 3) continue;
                for (var n = 0; n < nodes.Length; n++)
                {
                    var from = nodes[n].m_Position;
                    var to = nodes[(n + 1) % nodes.Length].m_Position;
                    var line = new Line3.Segment(from, to);
                    if (MathUtils.Distance(line.xz, raw.xz, out var t)
                        >= SnapDistance) continue;
                    var direction = math.normalizesafe(to.xz - from.xz);
                    if (math.lengthsq(direction) < 0.5f) continue;
                    var startDistance = math.distance(from.xz, raw.xz);
                    var endDistance = math.distance(to.xz, raw.xz);
                    var position = startDistance <= SnapDistance
                                   && startDistance <= endDistance ? from
                        : endDistance <= SnapDistance ? to
                        : MathUtils.Position(line, t);
                    RegisterSnap(ref best, raw, SnapLevelArea, SnapKind.AreaEdge,
                        position, direction);
                }
            }
        }

        private void CollectObjectSides(float3 raw, ref SnapCandidate best)
        {
            if (_objectSearchSystem == null) return;
            var tree = _objectSearchSystem.GetStaticSearchTree(true, out var deps);
            deps.Complete();
            using var results = new NativeList<Entity>(16, Allocator.Temp);
            var iterator = new SnapEntityIterator
            {
                Bounds = new Bounds2(raw.xz - SnapDistance,
                    raw.xz + SnapDistance),
                Results = results,
            };
            tree.Iterate(ref iterator);
            var found = best;
            var heightData = _terrainSystem.GetHeightData();

            void Consider(Line3 side)
            {
                var line = new Line2(side.a.xz, side.b.xz);
                if (math.distancesq(line.a, line.b) < 0.01f
                    || MathUtils.Distance(line, raw.xz, out var t)
                       >= SnapDistance) return;
                var direction = math.normalizesafe(line.b - line.a);
                if (math.lengthsq(direction) < 0.5f) return;
                var point = MathUtils.Position(line, t);
                var height = Game.Simulation.TerrainUtils.SampleHeight(ref heightData,
                    new float3(point.x, raw.y, point.y));
                RegisterSnap(ref found, raw, SnapLevelObject, SnapKind.ObjectSide,
                    new float3(point.x, height, point.y), direction);
            }

            var seen = new HashSet<Entity>();
            for (var i = 0; i < results.Length; i++)
            {
                var entity = results[i];
                if (!seen.Add(entity)
                    || !EntityManager.HasComponent<Game.Objects.Transform>(entity)
                    || !EntityManager.HasComponent<PrefabRef>(entity)
                    || EntityManager.HasComponent<Owner>(entity)
                    || EntityManager.HasComponent<Temp>(entity)
                    || EntityManager.HasComponent<Deleted>(entity)) continue;
                var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (!EntityManager.HasComponent<ObjectGeometryData>(prefab)) continue;
                var geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
                if ((geometry.m_Flags & Game.Objects.GeometryFlags.Circular) != 0)
                    continue;
                var bounds = geometry.m_Bounds;
                if (EntityManager.HasComponent<BuildingData>(prefab))
                {
                    var lotSize = (float2)EntityManager
                        .GetComponentData<BuildingData>(prefab).m_LotSize;
                    bounds.min.xz = lotSize * -4f;
                    bounds.max.xz = lotSize * 4f;
                }
                var transform = EntityManager
                    .GetComponentData<Game.Objects.Transform>(entity);
                var corners = ObjectUtils.CalculateBaseCorners(
                    transform.m_Position, transform.m_Rotation, bounds);
                Consider(corners.ab);
                Consider(corners.bc);
                Consider(corners.cd);
                Consider(corners.da);
            }
            best = found;
        }

        private void CollectZoneGrid(float3 raw, ref SnapCandidate best)
        {
            if (_zoneSearchSystem == null) return;
            const float radius = 8f * 1.4142136f + SnapDistance;
            var tree = _zoneSearchSystem.GetSearchTree(true, out var deps);
            deps.Complete();
            using var results = new NativeList<Entity>(8, Allocator.Temp);
            var iterator = new SnapZoneIterator
            {
                Bounds = new Bounds2(raw.xz - radius, raw.xz + radius),
                Results = results,
            };
            tree.Iterate(ref iterator);
            var bestDistance = radius;
            var cellPosition = float3.zero;
            var cellDirection = float2.zero;
            for (var i = 0; i < results.Length; i++)
            {
                var entity = results[i];
                if (!EntityManager.HasComponent<Game.Zones.Block>(entity)
                    || !EntityManager.HasBuffer<Game.Zones.Cell>(entity)) continue;
                var block = EntityManager.GetComponentData<Game.Zones.Block>(entity);
                var cells = EntityManager.GetBuffer<Game.Zones.Cell>(entity, true);
                var index = math.clamp(Game.Zones.ZoneUtils.GetCellIndex(block, raw.xz),
                    0, block.m_Size - 1);
                if (index.x < 0 || index.y < 0 || index.x >= block.m_Size.x
                    || index.y >= block.m_Size.y) continue;
                var cell = cells[index.x + index.y * block.m_Size.x];
                if ((cell.m_State & Game.Zones.CellFlags.Visible) == 0) continue;
                var position = Game.Zones.ZoneUtils.GetCellPosition(block, index);
                var distance = math.distance(position.xz, raw.xz);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                cellPosition = position;
                cellDirection = block.m_Direction;
            }
            if (math.lengthsq(cellDirection) < 0.5f) return;
            var across = MathUtils.Right(cellDirection);
            var delta = raw.xz - cellPosition.xz;
            var along = MathUtils.Snap(math.dot(delta, cellDirection), 8f, 4f);
            var side = MathUtils.Snap(math.dot(delta, across), 8f, 4f);
            var snapped = cellPosition.xz + cellDirection * along + across * side;
            var heightData = _terrainSystem.GetHeightData();
            var world = new float3(snapped.x,
                Game.Simulation.TerrainUtils.SampleHeight(ref heightData,
                    new float3(snapped.x, raw.y, snapped.y)), snapped.y);
            RegisterSnap(ref best, raw, SnapLevelZoneGrid, SnapKind.ZoneGrid,
                world, cellDirection, true);
            RegisterSnap(ref best, raw, SnapLevelZoneGrid, SnapKind.ZoneGrid,
                world, across, true);
        }
    }
}
