using System;
using System.Collections.Generic;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Builds a plaza as one navigable polygon and one pedestrian-access
    /// marker per entrance. No network course or invisible path is emitted.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private EntityQuery _plazaNavigationPrefabQuery;
        private EntityQuery _plazaAccessPrefabQuery;
        private EntityQuery _tempPlazaAccessQuery;
        private EntityQuery _permanentPlazaAccessQuery;
        private Entity _plazaNavigationPrefab = Entity.Null;
        private Entity _plazaAccessPrefab = Entity.Null;
        private readonly HashSet<Entity> _plazaAreaBaseline = new HashSet<Entity>();
        private readonly HashSet<Entity> _plazaAccessBaseline = new HashSet<Entity>();
        private int _expectedPlazaAccessMarkers;

        private void InitializePlazaAccessPlacement()
        {
            _plazaNavigationPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<NavigationAreaData>(),
                ComponentType.ReadOnly<AreaData>(),
                ComponentType.ReadOnly<AreaGeometryData>(),
                ComponentType.Exclude<PlaceholderObjectElement>());
            _plazaAccessPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<RouteConnectionData>(),
                ComponentType.Exclude<PlaceholderObjectElement>());
            _tempPlazaAccessQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>() },
            });
            _permanentPlazaAccessQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>() },
            });
        }

        private bool ResolvePlazaAccessPrefabs()
        {
            if (_plazaNavigationPrefab != Entity.Null
                && _plazaAccessPrefab != Entity.Null
                && EntityManager.Exists(_plazaNavigationPrefab)
                && EntityManager.Exists(_plazaAccessPrefab)) return true;
            _plazaNavigationPrefab = FindPlazaPrefab(_plazaNavigationPrefabQuery,
                new[] { "Pedestrian Area", "Walking Area", "Pathfinding Area",
                    "Pedestrian Pathfinding Area" }, true);
            _plazaAccessPrefab = FindPlazaPrefab(_plazaAccessPrefabQuery,
                new[] { "Pedestrian Access Location" },
                false);
            _usesSurfaceFallback = false;
            _selectedPathWidth = 0f;
            _selectedPathPrefabName = "Plaza-Navigationsfläche";
            return _plazaNavigationPrefab != Entity.Null
                && _plazaAccessPrefab != Entity.Null;
        }

        private Entity FindPlazaPrefab(EntityQuery query, string[] names,
            bool area)
        {
            using var prefabs = query.ToEntityArray(Allocator.TempJob);
            var candidates = new List<string>();
            for (var i = 0; i < prefabs.Length; i++)
            {
                var entity = prefabs[i];
                if (!_pathPrefabSystem.TryGetPrefab<PrefabBase>(entity,
                        out var prefab) || prefab == null || !prefab.isBuiltin)
                    continue;
                var name = prefab.name ?? string.Empty;
                if (name.IndexOf("pedestrian", StringComparison.OrdinalIgnoreCase)
                    >= 0 || name.IndexOf("walking", StringComparison.OrdinalIgnoreCase)
                    >= 0 || name.IndexOf("pathfinding", StringComparison.OrdinalIgnoreCase)
                    >= 0) candidates.Add(name);
                if (area && !HasUsableAreaPrefab(entity)) continue;
                for (var n = 0; n < names.Length; n++)
                    if (string.Equals(name, names[n],
                        StringComparison.OrdinalIgnoreCase))
                    {
                        Mod.Log.Info($"ParkManager plaza access uses '{name}' "
                            + $"({prefab.GetType().Name}, "
                            + $"{(area ? "navigation polygon" : "entrance marker")}).");
                        if (!area)
                        {
                            var route = EntityManager.GetComponentData<
                                RouteConnectionData>(entity);
                            Mod.Log.Info("ParkManager plaza access route "
                                + $"access={route.m_AccessConnectionType} "
                                + $"route={route.m_RouteConnectionType}.");
                        }
                        return entity;
                    }
            }
            Mod.Log.Warn($"ParkManager has no standalone plaza {(area ? "navigation area" : "pedestrian access marker")} prefab. Candidates: {string.Join(", ", candidates)}");
            return Entity.Null;
        }

        private void BuildPlazaAccess()
        {
            if (_plazaPlan == null || _pathPlan == null || _points.Count < 3
                || _entrances.Count == 0)
            {
                PublishState("Zuerst Plaza und Eingänge planen.");
                return;
            }
            if (HasBuiltPaths)
            {
                PublishState("Die Plaza ist bereits gebaut.");
                return;
            }
            if (!ResolvePlazaAccessPrefabs())
            {
                const string error = "Vanilla-Navigationsfläche oder Fußgänger-Zugangsmarker fehlt; die Plaza wird nicht ohne Zugang gebaut. Details im Log.";
                PublishState(error);
                PublishPathBuildState(error);
                return;
            }
            if (!_assetCatalog.TryGetSelected(Assets.ParkAssetCategory.Surface,
                    out _parkSurfacePrefab, out _))
            {
                PublishState("Kein sichtbarer Plaza-Untergrund verfügbar.");
                return;
            }
            try
            {
                if (!ValidateBuildSite(out var siteProblem))
                {
                    PublishState(siteProblem);
                    PublishPathBuildState(siteProblem);
                    return;
                }
                _buildDefinitions.Clear();
                ClearPlazaAccessBaseline();
                CapturePrefabBaseline(_permanentAreaQuery,
                    _plazaNavigationPrefab, _plazaAreaBaseline);
                CapturePrefabBaseline(_permanentPlazaAccessQuery,
                    _plazaAccessPrefab, _plazaAccessBaseline);
                _parkSurfaceAreaBaseline.Clear();
                CapturePrefabBaseline(_permanentAreaQuery,
                    _parkSurfacePrefab, _parkSurfaceAreaBaseline);
                _pendingBuildRecord = CreatePathBuildRecord(_pathPlan.Seed);
                var terrain = _terrainSystem.GetHeightData(waitForPending: true);
                if (!CreatePlazaNavigationArea(ref terrain)
                    || !CreateParkSurface(_parkSurfacePrefab, ref terrain))
                    throw new InvalidOperationException("Plaza-Polygon oder Terrain ungültig.");
                _expectedPlazaAccessMarkers = 0;
                for (var i = 0; i < _entrances.Count; i++)
                {
                    if (!CreatePlazaAccessMarker(_entrances[i], ref terrain))
                        throw new InvalidOperationException($"Zugang {i + 1} konnte nicht platziert werden.");
                    _expectedPlazaAccessMarkers++;
                }
                _expectedPathCourses = 0;
                _expectedPathAreas = 0;
                _expectedParkSurfaceAreas = 1;
                _pathBuildStartedFrame = UnityEngine.Time.frameCount;
                _pathBuildPhase = PathBuildPhase.WaitingForMaterialization;
                PublishState($"Plaza-Bau gestartet: Polygon und {_expectedPlazaAccessMarkers} Zugänge.");
                PublishPathBuildState("CS2 erzeugt Navigationsfläche und Zugänge …");
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager could not prepare plaza access.");
                AbortPathBuild("Plaza-Bau konnte nicht vorbereitet werden: "
                    + exception.Message);
            }
        }

        private bool CreatePlazaNavigationArea(ref TerrainHeightData terrain)
        {
            var definition = CreateBuildDefinition();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = _plazaNavigationPrefab,
            });
            EntityManager.AddComponent<Updated>(definition);
            var nodes = EntityManager.AddBuffer<Game.Areas.Node>(definition);
            nodes.ResizeUninitialized(_points.Count + 1);
            for (var i = 0; i < _points.Count; i++)
            {
                var point = _points[i];
                var height = TerrainUtils.SampleHeight(ref terrain,
                    new float3(point.x, 0f, point.y));
                if (!math.isfinite(height)) return false;
                nodes[i] = new Game.Areas.Node(
                    new float3(point.x, height, point.y), float.MinValue);
            }
            nodes[_points.Count] = nodes[0];
            return true;
        }

        private bool CreatePlazaAccessMarker(float3 entrance,
            ref TerrainHeightData terrain)
        {
            if (!TryGetPlazaAccessPosition(entrance.xz,
                    out var inside, out var outward)) return false;
            var point = new float3(inside.x, 0f, inside.y);
            point.y = TerrainUtils.SampleHeight(ref terrain, point);
            if (!math.all(math.isfinite(point))) return false;
            var definition = CreateBuildDefinition();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = _plazaAccessPrefab,
            });
            EntityManager.AddComponent<Updated>(definition);
            var objectDefinition = default(ObjectDefinition);
            objectDefinition.m_Position = point;
            objectDefinition.m_Rotation = quaternion.LookRotationSafe(
                new float3(outward.x, 0f, outward.y),
                new float3(0f, 1f, 0f));
            objectDefinition.m_Probability = 100;
            objectDefinition.m_PrefabSubIndex = -1;
            objectDefinition.m_Scale = 1f;
            objectDefinition.m_Intensity = 1f;
            objectDefinition.m_ParentMesh = -1;
            EntityManager.AddComponentData(definition, objectDefinition);
            Mod.Log.Info($"ParkManager plaza access marker planned at "
                + $"{point} ({math.distance(entrance.xz, inside):F2} m "
                + "inside the entrance boundary).");
            return true;
        }

        private bool TryGetPlazaAccessPosition(float2 entrance,
            out float2 position, out float2 outward)
        {
            position = default;
            outward = default;
            var bestDistance = float.MaxValue;
            var edgeIndex = -1;
            for (var i = 0; i < _points.Count; i++)
            {
                var a = _points[i];
                var b = _points[(i + 1) % _points.Count];
                var edge = b - a;
                var t = math.clamp(math.dot(entrance - a, edge)
                    / math.max(0.0001f, math.lengthsq(edge)), 0f, 1f);
                var distance = math.distancesq(entrance, a + edge * t);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                edgeIndex = i;
            }
            if (edgeIndex < 0) return false;
            var direction = math.normalizesafe(_points[(edgeIndex + 1)
                % _points.Count] - _points[edgeIndex], new float2(1f, 0f));
            var inward = new float2(-direction.y, direction.x)
                * (SignedArea() >= 0 ? 1f : -1f);
            outward = -inward;
            // Vanilla access columns are placed inside the walkable polygon,
            // not directly on its boundary. Try several bounded distances so
            // narrow corners still get a valid, interior marker.
            var distances = new[] { 1.25f, 0.75f, 2f, 0.4f };
            for (var i = 0; i < distances.Length; i++)
            {
                var candidate = entrance + inward * distances[i];
                if (!PointInside(candidate)) continue;
                position = candidate;
                return true;
            }
            return false;
        }

        private bool ProcessPlazaAccessPlacement()
        {
            switch (_pathBuildPhase)
            {
                case PathBuildPhase.WaitingForMaterialization:
                    applyMode = ApplyMode.None;
                    var area = CountOwnTempEntities(_tempAreaQuery,
                        _plazaNavigationPrefab);
                    var markers = CountOwnTempEntities(_tempPlazaAccessQuery,
                        _plazaAccessPrefab);
                    var surface = FindParkSurfaceArea(_tempAreaQuery,
                        _parkSurfacePrefab);
                    if (area >= 1 && markers >= _expectedPlazaAccessMarkers
                        && surface != Entity.Null)
                    {
                        var nextId = 1;
                        var attachedArea = TagPlazaTempEntities(_tempAreaQuery,
                            _plazaNavigationPrefab,
                            ParkPathMemberKind.NavigationArea, ref nextId);
                        var attachedMarkers = TagPlazaTempEntities(
                            _tempPlazaAccessQuery, _plazaAccessPrefab,
                            ParkPathMemberKind.AccessMarker, ref nextId);
                        var attachedSurface = SetMaterializedMember(surface,
                            _pendingBuildRecord, ParkPathMemberKind.ParkSurface,
                            ref nextId);
                        if (attachedArea < 1 || attachedMarkers
                            < _expectedPlazaAccessMarkers || !attachedSurface)
                            return true;
                        applyMode = ApplyMode.Apply;
                        _pathApplyFrame = UnityEngine.Time.frameCount;
                        _pathBuildPhase = PathBuildPhase.ApplyRequested;
                        PublishPathBuildState("Navigationsfläche wird übernommen …");
                        return true;
                    }
                    if (UnityEngine.Time.frameCount - _pathBuildStartedFrame
                        <= MaterializationTimeoutFrames) return true;
                    AbortPathBuild($"Plaza-Entities fehlen: {area}/1 Fläche, "
                        + $"{markers}/{_expectedPlazaAccessMarkers} Zugänge, "
                        + $"{(surface == Entity.Null ? 0 : 1)}/1 Untergrund.");
                    return true;
                case PathBuildPhase.ApplyRequested:
                    applyMode = ApplyMode.None;
                    if (UnityEngine.Time.frameCount - _pathApplyFrame
                        < GeometrySettleFrames) return true;
                    TagMaterializedPlazaAccess(_pendingBuildRecord,
                        out var areas, out var accesses, out var surfaces);
                    if (areas >= 1 && accesses >= _expectedPlazaAccessMarkers
                        && surfaces >= 1)
                    {
                        LogPlazaAccessConnections(_pendingBuildRecord);
                        FinalizeEditablePathBuild();
                        return true;
                    }
                    if (UnityEngine.Time.frameCount - _pathApplyFrame
                        <= MaterializationTimeoutFrames) return true;
                    AbortPathBuild($"Plaza nach Übernahme unvollständig: "
                        + $"{areas}/1 Fläche, "
                        + $"{accesses}/{_expectedPlazaAccessMarkers} Zugänge, "
                        + $"{surfaces}/1 Untergrund.");
                    return true;
                case PathBuildPhase.ClearRequested:
                    applyMode = ApplyMode.Clear;
                    _pathBuildPhase = PathBuildPhase.Idle;
                    return true;
                default:
                    return false;
            }
        }

        private int TagPlazaTempEntities(EntityQuery query, Entity prefab,
            ParkPathMemberKind kind, ref int nextId)
        {
            var count = 0;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != prefab) continue;
                if (SetMaterializedMember(entity, _pendingBuildRecord, kind,
                    ref nextId)) count++;
            }
            return count;
        }

        private void TagMaterializedPlazaAccess(Entity park,
            out int areaCount, out int markerCount, out int surfaceCount)
        {
            areaCount = 0;
            markerCount = 0;
            surfaceCount = 0;
            if (park == Entity.Null || !EntityManager.Exists(park)) return;
            var nextId = NextMemberElementId(park);
            using (var areas = _permanentAreaQuery.ToEntityArray(Allocator.TempJob))
                for (var i = 0; i < areas.Length; i++)
                {
                    var entity = areas[i];
                    var prefab = EntityManager.GetComponentData<PrefabRef>(entity)
                        .m_Prefab;
                    if (prefab == _plazaNavigationPrefab
                        && IsMaterializedBuildEntity(entity, park,
                            _plazaAreaBaseline)
                        && SetMaterializedMember(entity, park,
                            ParkPathMemberKind.NavigationArea, ref nextId))
                        areaCount++;
                    else if (prefab == _parkSurfacePrefab
                        && IsParkSurfaceArea(entity)
                        && IsMaterializedBuildEntity(entity, park,
                            _parkSurfaceAreaBaseline)
                        && SetMaterializedMember(entity, park,
                            ParkPathMemberKind.ParkSurface, ref nextId))
                        surfaceCount++;
                }
            using var markers = _permanentPlazaAccessQuery
                .ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < markers.Length; i++)
            {
                var entity = markers[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                        != _plazaAccessPrefab
                    || !IsMaterializedBuildEntity(entity, park,
                        _plazaAccessBaseline)
                    || !SetMaterializedMember(entity, park,
                        ParkPathMemberKind.AccessMarker, ref nextId)) continue;
                markerCount++;
            }
        }

        private void ClearPlazaAccessBaseline()
        {
            _plazaAreaBaseline.Clear();
            _plazaAccessBaseline.Clear();
        }

        private void LogPlazaAccessConnections(Entity park)
        {
            using var entities = _permanentPlazaAccessQuery
                .ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (!EntityManager.HasComponent<ParkPathMember>(entity)
                    || EntityManager.GetComponentData<ParkPathMember>(entity)
                        .Park != park) continue;
                var transform = EntityManager.GetComponentData<
                    Game.Objects.Transform>(entity);
                var hasSpawn = EntityManager.HasComponent<
                    Game.Objects.SpawnLocation>(entity);
                Mod.Log.Info($"ParkManager plaza access marker {entity}: "
                    + $"position={transform.m_Position} "
                    + $"spawnComponent={hasSpawn} "
                    + $"owner={EntityManager.HasComponent<Owner>(entity)}.");
                if (!hasSpawn) continue;
                var spawn = EntityManager.GetComponentData<
                    Game.Objects.SpawnLocation>(entity);
                Mod.Log.Info($"ParkManager plaza access marker {entity}: "
                    + $"connected lanes {spawn.m_ConnectedLane1}, "
                    + $"{spawn.m_ConnectedLane2}.");
                LogPlazaConnectedLane(spawn.m_ConnectedLane1);
                LogPlazaConnectedLane(spawn.m_ConnectedLane2);
            }
        }

        private void LogPlazaConnectedLane(Entity lane)
        {
            if (lane == Entity.Null || !EntityManager.Exists(lane)) return;
            var owner = EntityManager.HasComponent<Owner>(lane)
                ? EntityManager.GetComponentData<Owner>(lane).m_Owner
                : Entity.Null;
            var ownerPrefab = owner != Entity.Null
                && EntityManager.Exists(owner)
                && EntityManager.HasComponent<PrefabRef>(owner)
                ? EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab
                : Entity.Null;
            var ownerName = ownerPrefab != Entity.Null
                && _pathPrefabSystem.TryGetPrefab<PrefabBase>(ownerPrefab,
                    out var prefab) ? prefab?.name : null;
            Mod.Log.Info($"ParkManager plaza access lane {lane}: "
                + $"area={EntityManager.HasComponent<Game.Net.AreaLane>(lane)} "
                + $"pedestrian={EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)} "
                + $"connection={EntityManager.HasComponent<Game.Net.ConnectionLane>(lane)} "
                + $"owner={owner} ownerPrefab={ownerName ?? "(none)"}.");
        }
    }
}
