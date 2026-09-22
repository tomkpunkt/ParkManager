using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using ParkManager.Geometry;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    public sealed partial class ParkToolSystem
    {
        private const string FallbackPedestrianPathPrefabName = "Invisible Pedestrian Path";
        private const string PavementSurfacePrefabName = "Pavement Surface 01";
        private const int MaterializationTimeoutFrames = 120;
        private const int GeometrySettleFrames = 5;
        private const int ModificationCheckIntervalFrames = 30;

        private enum PathBuildPhase
        {
            Idle,
            WaitingForMaterialization,
            ApplyRequested,
            ClearRequested,
        }

        private PrefabSystem _pathPrefabSystem;
        private TerrainSystem _terrainSystem;
        private EntityQuery _pathPrefabQuery;
        private EntityQuery _surfacePrefabQuery;
        private EntityQuery _tempPathQuery;
        private EntityQuery _tempAreaQuery;
        private EntityQuery _permanentPathQuery;
        private EntityQuery _permanentAreaQuery;
        private EntityQuery _pathMemberQuery;
        private EntityQuery _parkBuildQuery;
        private EntityQuery _legacyBuildQuery;
        private EntityQuery _legacyOwnedPathPartsQuery;
        private Entity _pedestrianPathPrefab = Entity.Null;
        private Entity _pavementSurfacePrefab = Entity.Null;
        private bool _usesSurfaceFallback;
        private string _selectedPathPrefabName = string.Empty;
        private float _selectedPathWidth = 4f;
        private Entity _pendingBuildRecord = Entity.Null;
        private Entity _lastBuildRecord = Entity.Null;
        private bool _lastBuildIsLegacy;
        private PathBuildPhase _pathBuildPhase;
        private int _pathBuildStartedFrame;
        private int _pathApplyFrame;
        private int _expectedPathCourses;
        private int _expectedPathAreas;
        private int _lastModificationCheckFrame;
        private readonly HashSet<Entity> _pathEntityBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _areaEntityBaseline =
            new HashSet<Entity>();

        private bool PathBuildBusy => _pathBuildPhase != PathBuildPhase.Idle;
        private bool HasBuiltPaths => _lastBuildRecord != Entity.Null
            && EntityManager.Exists(_lastBuildRecord)
            && !EntityManager.HasComponent<Deleted>(_lastBuildRecord);

        private void InitializePathPlacement()
        {
            _pathPrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            _pathPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PathwayData>(),
                ComponentType.ReadOnly<NetGeometryData>(),
                ComponentType.ReadOnly<NetData>(),
                ComponentType.Exclude<PlaceholderObjectElement>());
            _surfacePrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<SurfaceData>(),
                ComponentType.ReadOnly<AreaData>(),
                ComponentType.ReadOnly<AreaGeometryData>(),
                ComponentType.Exclude<PlaceholderObjectElement>());
            _tempPathQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _tempAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _permanentPathQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>() },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Edge>(),
                    ComponentType.ReadOnly<Game.Net.Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _permanentAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _pathMemberQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ParkPathMember>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _parkBuildQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ParkPathBuildMarker>(),
                    ComponentType.ReadOnly<ParkEditableBuildState>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _legacyBuildQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ParkPathBuildMarker>() },
                None = new[]
                {
                    ComponentType.ReadOnly<ParkEditableBuildState>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _legacyOwnedPathPartsQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
        }

        internal void BuildPaths()
        {
            if (PathBuildBusy)
            {
                PublishState("Der aktuelle Wegebau wird noch von CS2 verarbeitet.");
                return;
            }
            if (_pathPlan == null || _pathPlan.Edges.Count == 0)
            {
                PublishState("Zuerst einen Wegentwurf erzeugen.");
                return;
            }
            if (HasBuiltPaths)
            {
                PublishState("Vor einem Neubau zuerst die gebauten Testwege entfernen.");
                return;
            }
            if (!ResolvePlacementPrefabs())
            {
                PublishState("Benötigte Vanilla-Prefabs sind noch nicht verfügbar.");
                return;
            }

            try
            {
                // A failed build must leave this draft active instead of
                // silently selecting one of the already completed parks.
                _freshDraftActive = true;
                CaptureMaterializationBaseline();
                _pendingBuildRecord = CreatePathBuildRecord(_pathPlan.Seed);
                var heightData = _terrainSystem.GetHeightData(waitForPending: true);
                var heights = new Dictionary<int, float>();
                var randomSeed = (uint)Math.Max(1, _pathPlan.Seed);
                var random = new Unity.Mathematics.Random(randomSeed);
                _expectedPathCourses = 0;
                _expectedPathAreas = 0;
                for (var i = 0; i < _pathPlan.Edges.Count; i++)
                {
                    var edge = _pathPlan.Edges[i];
                    var a2 = _pathPlan.Nodes[edge.A].Position;
                    var b2 = _pathPlan.Nodes[edge.B].Position;
                    var a = WorldPathPoint(edge.A, a2, ref heightData, heights);
                    var b = WorldPathPoint(edge.B, b2, ref heightData, heights);
                    if (CreatePathCourse(a, b, ref random)) _expectedPathCourses++;
                    if (_usesSurfaceFallback
                        && CreatePathSurface(a2, b2, edge.Width, ref heightData))
                        _expectedPathAreas++;
                }

                if (_expectedPathCourses == 0
                    || _usesSurfaceFallback && _expectedPathAreas == 0)
                    throw new InvalidOperationException("Der Plan enthält keine baubaren Segmente.");

                _pathBuildStartedFrame = UnityEngine.Time.frameCount;
                _pathBuildPhase = PathBuildPhase.WaitingForMaterialization;
                PublishState($"Wegebau gestartet: {_expectedPathCourses} Segmente werden materialisiert.");
                PublishPathBuildState(_usesSurfaceFallback
                    ? "CS2 erzeugt Wegknoten und Ersatzoberflächen …"
                    : $"CS2 erzeugt das Vanilla-Netz '{_selectedPathPrefabName}' …");
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager could not create path definitions.");
                AbortPathBuild("Wegebau konnte nicht vorbereitet werden: " + exception.Message);
            }
        }

        internal void RemoveBuiltPaths()
        {
            if (PathBuildBusy)
            {
                PublishState("Entfernen ist erst nach Abschluss des Wegebaues möglich.");
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState("Es sind keine von ParkManager gebauten Testwege vorhanden.");
                PublishPathBuildState("Noch keine Testwege gebaut.");
                return;
            }

            var removed = 0;
            if (_lastBuildIsLegacy)
            {
                using var entities = _legacyOwnedPathPartsQuery.ToEntityArray(Allocator.TempJob);
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (EntityManager.GetComponentData<Owner>(entity).m_Owner
                        != _lastBuildRecord) continue;
                    EntityManager.AddComponent<Deleted>(entity);
                    removed++;
                }
            }
            else
            {
                removed = DeleteEditableMembers(_lastBuildRecord);
            }
            EntityManager.AddComponent<Deleted>(_lastBuildRecord);
            _lastBuildRecord = Entity.Null;
            _lastBuildIsLegacy = false;
            _restoredReceiptRecord = Entity.Null;
            _freshDraftActive = true;
            DecorationBuildWasRemoved();
            PublishState($"Gebauter Park entfernt ({removed} Teile).");
            PublishPathBuildState("Noch kein Park gebaut.");
            PublishWorkspaceState();
        }

        private bool ProcessPathPlacement()
        {
            switch (_pathBuildPhase)
            {
                case PathBuildPhase.Idle:
                    applyMode = ApplyMode.None;
                    return false;
                case PathBuildPhase.WaitingForMaterialization:
                    applyMode = ApplyMode.None;
                    var pathParts = CountOwnTempEdges(_tempPathQuery,
                        _pedestrianPathPrefab);
                    var areas = _usesSurfaceFallback
                        ? CountOwnTempEntities(_tempAreaQuery, _pavementSurfacePrefab)
                        : 0;
                    if (pathParts >= _expectedPathCourses && areas >= _expectedPathAreas)
                    {
                        var nextElementId = 1;
                        var attachedPaths = TagEditableTempEntities(_tempPathQuery,
                            _pedestrianPathPrefab, _pendingBuildRecord,
                            ref nextElementId);
                        var attachedAreas = _usesSurfaceFallback
                            ? TagEditableTempEntities(_tempAreaQuery,
                                _pavementSurfacePrefab, _pendingBuildRecord,
                                ref nextElementId)
                            : 0;
                        if (attachedPaths < _expectedPathCourses
                            || attachedAreas < _expectedPathAreas) return true;

                        applyMode = ApplyMode.Apply;
                        _pathApplyFrame = UnityEngine.Time.frameCount;
                        _pathBuildPhase = PathBuildPhase.ApplyRequested;
                        Mod.Log.Info($"ParkManager tagged {attachedPaths} temporary path "
                            + $"entities and {attachedAreas} temporary areas before Apply; "
                            + "the permanent graph will be rediscovered afterwards.");
                        PublishPathBuildState("Vanilla-Wege werden als frei editierbare Elemente übernommen …");
                        return true;
                    }
                    if (UnityEngine.Time.frameCount - _pathBuildStartedFrame
                        <= MaterializationTimeoutFrames) return true;
                    AbortPathBuild("Zeitüberschreitung beim Erzeugen der Weg-Entities.");
                    return true;
                case PathBuildPhase.ApplyRequested:
                    applyMode = ApplyMode.None;
                    if (UnityEngine.Time.frameCount - _pathApplyFrame
                        < GeometrySettleFrames) return true;
                    TagMaterializedPathEntities(_pendingBuildRecord,
                        out var permanentEdges, out var permanentNodes,
                        out var permanentAreas);
                    if (permanentEdges < _expectedPathCourses
                        || permanentAreas < _expectedPathAreas)
                    {
                        if (UnityEngine.Time.frameCount - _pathApplyFrame
                            <= MaterializationTimeoutFrames) return true;
                        AbortPathBuild("Das materialisierte Wegenetz blieb "
                            + $"unvollständig: {permanentEdges}/{_expectedPathCourses} "
                            + $"Kanten, {permanentNodes} Knoten und "
                            + $"{permanentAreas}/{_expectedPathAreas} Flächen.");
                        return true;
                    }
                    Mod.Log.Info($"ParkManager rediscovered the permanent path graph: "
                        + $"{permanentEdges}/{_expectedPathCourses} edges, "
                        + $"{permanentNodes} merged nodes and "
                        + $"{permanentAreas}/{_expectedPathAreas} surfaces.");
                    FinalizeEditablePathBuild();
                    return true;
                case PathBuildPhase.ClearRequested:
                    applyMode = ApplyMode.Clear;
                    _pathBuildPhase = PathBuildPhase.Idle;
                    PublishPathBuildState("Wegebau wurde verworfen.");
                    return true;
                default:
                    return false;
            }
        }

        private bool ResolvePlacementPrefabs()
        {
            if (HasBuiltinEntity(_pedestrianPathPrefab))
                return !_usesSurfaceFallback
                    || HasUsableAreaPrefab(_pavementSurfacePrefab);

            _pedestrianPathPrefab = FindVisiblePedestrianPath(out var visibleName);
            if (_pedestrianPathPrefab != Entity.Null)
            {
                _usesSurfaceFallback = false;
                _selectedPathPrefabName = visibleName;
                MeasureSelectedPathWidth(_pedestrianPathPrefab);
                Mod.Log.Info($"ParkManager uses visible vanilla path '{visibleName}'; "
                    + $"measured width {_selectedPathWidth:F2} m; separate "
                    + "rectangular surfaces are disabled.");
                return true;
            }

            _usesSurfaceFallback = true;
            _selectedPathPrefabName = FallbackPedestrianPathPrefabName
                + " + " + PavementSurfacePrefabName;
            _pedestrianPathPrefab = FindNamedBuiltin(_pathPrefabQuery,
                FallbackPedestrianPathPrefabName, false);
            if (!HasNamedBuiltin(_pavementSurfacePrefab, PavementSurfacePrefabName)
                || !HasUsableAreaPrefab(_pavementSurfacePrefab))
                _pavementSurfacePrefab = FindNamedBuiltin(_surfacePrefabQuery,
                    PavementSurfacePrefabName, true);
            Mod.Log.Warn("ParkManager found no visible vanilla pedestrian path; "
                + "using the M0.3 compatibility surface fallback.");
            return _pedestrianPathPrefab != Entity.Null
                && _pavementSurfacePrefab != Entity.Null;
        }

        private void MeasureSelectedPathWidth(Entity prefab)
        {
            _selectedPathWidth = 4f;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<NetGeometryData>(prefab)) return;
            var width = EntityManager.GetComponentData<NetGeometryData>(prefab)
                .m_DefaultWidth;
            if (math.isfinite(width) && width > 0.5f)
                _selectedPathWidth = width;
        }

        private Entity FindVisiblePedestrianPath(out string selectedName)
        {
            selectedName = string.Empty;
            var best = Entity.Null;
            var bestScore = 0;
            var bestWidth = float.MaxValue;
            using var prefabs = _pathPrefabQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < prefabs.Length; i++)
            {
                var entity = prefabs[i];
                if (!_pathPrefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab)
                    || prefab == null || !prefab.isBuiltin) continue;
                var name = prefab.name ?? string.Empty;
                var lower = name.ToLowerInvariant();
                var score = 0;
                // In the current Vanilla asset set PedestrianPathWide01 is the
                // broad paved park path used by the established builder. The
                // similarly named narrow variant renders as a cycle path, so
                // names alone are not interchangeable here.
                if (string.Equals(name, "PedestrianPathWide01",
                        StringComparison.OrdinalIgnoreCase)) score += 2000;
                if (string.Equals(name, "Pedestrian Path",
                        StringComparison.OrdinalIgnoreCase)) score += 1000;
                if (string.Equals(name, "Pedestrian Pathway",
                        StringComparison.OrdinalIgnoreCase)) score += 900;
                if (lower.Contains("pedestrian")) score += 180;
                if (lower.Contains("path")) score += 80;
                if (lower.Contains("invisible")) score -= 1000;
                if (lower.Contains("bridge") || lower.Contains("pier")
                    || lower.Contains("covered") || lower.Contains("cable")
                    || lower.Contains("arc") || lower.Contains("subway")
                    || lower.Contains("harbor")) score -= 500;

                var width = float.MaxValue;
                if (EntityManager.HasComponent<NetGeometryData>(entity))
                {
                    width = EntityManager.GetComponentData<NetGeometryData>(entity)
                        .m_DefaultWidth;
                    if (math.isfinite(width) && width > 0.5f)
                    {
                        // Prefer the established broad park pavement as the
                        // geometric fallback when internal names change.
                        score += 300 - (int)math.round(math.abs(width - 8f) * 30f);
                        if (width < 5f) score -= 250;
                    }
                }

                var betterTie = score == bestScore
                    && (math.abs(width - 8f) < math.abs(bestWidth - 8f) - 0.01f
                        || math.abs(math.abs(width - 8f)
                            - math.abs(bestWidth - 8f)) <= 0.01f
                        && string.Compare(name, selectedName,
                            StringComparison.OrdinalIgnoreCase) < 0);
                if (score < bestScore || score == bestScore && !betterTie) continue;
                bestScore = score;
                bestWidth = width;
                best = entity;
                selectedName = name;
            }
            return best;
        }

        private Entity FindNamedBuiltin(EntityQuery query, string name, bool surface)
        {
            using var prefabs = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < prefabs.Length; i++)
            {
                var entity = prefabs[i];
                if (!_pathPrefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab)
                    || prefab == null || !prefab.isBuiltin
                    || !string.Equals(prefab.name, name,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (surface && !HasUsableAreaPrefab(entity)) continue;
                Mod.Log.Info($"ParkManager path placement uses '{name}'.");
                return entity;
            }
            Mod.Log.Warn($"ParkManager is waiting for vanilla prefab '{name}'.");
            return Entity.Null;
        }

        private bool HasNamedBuiltin(Entity entity, string name)
        {
            return entity != Entity.Null && EntityManager.Exists(entity)
                && _pathPrefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab)
                && prefab != null && prefab.isBuiltin
                && string.Equals(prefab.name, name, StringComparison.OrdinalIgnoreCase);
        }

        private bool HasBuiltinEntity(Entity entity)
            => entity != Entity.Null && EntityManager.Exists(entity)
                && _pathPrefabSystem.TryGetPrefab<PrefabBase>(entity, out var prefab)
                && prefab != null && prefab.isBuiltin;

        private bool HasUsableAreaPrefab(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)
                || !EntityManager.HasComponent<AreaData>(entity)) return false;
            return EntityManager.GetComponentData<AreaData>(entity).m_Archetype.Valid;
        }

        private Entity CreatePathBuildRecord(int seed)
        {
            var record = EntityManager.CreateEntity();
            // A PrefabRef keeps this otherwise headless record in the normal
            // savegame entity stream. It has no Node/Edge/Area/Transform and
            // therefore cannot masquerade as a selectable path element.
            EntityManager.AddComponentData(record, new PrefabRef
            {
                m_Prefab = _pedestrianPathPrefab,
            });
            EntityManager.AddComponentData(record, new ParkPathBuildMarker { Seed = seed });
            EntityManager.AddComponentData(record, new ProceduralSiteBuilder
            {
                Version = ProceduralSiteBuilder.CurrentVersion,
                Kind = ProceduralSiteKind.Park,
            });
            EntityManager.AddComponentData(record, new ParkEditableBuildState
            {
                Version = ParkEditableBuildState.CurrentVersion,
            });
            WriteBuildReceipt(record, seed);
            return record;
        }

        private float3 WorldPathPoint(int nodeIndex, float2 point,
            ref TerrainHeightData heightData, Dictionary<int, float> heights)
        {
            if (!heights.TryGetValue(nodeIndex, out var height))
            {
                height = TerrainUtils.SampleHeight(ref heightData,
                    new float3(point.x, 0f, point.y));
                if (!math.isfinite(height))
                    throw new InvalidOperationException("Terrainhöhe ist nicht endlich.");
                heights[nodeIndex] = height;
            }
            return new float3(point.x, height, point.y);
        }

        private bool CreatePathCourse(float3 a, float3 b,
            ref Unity.Mathematics.Random random)
        {
            var length = math.distance(a, b);
            if (length < 1f) return false;
            var curve = NetUtils.StraightCurve(a, b);
            var definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = _pedestrianPathPrefab,
                m_RandomSeed = random.NextInt(),
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponentData(definition, new NetCourse
            {
                m_Curve = curve,
                m_Length = length,
                m_FixedIndex = -1,
                m_Elevation = float2.zero,
                m_StartPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = a,
                    m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)),
                    m_CourseDelta = 0f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsFirst,
                    m_ParentMesh = -1,
                    m_SplitPosition = 0f,
                },
                m_EndPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = b,
                    m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)),
                    m_CourseDelta = 1f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsLast,
                    m_ParentMesh = -1,
                    m_SplitPosition = 0f,
                },
            });
            return true;
        }

        private bool CreatePathSurface(float2 a, float2 b, float width,
            ref TerrainHeightData heightData)
        {
            var delta = b - a;
            var length = math.length(delta);
            if (length < 1f) return false;
            var normal = new float2(-delta.y, delta.x) / length * (width * 0.5f);
            var polygon = new[] { a - normal, b - normal, b + normal, a + normal };
            var definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = _pavementSurfacePrefab,
            });
            EntityManager.AddComponent<Updated>(definition);
            var nodes = EntityManager.AddBuffer<Game.Areas.Node>(definition);
            nodes.ResizeUninitialized(5);
            for (var i = 0; i < 4; i++)
            {
                var point = polygon[i];
                var height = TerrainUtils.SampleHeight(ref heightData,
                    new float3(point.x, 0f, point.y));
                nodes[i] = new Game.Areas.Node(new float3(point.x, height, point.y),
                    float.MinValue);
            }
            nodes[4] = nodes[0];
            return true;
        }

        private int CountOwnTempEntities(EntityQuery query, Entity prefab)
        {
            var count = 0;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
                if (EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab
                    == prefab) count++;
            return count;
        }

        private int CountOwnTempEdges(EntityQuery query, Entity prefab)
        {
            var count = 0;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (!EntityManager.HasComponent<Game.Net.Edge>(entity)
                    || EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != prefab) continue;
                count++;
            }
            return count;
        }

        private int TagEditableTempEntities(EntityQuery query, Entity prefab,
            Entity park, ref int nextElementId)
        {
            var count = 0;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != prefab) continue;
                var kind = EntityManager.HasComponent<Game.Net.Edge>(entity)
                    ? ParkPathMemberKind.Edge
                    : EntityManager.HasComponent<Game.Net.Node>(entity)
                        ? ParkPathMemberKind.Node
                        : ParkPathMemberKind.Surface;
                var member = new ParkPathMember
                {
                    Park = park,
                    ElementId = nextElementId++,
                    Kind = kind,
                };
                if (EntityManager.HasComponent<ParkPathMember>(entity))
                    EntityManager.SetComponentData(entity, member);
                else EntityManager.AddComponentData(entity, member);
                count++;
            }
            return count;
        }

        /// <summary>
        /// Remembers every permanent entity that already used the selected
        /// path/surface prefab before this build. CS2 may merge temporary nodes
        /// while applying a network, so entity counts and component transfer
        /// are not a stable identity mechanism. Anything appearing after this
        /// snapshot is a materialized result of the active build.
        /// </summary>
        private void CaptureMaterializationBaseline()
        {
            _pathEntityBaseline.Clear();
            _areaEntityBaseline.Clear();
            CapturePrefabBaseline(_permanentPathQuery, _pedestrianPathPrefab,
                _pathEntityBaseline);
            if (_usesSurfaceFallback)
                CapturePrefabBaseline(_permanentAreaQuery, _pavementSurfacePrefab,
                    _areaEntityBaseline);
        }

        private void CapturePrefabBaseline(EntityQuery query, Entity prefab,
            HashSet<Entity> baseline)
        {
            if (prefab == Entity.Null) return;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    == prefab) baseline.Add(entity);
            }
        }

        /// <summary>
        /// Rebuilds logical membership from the permanent graph after Apply.
        /// Edges are the invariant unit: one planned NetCourse must result in
        /// one permanent edge. Nodes are deliberately allowed to merge at
        /// junctions and therefore are only counted, never compared with the
        /// temporary node count.
        /// </summary>
        private void TagMaterializedPathEntities(Entity park, out int edgeCount,
            out int nodeCount, out int areaCount)
        {
            edgeCount = 0;
            nodeCount = 0;
            areaCount = 0;
            if (park == Entity.Null || !EntityManager.Exists(park)) return;

            var nextElementId = NextMemberElementId(park);
            using (var entities = _permanentPathQuery
                       .ToEntityArray(Allocator.TempJob))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                        != _pedestrianPathPrefab) continue;
                    var kind = EntityManager.HasComponent<Game.Net.Edge>(entity)
                        ? ParkPathMemberKind.Edge
                        : ParkPathMemberKind.Node;
                    if (!IsMaterializedBuildEntity(entity, park,
                            _pathEntityBaseline)) continue;
                    if (!SetMaterializedMember(entity, park, kind,
                            ref nextElementId)) continue;
                    if (kind == ParkPathMemberKind.Edge) edgeCount++;
                    else nodeCount++;
                }
            }

            if (!_usesSurfaceFallback) return;
            using var areas = _permanentAreaQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < areas.Length; i++)
            {
                var entity = areas[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != _pavementSurfacePrefab
                    || !IsMaterializedBuildEntity(entity, park,
                        _areaEntityBaseline)) continue;
                if (SetMaterializedMember(entity, park, ParkPathMemberKind.Surface,
                        ref nextElementId)) areaCount++;
            }
        }

        private bool IsMaterializedBuildEntity(Entity entity, Entity park,
            HashSet<Entity> baseline)
        {
            if (!baseline.Contains(entity)) return true;
            return EntityManager.HasComponent<ParkPathMember>(entity)
                && EntityManager.GetComponentData<ParkPathMember>(entity).Park
                == park;
        }

        private bool SetMaterializedMember(Entity entity, Entity park,
            ParkPathMemberKind kind, ref int nextElementId)
        {
            if (EntityManager.HasComponent<ParkPathMember>(entity))
            {
                var existing = EntityManager.GetComponentData<ParkPathMember>(entity);
                if (existing.Park != park) return false;
                if (existing.Kind != kind)
                {
                    existing.Kind = kind;
                    EntityManager.SetComponentData(entity, existing);
                }
                return true;
            }
            EntityManager.AddComponentData(entity, new ParkPathMember
            {
                Park = park,
                ElementId = nextElementId++,
                Kind = kind,
            });
            return true;
        }

        private int NextMemberElementId(Entity park)
        {
            var next = 1;
            using var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var member = EntityManager.GetComponentData<ParkPathMember>(
                    entities[i]);
                if (member.Park == park && member.ElementId >= next)
                    next = member.ElementId + 1;
            }
            return next;
        }

        private void FinalizeEditablePathBuild()
        {
            EnsureMembersAreTopLevel(_pendingBuildRecord);
            var geometryHash = ComputeMemberGeometryHash(_pendingBuildRecord,
                out var memberCount);
            var state = EntityManager.GetComponentData<ParkEditableBuildState>(
                _pendingBuildRecord);
            state.Version = ParkEditableBuildState.CurrentVersion;
            state.MemberCount = memberCount;
            state.GeometryHash = geometryHash;
            state.Modified = false;
            EntityManager.SetComponentData(_pendingBuildRecord, state);
            UpdateBuildReceipt(_pendingBuildRecord, false);

            _lastBuildRecord = _pendingBuildRecord;
            _pendingBuildRecord = Entity.Null;
            _pathEntityBaseline.Clear();
            _areaEntityBaseline.Clear();
            _lastBuildIsLegacy = false;
            _freshDraftActive = false;
            _pathBuildPhase = PathBuildPhase.Idle;
            _lastModificationCheckFrame = UnityEngine.Time.frameCount;
            PublishState($"Testwege gebaut: {_expectedPathCourses} verbundene "
                + $"Segmente mit '{_selectedPathPrefabName}'. Einzelne Knoten und "
                + "Segmente können jetzt extern bearbeitet werden.");
            PublishPathBuildState($"Gebaut · frei editierbar · {memberCount} Elemente · "
                + $"{_selectedPathPrefabName} · Seed {_pathPlan?.Seed ?? 0}");
            PublishWorkspaceState();
            Mod.Log.Info($"ParkManager built {_expectedPathCourses} pedestrian courses and "
                + $"{_expectedPathAreas} surfaces as {memberCount} top-level editable entities.");
        }

        private void EnsureMembersAreTopLevel(Entity park)
        {
            using var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                    != park || !EntityManager.HasComponent<Owner>(entity)) continue;
                EntityManager.RemoveComponent<Owner>(entity);
            }
        }

        private int CountMembers(Entity park)
        {
            if (park == Entity.Null) return 0;
            var count = 0;
            using var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
                if (EntityManager.GetComponentData<ParkPathMember>(entities[i]).Park
                    == park) count++;
            return count;
        }

        private int DeleteEditableMembers(Entity park)
        {
            if (park == Entity.Null) return 0;
            var removed = 0;
            using var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                    != park) continue;
                EntityManager.AddComponent<Deleted>(entity);
                removed++;
            }
            return removed;
        }

        private void AbortPathBuild(string reason)
        {
            Mod.Log.Warn("ParkManager path build aborted: " + reason);
            TagMaterializedPathEntities(_pendingBuildRecord,
                out var materializedEdges, out var materializedNodes,
                out var materializedAreas);
            DeleteEditableMembers(_pendingBuildRecord);
            if (_pendingBuildRecord != Entity.Null
                && EntityManager.Exists(_pendingBuildRecord)
                && !EntityManager.HasComponent<Deleted>(_pendingBuildRecord))
                EntityManager.AddComponent<Deleted>(_pendingBuildRecord);
            _pendingBuildRecord = Entity.Null;
            _freshDraftActive = true;
            _pathEntityBaseline.Clear();
            _areaEntityBaseline.Clear();
            applyMode = ApplyMode.Clear;
            _pathApplyFrame = UnityEngine.Time.frameCount;
            _pathBuildPhase = PathBuildPhase.ClearRequested;
            PublishState(reason);
            PublishPathBuildState("Wegebau wird verworfen …");
            PublishWorkspaceState();
            Mod.Log.Info($"ParkManager abort cleanup captured "
                + $"{materializedEdges} permanent edges, {materializedNodes} "
                + $"permanent nodes and {materializedAreas} permanent surfaces.");
        }

        private void PublishPathBuildState(string summary)
            => _ui?.SetPathBuildState(PathBuildBusy, HasBuiltPaths, summary);
    }
}
