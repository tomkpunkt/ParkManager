using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using ParkManager.Assets;
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
        private Entity _pedestrianPathPrefab = Entity.Null;
        private Entity _pavementSurfacePrefab = Entity.Null;
        private Entity _parkSurfacePrefab = Entity.Null;
        private bool _usesSurfaceFallback;
        private string _selectedPathPrefabName = string.Empty;
        private float _selectedPathWidth = 4f;
        private ParkPathType _selectedPathType = ParkPathType.Wide;
        private Entity _pendingBuildRecord = Entity.Null;
        private Entity _lastBuildRecord = Entity.Null;
        private PathBuildPhase _pathBuildPhase;
        private int _pathBuildStartedFrame;
        private int _pathApplyFrame;
        private int _expectedPathCourses;
        private int _expectedPathAreas;
        private int _expectedParkSurfaceAreas;
        private int _expectedParkAccessMarkers;
        private int _lastModificationCheckFrame;
        private readonly HashSet<Entity> _pathEntityBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _areaEntityBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _parkSurfaceAreaBaseline =
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
            InitializePlazaAccessPlacement();
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
        }

        internal void BuildPaths()
        {
            if (PathBuildBusy)
            {
                PublishState("Der aktuelle Wegebau wird noch von CS2 verarbeitet.");
                return;
            }
            if (_selectedSiteKind == ProceduralSiteKind.Plaza)
            {
                BuildPlazaAccess();
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
            if (!ResolvePedestrianAccessMarkerPrefab())
            {
                PublishState("Fußgänger-Zugangsmarker fehlt; der Park wird nicht ohne Zugang gebaut.");
                return;
            }
            if (!_assetCatalog.TryGetSelected(ParkAssetCategory.Surface,
                    out _parkSurfacePrefab, out _))
            {
                PublishState("Kein sichtbarer Vanilla-Untergrund verfügbar.");
                return;
            }
            try
            {
                if (!ValidateBuildSite(out var siteProblem))
                {
                    PublishState(siteProblem);
                    PublishPathBuildState(siteProblem, PathBuildStatus.Error);
                    return;
                }
                _buildDefinitions.Clear();
                CaptureMaterializationBaseline();
                _plazaAccessBaseline.Clear();
                CapturePrefabBaseline(_permanentPlazaAccessQuery,
                    _plazaAccessPrefab, _plazaAccessBaseline);
                _pendingBuildRecord = CreatePathBuildRecord(_pathPlan.Seed);
                var heightData = _terrainSystem.GetHeightData(waitForPending: true);
                var heights = new Dictionary<int, float>();
                var randomSeed = (uint)Math.Max(1, _pathPlan.Seed);
                var random = new Unity.Mathematics.Random(randomSeed);
                _expectedPathCourses = 0;
                _expectedPathAreas = 0;
                _expectedParkSurfaceAreas = 0;
                var courses = BuildMaterializedPathCourses(ref heightData,
                    heights, out var chainCount);
                for (var i = 0; i < courses.Count; i++)
                    if (CreatePathCourse(courses[i].Curve, courses[i].Length,
                            ref random)) _expectedPathCourses++;
                for (var i = 0; i < _pathPlan.Edges.Count; i++)
                {
                    var edge = _pathPlan.Edges[i];
                    var a2 = _pathPlan.Nodes[edge.A].Position;
                    var b2 = _pathPlan.Nodes[edge.B].Position;
                    if (_usesSurfaceFallback
                        && CreatePathSurface(a2, b2, edge.Width, ref heightData))
                        _expectedPathAreas++;
                }
                if (!CreateParkSurface(_parkSurfacePrefab, ref heightData))
                    throw new InvalidOperationException(
                        "Der gewählte Untergrund konnte nicht vorbereitet werden.");
                _expectedParkSurfaceAreas = 1;
                _expectedParkAccessMarkers = 0;
                for (var i = 0; i < _entrances.Count; i++)
                {
                    if (!CreatePedestrianAccessMarker(_entrances[i], ref heightData))
                        throw new InvalidOperationException(
                            $"Zugangsmarker für Parkeingang {i + 1} konnte nicht platziert werden.");
                    _expectedParkAccessMarkers++;
                }

                if (_expectedPathCourses == 0
                    || _usesSurfaceFallback && _expectedPathAreas == 0)
                    throw new InvalidOperationException("Der Plan enthält keine baubaren Segmente.");

                LogPlannedPathDiagnostics();
                LogMaterializedCourseDiagnostics(courses, chainCount);

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

        internal void SetPathType(int value)
        {
            if (_selectedSiteKind == ProceduralSiteKind.Plaza) return;
            if (PathBuildBusy || HasBuiltPaths)
            {
                PublishState("Der Wegtyp kann nach dem Wegebau nicht mehr geändert werden.");
                return;
            }
            var type = value == (int)ParkPathType.Narrow
                ? ParkPathType.Narrow : ParkPathType.Wide;
            if (_selectedPathType == type) return;
            _selectedPathType = type;
            _pedestrianPathPrefab = Entity.Null;
            _selectedPathPrefabName = string.Empty;
            ResolvePlacementPrefabs();
            var decorationSeed = _decorationPlan?.Seed ?? 0;
            if (decorationSeed != 0 && _pathPlan != null)
                GenerateDecorationPlan(decorationSeed);
            else
                _decorationPlan = null;
            _ui?.SetPathType((int)_selectedPathType);
            PublishDecorationState(decorationSeed != 0
                ? "Ausstattung an die neue Wegbreite angepasst."
                : "Wegtyp geändert; Ausstattung noch nicht geplant.");
            PublishState(type == ParkPathType.Narrow
                ? "Schmale Fußwege ausgewählt."
                : "Breite Fußwege ausgewählt.");
        }

        internal void RemoveBuiltPaths()
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Entfernen ist erst nach Abschluss des Baues möglich.");
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState("Es sind keine von ParkManager gebauten Testwege vorhanden.");
                PublishPathBuildState("Noch keine Testwege gebaut.");
                return;
            }

            var removed = DeleteEditableMembers(_lastBuildRecord);
            EntityManager.AddComponent<Deleted>(_lastBuildRecord);
            _lastBuildRecord = Entity.Null;
            DecorationBuildWasRemoved();
            PublishState($"Gebauter Park entfernt ({removed} Teile).");
            PublishPathBuildState("Noch kein Park gebaut.");
        }

        private bool ProcessPathPlacement()
        {
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _pathBuildPhase != PathBuildPhase.Idle)
                return ProcessPlazaAccessPlacement();
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
                    var parkSurface = FindParkSurfaceArea(_tempAreaQuery,
                        _parkSurfacePrefab);
                    var accessMarkers = CountOwnTempEntities(
                        _tempPlazaAccessQuery, _plazaAccessPrefab);
                    if (pathParts >= _expectedPathCourses
                        && areas >= _expectedPathAreas
                        && parkSurface != Entity.Null
                        && accessMarkers >= _expectedParkAccessMarkers)
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
                        var attachedParkSurface = SetMaterializedMember(
                            parkSurface, _pendingBuildRecord,
                            ParkPathMemberKind.ParkSurface, ref nextElementId);
                        var attachedMarkers = TagPlazaTempEntities(
                            _tempPlazaAccessQuery, _plazaAccessPrefab,
                            ParkPathMemberKind.AccessMarker, ref nextElementId);
                        if (attachedPaths < _expectedPathCourses
                            || attachedAreas < _expectedPathAreas
                            || !attachedParkSurface
                            || attachedMarkers < _expectedParkAccessMarkers)
                            return true;

                        LogTemporaryPathDiagnostics(_pedestrianPathPrefab);
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
                        out var permanentAreas, out var permanentParkSurfaces);
                    var permanentMarkers = TagMaterializedAccessMarkers(
                        _pendingBuildRecord);
                    if (permanentEdges < _expectedPathCourses
                        || permanentAreas < _expectedPathAreas
                        || permanentParkSurfaces < _expectedParkSurfaceAreas
                        || permanentMarkers < _expectedParkAccessMarkers)
                    {
                        if (UnityEngine.Time.frameCount - _pathApplyFrame
                            <= MaterializationTimeoutFrames) return true;
                        AbortPathBuild("Das materialisierte Wegenetz blieb "
                            + $"unvollständig: {permanentEdges}/{_expectedPathCourses} "
                            + $"Kanten, {permanentNodes} Knoten und "
                            + $"{permanentAreas}/{_expectedPathAreas} Wegflächen und "
                            + $"{permanentParkSurfaces}/{_expectedParkSurfaceAreas} Untergründe, "
                            + $"{permanentMarkers}/{_expectedParkAccessMarkers} Zugangsmarker.");
                        return true;
                    }
                    Mod.Log.Info($"ParkManager rediscovered the permanent path graph: "
                        + $"{permanentEdges}/{_expectedPathCourses} edges, "
                        + $"{permanentNodes} merged nodes and "
                        + $"{permanentAreas}/{_expectedPathAreas} path surfaces and "
                        + $"{permanentParkSurfaces} park surface.");
                    LogPermanentPathDiagnostics(_pendingBuildRecord);
                    LogPlazaAccessConnections(_pendingBuildRecord);
                    FinalizeEditablePathBuild();
                    return true;
                case PathBuildPhase.ClearRequested:
                    applyMode = ApplyMode.Clear;
                    _pathBuildPhase = PathBuildPhase.Idle;
                    return true;
                default:
                    return false;
            }
        }

        private bool ResolvePlacementPrefabs()
        {
            if (_selectedSiteKind == ProceduralSiteKind.Plaza)
                return ResolvePlazaAccessPrefabs();
            if (HasBuiltinEntity(_pedestrianPathPrefab))
                return !_usesSurfaceFallback
                    || HasUsableAreaPrefab(_pavementSurfacePrefab);

            _pedestrianPathPrefab = FindVisiblePedestrianPath(_selectedPathType,
                out var visibleName);
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

        private Entity FindVisiblePedestrianPath(ParkPathType pathType,
            out string selectedName)
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
                var targetWidth = pathType == ParkPathType.Wide ? 8f : 4f;
                if (pathType == ParkPathType.Wide
                    && string.Equals(name, "PedestrianPathWide01",
                        StringComparison.OrdinalIgnoreCase)) score += 2000;
                if (pathType == ParkPathType.Narrow
                    && string.Equals(name, "Pavement Path",
                        StringComparison.OrdinalIgnoreCase)) score += 2500;
                // The bike patch added several PathwayData prefabs with widths
                // close to the narrow footpath. Width scoring alone must never
                // turn the park's pedestrian-path choice into a cycle path.
                if (lower.Contains("bike") || lower.Contains("bicycle"))
                    score -= 3000;
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
                        score += 300 - (int)math.round(
                            math.abs(width - targetWidth) * 30f);
                        if (pathType == ParkPathType.Wide && width < 5f)
                            score -= 250;
                        if (pathType == ParkPathType.Narrow && width > 6f)
                            score -= 250;
                    }
                }

                var betterTie = score == bestScore
                    && (math.abs(width - targetWidth)
                            < math.abs(bestWidth - targetWidth) - 0.01f
                        || math.abs(math.abs(width - targetWidth)
                            - math.abs(bestWidth - targetWidth)) <= 0.01f
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
                m_Prefab = _selectedSiteKind == ProceduralSiteKind.Plaza
                    ? _plazaNavigationPrefab : _pedestrianPathPrefab,
            });
            EntityManager.AddComponentData(record, new ParkPathBuildMarker { Seed = seed });
            EntityManager.AddComponentData(record, new ParkEditableBuildState
            {
                Version = ParkEditableBuildState.CurrentVersion,
            });
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

        private bool CreatePathCourse(Bezier4x3 curve, float length,
            ref Unity.Mathematics.Random random)
        {
            var a = curve.a;
            var b = curve.d;
            if (length < 1f) return false;
            var definition = CreateBuildDefinition();
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
            var definition = CreateBuildDefinition();
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
                if (EntityManager.HasComponent<Game.Areas.Area>(entity)
                    && IsParkSurfaceArea(entity)) continue;
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
            _parkSurfaceAreaBaseline.Clear();
            CapturePrefabBaseline(_permanentAreaQuery, _parkSurfacePrefab,
                _parkSurfaceAreaBaseline);
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
            out int nodeCount, out int areaCount, out int parkSurfaceCount)
        {
            edgeCount = 0;
            nodeCount = 0;
            areaCount = 0;
            parkSurfaceCount = 0;
            if (park == Entity.Null || !EntityManager.Exists(park)) return;

            var nextElementId = NextMemberElementId(park);
            using (var entities = _permanentPathQuery
                       .ToEntityArray(Allocator.TempJob))
            {
                // Edges establish ownership first. The second pass can then
                // safely adopt a baseline node that CS2 reused from an older
                // broken cleanup, but only when every live connection belongs
                // to this new park. Existing street/gate nodes stay external.
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                            != _pedestrianPathPrefab
                        || !EntityManager.HasComponent<Game.Net.Edge>(entity))
                        continue;
                    if (!IsMaterializedBuildEntity(entity, park,
                            _pathEntityBaseline)) continue;
                    if (!SetMaterializedMember(entity, park,
                            ParkPathMemberKind.Edge,
                            ref nextElementId)) continue;
                    edgeCount++;
                }

                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                            != _pedestrianPathPrefab
                        || !EntityManager.HasComponent<Game.Net.Node>(entity))
                        continue;
                    if (!IsMaterializedBuildEntity(entity, park,
                            _pathEntityBaseline)
                        && !IsReusedOrphanNodeForPark(entity, park)) continue;
                    if (!SetMaterializedMember(entity, park,
                            ParkPathMemberKind.Node,
                            ref nextElementId)) continue;
                    nodeCount++;
                }
            }

            using var areas = _permanentAreaQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < areas.Length; i++)
            {
                var entity = areas[i];
                var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (prefab == _parkSurfacePrefab && IsParkSurfaceArea(entity)
                    && IsMaterializedBuildEntity(entity, park,
                        _parkSurfaceAreaBaseline))
                {
                    if (SetMaterializedMember(entity, park,
                            ParkPathMemberKind.ParkSurface, ref nextElementId))
                        parkSurfaceCount++;
                }
                else if (_usesSurfaceFallback
                    && prefab == _pavementSurfacePrefab
                    && IsMaterializedBuildEntity(entity, park,
                        _areaEntityBaseline)
                    && SetMaterializedMember(entity, park,
                        ParkPathMemberKind.Surface, ref nextElementId))
                    areaCount++;
            }
        }

        private Entity FindParkSurfaceArea(EntityQuery query, Entity prefab)
        {
            if (prefab == Entity.Null) return Entity.Null;
            using var areas = query.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < areas.Length; i++)
            {
                var entity = areas[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                        == prefab && IsParkSurfaceArea(entity)) return entity;
            }
            return Entity.Null;
        }

        private bool IsParkSurfaceArea(Entity area)
        {
            if (EntityManager.GetComponentData<PrefabRef>(area).m_Prefab
                != _parkSurfacePrefab) return false;
            // Only geometry-match when the chosen ground happens to use the
            // same prefab as the compatibility path-surface fallback.
            if (_parkSurfacePrefab != _pavementSurfacePrefab) return true;
            if (_points.Count < 3
                || !EntityManager.HasBuffer<Game.Areas.Node>(area)) return false;
            var nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
            if (nodes.Length < _points.Count) return false;
            for (var i = 0; i < _points.Count; i++)
            {
                var found = false;
                for (var j = 0; j < nodes.Length; j++)
                {
                    var position = nodes[j].m_Position;
                    if (math.distance(new float2(position.x, position.z),
                            _points[i]) > 0.4f) continue;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        private bool IsMaterializedBuildEntity(Entity entity, Entity park,
            HashSet<Entity> baseline)
        {
            if (!baseline.Contains(entity)) return true;
            return EntityManager.HasComponent<ParkPathMember>(entity)
                && EntityManager.GetComponentData<ParkPathMember>(entity).Park
                == park;
        }

        private bool IsReusedOrphanNodeForPark(Entity node, Entity park)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            var ownConnections = 0;
            var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (var i = 0; i < connected.Length; i++)
            {
                var edge = connected[i].m_Edge;
                if (edge == Entity.Null || !EntityManager.Exists(edge)
                    || EntityManager.HasComponent<Deleted>(edge)) continue;
                if (!EntityManager.HasComponent<ParkPathMember>(edge)
                    || EntityManager.GetComponentData<ParkPathMember>(edge).Park
                        != park) return false;
                ownConnections++;
            }
            if (ownConnections == 0) return false;
            Mod.Log.Info($"ParkManager adopted reused orphan path node {node} "
                + $"into park {park} with {ownConnections} owned connections.");
            return true;
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

            _lastBuildRecord = _pendingBuildRecord;
            _pendingBuildRecord = Entity.Null;
            _buildDefinitions.Clear();
            _pathEntityBaseline.Clear();
            _areaEntityBaseline.Clear();
            _parkSurfaceAreaBaseline.Clear();
            ClearPlazaAccessBaseline();
            _pathBuildPhase = PathBuildPhase.Idle;
            _preflightWarning = null;
            _buildIssues.Clear();
            _lastModificationCheckFrame = UnityEngine.Time.frameCount;
            if (_selectedSiteKind == ProceduralSiteKind.Plaza)
            {
                PublishState("Plaza-Fläche und Zugänge gebaut.");
                PublishPathBuildState($"Plaza gebaut · {memberCount} Elemente · "
                    + $"Seed {_pathPlan?.Seed ?? 0}");
            }
            else
            {
                PublishState($"Testwege gebaut: {_expectedPathCourses} verbundene "
                    + $"Segmente mit '{_selectedPathPrefabName}'. Einzelne Knoten und "
                    + "Segmente können jetzt extern bearbeitet werden.");
                PublishPathBuildState($"Gebaut · frei editierbar · {memberCount} Elemente · "
                    + $"{_selectedPathPrefabName} · Seed {_pathPlan?.Seed ?? 0}");
            }
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
            _preflightWarning = null;
            Mod.Log.Warn($"ParkManager path build aborted in {_pathBuildPhase} "
                + $"(seed {_pathPlan?.Seed ?? 0}, expected {_expectedPathCourses} "
                + $"courses/{_expectedPathAreas} path areas): {reason}");
            if (_selectedSiteKind == ProceduralSiteKind.Plaza)
                TagMaterializedPlazaAccess(_pendingBuildRecord,
                    out _, out _, out _);
            else TagMaterializedAccessMarkers(_pendingBuildRecord);
            TagMaterializedPathEntities(_pendingBuildRecord,
                out var materializedEdges, out var materializedNodes,
                out var materializedAreas, out var materializedParkSurfaces);
            LogPermanentPathDiagnostics(_pendingBuildRecord);
            DeleteEditableMembers(_pendingBuildRecord);
            var discardedDefinitions = DiscardBuildDefinitions();
            if (_pendingBuildRecord != Entity.Null
                && EntityManager.Exists(_pendingBuildRecord)
                && !EntityManager.HasComponent<Deleted>(_pendingBuildRecord))
                EntityManager.AddComponent<Deleted>(_pendingBuildRecord);
            _pendingBuildRecord = Entity.Null;
            _pathEntityBaseline.Clear();
            _areaEntityBaseline.Clear();
            _parkSurfaceAreaBaseline.Clear();
            ClearPlazaAccessBaseline();
            applyMode = ApplyMode.Clear;
            _pathApplyFrame = UnityEngine.Time.frameCount;
            _pathBuildPhase = PathBuildPhase.ClearRequested;
            PublishState(reason);
            PublishPathBuildState("Fehler: " + reason, PathBuildStatus.Error);
            Mod.Log.Info($"ParkManager abort cleanup captured "
                + $"{materializedEdges} permanent edges, {materializedNodes} "
                + $"permanent nodes, {materializedAreas} path surfaces and "
                + $"{materializedParkSurfaces} park surfaces; discarded "
                + $"{discardedDefinitions} definitions.");
        }

        private void PublishPathBuildState(string summary,
            PathBuildStatus status = PathBuildStatus.Ok)
        {
            if (_preflightWarning != null && status == PathBuildStatus.Ok)
            {
                summary = _preflightWarning;
                status = PathBuildStatus.Warning;
            }
            _ui?.SetPathBuildState(PathBuildBusy, HasBuiltPaths, summary, status);
        }
    }
}
