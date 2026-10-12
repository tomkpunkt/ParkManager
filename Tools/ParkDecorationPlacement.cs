using System;
using System.Collections.Generic;
using Game.Areas;
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
        // About 2 km of fence at four-metre pieces is too short for large
        // parks; the cap only guards against degenerate outlines.
        private const int MaximumFenceObjects = 4000;

        private enum DecorationBuildPhase
        {
            Idle,
            WaitingForMaterialization,
            ApplyRequested,
            ClearRequested,
        }

        /// <summary>
        /// Expected permanent object recorded while a temporary creation
        /// definition is materialized. It lets the system match the resulting
        /// Vanilla entity back to its park, role and deterministic age stage.
        /// </summary>
        private sealed class PendingDecoration
        {
            internal Entity Prefab;
            internal float3 Position;
            internal ParkPathMemberKind Kind;
            internal int ElementId;
            internal byte AgeStage;
        }

        /// <summary>
        /// Lightweight snapshot used while matching materialized Vanilla
        /// objects. Positions are cached once so candidate searches do not
        /// repeatedly cross the EntityManager boundary.
        /// </summary>
        private readonly struct DecorationObjectCandidate
        {
            internal readonly Entity Entity;
            internal readonly Entity Prefab;
            internal readonly float3 Position;

            internal DecorationObjectCandidate(Entity entity, Entity prefab,
                float3 position)
            {
                Entity = entity;
                Prefab = prefab;
                Position = position;
            }
        }

        private ParkAssetCatalogSystem _assetCatalog;
        private EntityQuery _tempObjectQuery;
        private EntityQuery _permanentObjectQuery;
        private ParkDecorationPlan _decorationPlan;
        private bool _fenceEnabled;
        private bool _lakeEnabled = true;
        private int _vegetationDensity = 100;
        private int _furnitureDensity = 100;
        private int _decorationEnabledMask = 0x2f;
        private DecorationBuildPhase _decorationBuildPhase;
        private readonly List<PendingDecoration> _pendingDecorations
            = new List<PendingDecoration>();
        private Entity _pendingFencePrefab = Entity.Null;
        private int _expectedFenceCourses;
        private int _pendingFenceElementIdStart;
        private int _decorationStartedFrame;
        private int _decorationApplyFrame;
        private readonly HashSet<Entity> _decorationObjectBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _decorationFenceBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _capturedDecorationObjectPrefabs =
            new HashSet<Entity>();

        private bool DecorationBuildBusy
            => _decorationBuildPhase != DecorationBuildPhase.Idle;

        private bool IsBuildComplete => HasBuiltPaths
            && EntityManager.HasComponent<ParkCompletedBundle>(_lastBuildRecord);
        private bool HasDecorationObjects => HasBuiltPaths
            && CountDecorationMembers(_lastBuildRecord) > 0;
        private bool DecorationEditingLocked => IsBuildComplete || HasDecorationObjects;

        private void InitializeDecorationPlacement()
        {
            _assetCatalog = World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>();
            _tempObjectQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            _permanentObjectQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            PublishDecorationState(UiText.Of("decoration.none"));
        }

        internal void GenerateDecorations()
        {
            if (BuildBusy || DecorationEditingLocked)
            {
                PublishState(DecorationEditingLocked
                    ? UiText.Of("status.removeDecorationsToReplan")
                    : UiText.Of("status.buildBusy"));
                return;
            }
            if (IsPlaza
                && _plazaPlan != null)
            {
                // Keep the structure preview and only reroll the furnishings.
                if (_decorationPlan != null)
                    RollPlazaFurnishing(Seeds.NewSeed());
                ReplanPlazaArrangement();
                return;
            }
            GenerateDecorationPlan(Seeds.NewSeed());
        }

        internal void RefreshDecorationPlan()
        {
            if (BuildBusy || DecorationEditingLocked
                || _decorationPlan == null || _pathPlan == null) return;
            GenerateDecorationPlan(_decorationPlan.Seed);
        }

        internal void SetVegetationDensity(int density)
        {
            if (BuildBusy)
            {
                PublishState(UiText.Of("status.buildBusy"));
                return;
            }
            density = math.clamp(density, 25, 200);
            if (_vegetationDensity == density) return;
            if (DecorationEditingLocked)
            {
                PublishState(UiText.Of("status.removeDecorationsForPlantDensity"));
                PublishDecorationState(DecorationPlanSummary(_decorationPlan));
                return;
            }
            _vegetationDensity = density;
            if (_decorationPlan != null)
                GenerateDecorationPlan(_decorationPlan.Seed);
            else PublishDecorationState(UiText.Of("decoration.plantDensityChanged"));
        }

        internal void SetFurnitureDensity(int density)
        {
            if (BuildBusy) return;
            density = math.clamp(density, 25, 200);
            if (_furnitureDensity == density) return;
            if (DecorationEditingLocked)
            {
                PublishState(UiText.Of("status.removeDecorationsForFurnitureDensity"));
                return;
            }
            _furnitureDensity = density;
            if (IsPlaza
                && _plazaPlan != null) ReplanPlazaArrangement();
            else if (_decorationPlan != null) GenerateDecorationPlan(_decorationPlan.Seed);
            else PublishDecorationState(UiText.Of("decoration.furnitureDensityChanged"));
        }

        /// <summary>Plans (and later builds) a lake in the largest open space.</summary>
        internal void SetLakeEnabled(bool enabled)
        {
            if (_lakeEnabled == enabled) return;
            if (BuildBusy || DecorationEditingLocked)
            {
                PublishState(UiText.Of("status.lakeLocked"));
                _ui?.SetLakeEnabled(_lakeEnabled);
                return;
            }
            _lakeEnabled = enabled;
            _ui?.SetLakeEnabled(_lakeEnabled);
            if (_decorationPlan != null && !IsPlaza)
                GenerateDecorationPlan(_decorationPlan.Seed);
            PublishState(UiText.Of(enabled ? "status.lakeEnabled" : "status.lakeDisabled"));
        }

        internal void ToggleDecorationCategory(int kindValue)
        {
            if (BuildBusy || DecorationEditingLocked) return;
            if (kindValue < (int)ParkDecorationKind.Tree
                || kindValue > (int)ParkDecorationKind.TrashBin) return;
            var bit = 1 << (kindValue - 1);
            _decorationEnabledMask ^= bit;
            if (kindValue == (int)ParkDecorationKind.Fence)
                _fenceEnabled = (_decorationEnabledMask & bit) != 0;
            if (_decorationPlan != null) GenerateDecorationPlan(_decorationPlan.Seed);
            else PublishDecorationState(UiText.Of("decoration.selectionChanged"));
        }

        private void GenerateDecorationPlan(int seed)
        {
            if (!_plannerMode || _pathPlan == null
                || !IsPlaza && _pathPlan.Edges.Count == 0)
            {
                PublishState(UiText.Of("status.needPathNetwork"));
                return;
            }
            if (IsPlaza && _plazaPlan == null)
            {
                _decorationPlan = null;
                PublishDecorationState(UiText.Of("plaza.arrangementDoesNotFit"));
                return;
            }
            if (IsPlaza)
                _decorationPlan = GeneratePlazaDecorations(seed);
            else
            {
                // Resolve the real Vanilla net before furnishing. The procedural
                // edge width is only a design value and PedestrianPathWide01 is
                // considerably broader in the rendered game.
                ResolvePlacementPrefabs();
                _decorationPlan = ParkDecorationPlanner.Generate(_points,
                    _pathPlan, EntrancePoints(), seed, _selectedPathWidth,
                    _fenceEnabled, _vegetationDensity, _furnitureDensity,
                    _decorationEnabledMask, planLake: _lakeEnabled, planAnimals: true);
                FitFurnitureToSelectedAssets();
            }
            var summary = DecorationPlanSummary(_decorationPlan);
            PublishState(UiText.Of("status.decorationPlanned", summary));
            PublishDecorationState(summary);
        }

        internal void BuildDecorations()
        {
            if (BuildBusy)
            {
                PublishState(UiText.Of("status.buildBusy"));
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState(UiText.Of("status.buildPathsFirst"));
                return;
            }
            if (DecorationEditingLocked)
            {
                PublishState(UiText.Of("status.removeDecorationsBeforeRebuild"));
                return;
            }
            if (_decorationPlan == null)
            {
                PublishState(UiText.Of("status.planDecorationsFirst"));
                return;
            }
            if (IsPlaza && _plazaPlan == null)
            {
                PublishState(UiText.Of("plaza.arrangementDoesNotFit"));
                return;
            }
            if (IsPlaza && _plazaPlan.HasCenterpiece
                && !_assetCatalog.TryGetFittingPlazaCenter(_points,
                    out _, out _))
            {
                PublishState(UiText.Of("plaza.centerUnavailable"));
                return;
            }
            try
            {
                if (!ValidateDecorationSite(out var siteProblem))
                {
                    PublishState(siteProblem);
                    PublishDecorationState(siteProblem);
                    return;
                }
                _buildDefinitions.Clear();
                var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
                ResetPendingDecorationBuild();
                var nextElementId = NextMemberElementId(_lastBuildRecord);
                var heightData = _terrainSystem.GetHeightData(waitForPending: true);

                var random = new Unity.Mathematics.Random(
                    (uint)Math.Max(1, _decorationPlan.Seed));
                var fenceRandomSeed = random.NextInt();
                var fenceObjects = 0;
                // Adjacent fence courses must receive bit-identical heights at
                // their common polygon corner. Sampling every run separately
                // can differ by a tiny amount and makes CS2 materialize two
                // overlapping endpoint nodes instead of one shared node.
                var fenceHeights = new Dictionary<(long, long), float>();
                for (var i = 0; i < _decorationPlan.Placements.Count; i++)
                {
                    var placement = _decorationPlan.Placements[i];
                    if (!TryResolveParkAsset(placement, out var prefab))
                    {
                        if (IsPlaza)
                            throw new InvalidOperationException(
                                UiText.Of("error.arrangementAssetMissing"));
                        continue;
                    }
                    if (placement.Kind == ParkDecorationKind.Fence)
                    {
                        if (_assetCatalog.IsNetworkFence(prefab))
                        {
                            if (_pendingFencePrefab == Entity.Null)
                            {
                                _pendingFencePrefab = prefab;
                                Mod.Log.Info($"ParkManager BUILD-FENCE park={_lastBuildRecord} "
                                    + $"seed={_decorationPlan.Seed} prefab={prefab} "
                                    + $"name='{PrefabName(prefab)}'.");
                                CapturePrefabBaseline(_permanentPathQuery, prefab,
                                    _decorationFenceBaseline);
                            }
                            if (_pendingFencePrefab == prefab
                                && CreateFenceNetworkRun(placement, prefab,
                                    ref heightData, fenceHeights,
                                    fenceRandomSeed))
                                _expectedFenceCourses++;
                        }
                        else
                        {
                            CaptureDecorationObjectPrefab(prefab);
                            CreateFenceRunDefinitions(placement, prefab,
                                ref heightData, fenceRandomSeed, ref nextElementId,
                                ref fenceObjects);
                        }
                        continue;
                    }
                    CaptureDecorationObjectPrefab(prefab);
                    // CS2 uses CreationDefinition.m_RandomSeed for mesh/color
                    // variation. One fixed value and one selected prefab per
                    // furniture category keep a plaza visually consistent;
                    // the geometry seed remains free to vary its layout.
                    var appearanceSeed = IsPlaza
                        && IsPlazaColorFurniture(placement.Kind)
                        ? 1 : random.NextInt();
                    if (!CreateObjectDefinition(placement, prefab,
                        ref heightData, appearanceSeed, out var position))
                    {
                        if (IsPlaza)
                            throw new InvalidOperationException(
                                UiText.Of("error.arrangementPlacementFailed"));
                        continue;
                    }
                    _pendingDecorations.Add(new PendingDecoration
                    {
                        Prefab = prefab,
                        Position = position,
                        Kind = ToMemberKind(placement.Kind),
                        ElementId = nextElementId++,
                        AgeStage = placement.AgeStage,
                    });
                }

                CaptureDecorationObjectBaseline();

                _pendingFenceElementIdStart = nextElementId;

                var expectedMembers = _pendingDecorations.Count
                    + _expectedFenceCourses;
                if (expectedMembers == 0)
                {
                    if (_decorationPlan.Placements.Count == 0)
                    {
                        FinalizeDecorationBuild();
                        return;
                    }
                    throw new InvalidOperationException(
                        UiText.Of("error.noAssetClasses"));
                }

                _decorationStartedFrame = UnityEngine.Time.frameCount;
                ResetBuildValidation();
                _decorationBuildPhase = DecorationBuildPhase.WaitingForMaterialization;
                preparationTimer.Stop();
                Mod.Log.Info("ParkManager decoration palette: "
                    + _assetCatalog.GetParkPaletteName(_decorationPlan.Seed)
                    + $"; prepared {_pendingDecorations.Count} object definitions "
                    + $"in {preparationTimer.Elapsed.TotalMilliseconds:F1} ms.");
                PublishState(UiText.Of("status.decorationBuildStarted",
                    expectedMembers));
                PublishDecorationState(UiText.Of("decoration.materializing"));
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager could not create decoration definitions.");
                AbortDecorationBuild(UiText.Of("decoration.prepareFailed", exception.Message));
            }
        }

        private bool ProcessDecorationPlacement()
        {
            switch (_decorationBuildPhase)
            {
                case DecorationBuildPhase.Idle:
                    return false;
                case DecorationBuildPhase.WaitingForMaterialization:
                    applyMode = ApplyMode.None;
                    CollectGameErrors();
                    var temporaryTimer = System.Diagnostics.Stopwatch.StartNew();
                    var taggedObjects = TagMaterializedObjects();
                    var fenceReady = _pendingFencePrefab == Entity.Null
                        || CountOwnTempEdges(_tempPathQuery, _pendingFencePrefab)
                            >= _expectedFenceCourses;
                    if (taggedObjects >= _pendingDecorations.Count
                        && fenceReady)
                    {
                        TagMaterializedNetworkFence();
                        applyMode = ApplyMode.Apply;
                        _decorationApplyFrame = UnityEngine.Time.frameCount;
                        _decorationBuildPhase = DecorationBuildPhase.ApplyRequested;
                        temporaryTimer.Stop();
                        Mod.Log.Info("ParkManager decoration performance: matched "
                            + $"{taggedObjects} temporary objects in "
                            + $"{temporaryTimer.Elapsed.TotalMilliseconds:F1} ms.");
                        PublishDecorationState(UiText.Of("decoration.applying"));
                        return true;
                    }
                    if (UnityEngine.Time.frameCount - _decorationStartedFrame
                        <= MaterializationTimeoutFrames) return true;
                    AbortDecorationBuild(GameErrorOr(UiText.Of("decoration.timeout")));
                    return true;
                case DecorationBuildPhase.ApplyRequested:
                    applyMode = ApplyMode.None;
                    if (UnityEngine.Time.frameCount - _decorationApplyFrame
                        < GeometrySettleFrames) return true;
                    var permanentTimer = System.Diagnostics.Stopwatch.StartNew();
                    TagPermanentDecorationEntities(out var permanentObjects,
                        out var permanentFenceEdges,
                        out var permanentFenceNodes);
                    permanentTimer.Stop();
                    var permanentFenceReady = _pendingFencePrefab == Entity.Null
                        || permanentFenceEdges >= _expectedFenceCourses;
                    if (permanentObjects < _pendingDecorations.Count
                        || !permanentFenceReady)
                    {
                        if (UnityEngine.Time.frameCount - _decorationApplyFrame
                            <= MaterializationTimeoutFrames) return true;
                        AbortDecorationBuild(GameErrorOr(UiText.Of(
                            "decoration.incomplete", permanentObjects,
                            _pendingDecorations.Count, permanentFenceEdges,
                            _expectedFenceCourses, permanentFenceNodes)));
                        return true;
                    }
                    Mod.Log.Info("ParkManager rediscovered permanent decoration: "
                        + $"{permanentObjects}/{_pendingDecorations.Count} objects, "
                        + $"{permanentFenceEdges}/{_expectedFenceCourses} fence edges "
                        + $"and {permanentFenceNodes} fence nodes in "
                        + $"{permanentTimer.Elapsed.TotalMilliseconds:F1} ms.");
                    FinalizeDecorationBuild();
                    return true;
                case DecorationBuildPhase.ClearRequested:
                    applyMode = ApplyMode.Clear;
                    _decorationBuildPhase = DecorationBuildPhase.Idle;
                    PublishDecorationState(UiText.Of("decoration.discarded"));
                    return true;
                default:
                    return false;
            }
        }

        private bool TryResolveParkAsset(ParkDecorationPlacement placement,
            out Entity prefab)
        {
            ParkAssetCategory category;
            switch (placement.Kind)
            {
                case ParkDecorationKind.Tree:
                    category = ParkAssetCategory.Tree;
                    break;
                case ParkDecorationKind.Bush:
                    category = IsPlaza
                        ? ParkAssetCategory.PlazaPlanter
                        : ParkAssetCategory.Bush;
                    break;
                case ParkDecorationKind.Bench:
                    category = ParkAssetCategory.Bench;
                    break;
                case ParkDecorationKind.Lamp:
                    category = ParkAssetCategory.Lamp;
                    break;
                case ParkDecorationKind.Fence:
                    category = ParkAssetCategory.Fence;
                    break;
                case ParkDecorationKind.TrashBin:
                    category = ParkAssetCategory.TrashBin;
                    break;
                case ParkDecorationKind.PlazaCenter:
                    category = ParkAssetCategory.PlazaCenter;
                    break;
                case ParkDecorationKind.AnimalSpawner:
                    return TryResolveAnimalSpawner(out prefab);
                default:
                    prefab = Entity.Null;
                    return false;
            }
            if (category == ParkAssetCategory.PlazaCenter
                && _assetCatalog.TryGetFittingPlazaCenter(_points,
                    out prefab, out _))
                return true;
            if (IsPlaza
                && !string.IsNullOrEmpty(placement.ExplicitAssetName))
                return _assetCatalog.TryGetNamed(category,
                    placement.ExplicitAssetName, out prefab);
            if (IsPlaza
                && IsPlazaColorFurniture(placement.Kind)
                && _assetCatalog.TryGetSelected(category, out prefab, out _))
                return true;
            if (_assetCatalog.TryGetParkVariant(category, _decorationPlan.Seed,
                placement.Variant, out prefab, out _)) return true;
            if (category == ParkAssetCategory.PlazaPlanter
                && _assetCatalog.TryGetParkVariant(ParkAssetCategory.Bush,
                    _decorationPlan.Seed, placement.Variant, out prefab,
                    out _)) return true;
            Mod.Log.Warn($"ParkManager has no usable {category} prefab; layer skipped.");
            return false;
        }

        private const string AnimalSpawnerPrefabName = "Forest Animal Spawner";
        private Entity _animalSpawnerPrefab = Entity.Null;

        /// <summary>
        /// Resolves the Vanilla forest animal spawner marker. It is a
        /// placeholder prefab that must be placed as-is; the game's creature
        /// spawner system picks the animals.
        /// </summary>
        private bool TryResolveAnimalSpawner(out Entity prefab)
        {
            if (_animalSpawnerPrefab == Entity.Null
                || !EntityManager.Exists(_animalSpawnerPrefab))
            {
                _animalSpawnerPrefab = Entity.Null;
                if (_pathPrefabSystem.TryGetPrefab(new PrefabID(
                        nameof(MarkerObjectPrefab), AnimalSpawnerPrefabName),
                        out var spawner) && spawner != null)
                    _animalSpawnerPrefab = _pathPrefabSystem.GetEntity(spawner);
                else Mod.Log.Warn($"ParkManager found no '{AnimalSpawnerPrefabName}'; "
                    + "animal spawners are skipped.");
            }
            prefab = _animalSpawnerPrefab;
            return prefab != Entity.Null;
        }

        private static bool IsPlazaColorFurniture(ParkDecorationKind kind)
            => kind == ParkDecorationKind.Bench
                || kind == ParkDecorationKind.Lamp
                || kind == ParkDecorationKind.TrashBin
                || kind == ParkDecorationKind.PlazaCenter;

        /// <summary>
        /// Replaces the planner's conservative furniture footprint with the
        /// selected Vanilla prefab's actual path-normal collision extent. This
        /// leaves a 20 cm tolerance at the visible path edge: close enough to
        /// read as path furniture, but outside the overlap that gives
        /// overridable objects an invisible <see cref="Overridden"/> state.
        /// </summary>
        private void FitFurnitureToSelectedAssets()
        {
            if (_decorationPlan == null || _pathPlan == null) return;
            var adjusted = 0;
            var missingBounds = 0;
            var minimumRadius = float.MaxValue;
            var maximumRadius = 0f;
            for (var i = _decorationPlan.Placements.Count - 1; i >= 0; i--)
            {
                var placement = _decorationPlan.Placements[i];
                if (placement.Kind != ParkDecorationKind.Bench
                    && placement.Kind != ParkDecorationKind.Lamp
                    && placement.Kind != ParkDecorationKind.TrashBin) continue;
                if (!TryResolveParkAsset(placement, out var prefab)
                    || !_assetCatalog.TryGetPathNormalRadius(prefab,
                        out var footprintRadius))
                {
                    missingBounds++;
                    continue;
                }
                if (!TryGetNearestPathPoint(placement.Position,
                    out var pathPoint, out var pathWidth)) continue;

                var direction = placement.Position - pathPoint;
                var distance = math.length(direction);
                if (distance < 0.001f) continue;
                var desired = math.max(pathWidth, _selectedPathWidth) * 0.5f
                    + footprintRadius + 0.2f;
                placement.Position = pathPoint + direction / distance * desired;
                _decorationPlan.Placements[i] = placement;
                minimumRadius = math.min(minimumRadius, footprintRadius);
                maximumRadius = math.max(maximumRadius, footprintRadius);
                adjusted++;
            }
            if (adjusted > 0)
                Mod.Log.Info($"ParkManager fitted {adjusted} path-furniture objects to "
                    + "the path edge using prefab collision bounds "
                    + $"({minimumRadius:F2}-{maximumRadius:F2} m normal radius, "
                    + "0.20 m safety gap)." + (missingBounds > 0
                        ? $" Missing bounds for {missingBounds} placements."
                        : string.Empty));
        }

        private bool TryGetNearestPathPoint(float2 point, out float2 closest,
            out float width)
        {
            closest = default;
            width = 0f;
            var best = float.MaxValue;
            if (_pathPlan == null) return false;
            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                var candidate = PolygonMath.ClosestPointOnSegment(point,
                    _pathPlan.Nodes[edge.A].Position,
                    _pathPlan.Nodes[edge.B].Position);
                var distance = math.distancesq(point, candidate);
                if (!(distance < best)) continue;
                best = distance;
                closest = candidate;
                width = edge.Width;
            }
            return best < float.MaxValue;
        }

        private bool CreateObjectDefinition(ParkDecorationPlacement placement,
            Entity prefab, ref TerrainHeightData heightData,
            int randomSeed, out float3 position)
        {
            position = new float3(placement.Position.x, 0f, placement.Position.y);
            position.y = TerrainUtils.SampleHeight(ref heightData, position);
            if (!math.all(math.isfinite(position))) return false;
            var definition = CreateBuildDefinition();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_RandomSeed = randomSeed,
            });
            EntityManager.AddComponent<Updated>(definition);
            var scale = 1f;
            if (placement.Kind == ParkDecorationKind.Tree)
                scale = math.clamp(placement.Size / 6.5f, 0.82f, 1.28f);
            else if (placement.Kind == ParkDecorationKind.Bush)
                scale = math.clamp(placement.Size / 3.2f, 0.75f, 1.2f);
            var data = default(ObjectDefinition);
            data.m_Position = position;
            // Bench meshes use their local X axis as their visual length while
            // the overlay and path tangent use local Z as forward. Rotating all
            // path furniture by a quarter turn aligns the built bench with its
            // preview and makes directional lamps face the path.
            var rotation = placement.Rotation;
            if ((placement.Kind == ParkDecorationKind.Bench
                    && !IsPlaza)
                || placement.Kind == ParkDecorationKind.Lamp
                || placement.Kind == ParkDecorationKind.TrashBin)
                rotation += math.PI * 0.5f;
            data.m_Rotation = quaternion.RotateY(rotation);
            data.m_Probability = 100;
            data.m_PrefabSubIndex = -1;
            data.m_Scale = scale;
            data.m_Intensity = 1f;
            data.m_ParentMesh = -1;
            data.m_Age = placement.Kind == ParkDecorationKind.Tree
                ? TreeAgeValue(placement.AgeStage) : 0f;
            data.m_IsDecoration = false;
            EntityManager.AddComponentData(definition, data);
            return true;
        }

        private void CreateFenceRunDefinitions(ParkDecorationPlacement run,
            Entity prefab, ref TerrainHeightData heightData, int randomSeed,
            ref int nextElementId, ref int fenceObjects)
        {
            if (run.Size < 0.5f || fenceObjects >= MaximumFenceObjects) return;
            if (!_assetCatalog.TryGetLongitudinalBounds(prefab,
                out var minimum, out var maximum))
            {
                minimum = -2f;
                maximum = 2f;
                Mod.Log.Warn("ParkManager could not read fence mesh bounds; "
                    + "using a four-metre line-tool fallback.");
            }

            var spacing = maximum - minimum;
            if (!(spacing > 0.1f) || !math.isfinite(spacing)) return;
            var forward = new float2(math.sin(run.Rotation),
                math.cos(run.Rotation));
            var start = run.Position - forward * (run.Size * 0.5f);
            var distance = -minimum;
            var endDistance = run.Size - maximum;

            while (distance < endDistance + 0.001f
                && fenceObjects < MaximumFenceObjects)
            {
                if (AddFenceDefinition(run, prefab, start + forward * distance,
                    ref heightData, randomSeed, ref nextElementId))
                    fenceObjects++;
                distance += spacing;
            }

            // Match the Advanced Line Tool fence-mode end treatment: when the
            // line length is not an exact mesh multiple, overlap only the final
            // piece enough to close the visible gap at the endpoint.
            if (distance < run.Size - minimum
                && fenceObjects < MaximumFenceObjects)
            {
                if (AddFenceDefinition(run, prefab,
                    start + forward * (endDistance + 0.001f),
                    ref heightData, randomSeed, ref nextElementId))
                    fenceObjects++;
            }
        }

        /// <summary>
        /// Creates one native fence-network course for a complete boundary run.
        /// The fence prefab repeats and bends its mesh internally, so the ECS
        /// result is a normal editable network edge rather than hundreds of
        /// adjacent prop entities.
        /// </summary>
        private bool CreateFenceNetworkRun(ParkDecorationPlacement run,
            Entity prefab, ref TerrainHeightData heightData,
            Dictionary<(long, long), float> heights, int randomSeed)
        {
            if (run.Size < 0.5f) return false;
            var forward = new float2(math.sin(run.Rotation),
                math.cos(run.Rotation));
            var half = forward * (run.Size * 0.5f);
            var start = CanonicalFenceEndpoint(run.Position - half,
                ref heightData, heights);
            var end = CanonicalFenceEndpoint(run.Position + half,
                ref heightData, heights);
            if (!math.all(math.isfinite(start)) || !math.all(math.isfinite(end)))
                return false;

            CreateNetCourseDefinition(prefab, NetUtils.StraightCurve(start, end),
                math.distance(start, end), randomSeed);
            return true;
        }

        /// <summary>
        /// Samples each logical fence endpoint once. A 2.5 cm key absorbs the
        /// harmless float reconstruction error introduced by storing a run as
        /// centre, rotation and length while keeping distinct nearby gate ends
        /// separate. Shared corners therefore become one Vanilla network node.
        /// </summary>
        private static float3 CanonicalFenceEndpoint(float2 point,
            ref TerrainHeightData heightData,
            Dictionary<(long, long), float> heights)
        {
            var key = ((long)math.round(point.x * 40f),
                (long)math.round(point.y * 40f));
            var canonical = new float3(key.Item1 / 40f, 0f,
                key.Item2 / 40f);
            if (!heights.TryGetValue(key, out var height))
            {
                height = TerrainUtils.SampleHeight(ref heightData, canonical);
                heights[key] = height;
            }
            canonical.y = height;
            return canonical;
        }

        private bool AddFenceDefinition(ParkDecorationPlacement run,
            Entity prefab, float2 point, ref TerrainHeightData heightData,
            int randomSeed, ref int nextElementId)
        {
            var piece = run;
            piece.Position = point;
            if (!CreateObjectDefinition(piece, prefab, ref heightData,
                randomSeed, out var position)) return false;
            _pendingDecorations.Add(new PendingDecoration
            {
                Prefab = prefab,
                Position = position,
                Kind = ParkPathMemberKind.Fence,
                ElementId = nextElementId++,
                AgeStage = 0,
            });
            return true;
        }

        private int TagMaterializedObjects()
        {
            BuildDecorationObjectIndex(_tempObjectQuery, null,
                out var candidates, out var ownedByElementId, out _, out _);
            var used = new HashSet<Entity>();
            var tagged = 0;
            for (var pendingIndex = 0; pendingIndex < _pendingDecorations.Count;
                pendingIndex++)
            {
                var pending = _pendingDecorations[pendingIndex];
                var best = FindDecorationObject(pending, candidates,
                    ownedByElementId, used, 0.36f);
                if (best == Entity.Null) continue;
                used.Add(best);
                if (!EntityManager.HasComponent<ParkPathMember>(best))
                    EntityManager.AddComponentData(best, new ParkPathMember
                    {
                        Park = _lastBuildRecord,
                        ElementId = pending.ElementId,
                        Kind = pending.Kind,
                    });
                tagged++;
            }
            return tagged;
        }

        private int TagMaterializedNetworkFence()
        {
            if (_pendingFencePrefab == Entity.Null) return 0;
            var tagged = 0;
            var nextElementId = _pendingFenceElementIdStart;
            using var entities = _tempPathQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != _pendingFencePrefab) continue;
                SetMember(entity, new ParkPathMember
                {
                    Park = _lastBuildRecord,
                    ElementId = nextElementId++,
                    Kind = ParkPathMemberKind.Fence,
                });
                tagged++;
            }
            Mod.Log.Info($"ParkManager native fence network: "
                + $"{_expectedFenceCourses} courses materialized as {tagged} "
                + "editable node/edge entities.");
            return tagged;
        }

        /// <summary>
        /// Captures each selected object prefab before its first creation
        /// definition. Permanent decoration entities are rediscovered from
        /// these baselines after Apply because CS2 may replace temporary
        /// entities instead of preserving custom membership components.
        /// </summary>
        private void CaptureDecorationObjectPrefab(Entity prefab)
        {
            if (prefab != Entity.Null && _capturedDecorationObjectPrefabs.Add(prefab))
                Mod.Log.Info($"ParkManager BUILD-ASSET park={_lastBuildRecord} "
                    + $"seed={_decorationPlan?.Seed} prefab={prefab} "
                    + $"name='{PrefabName(prefab)}'.");
        }

        /// <summary>
        /// Captures all pre-existing objects for every selected prefab in one
        /// query pass. The previous implementation repeated the full-city scan
        /// once per prefab, which made preparation increasingly expensive.
        /// </summary>
        private void CaptureDecorationObjectBaseline()
        {
            if (_capturedDecorationObjectPrefabs.Count == 0) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var entities = _permanentObjectQuery
                .ToEntityArray(Allocator.TempJob);
            using var prefabs = _permanentObjectQuery
                .ToComponentDataArray<PrefabRef>(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                if (_capturedDecorationObjectPrefabs.Contains(prefabs[i].m_Prefab))
                    _decorationObjectBaseline.Add(entities[i]);
            }
            timer.Stop();
            Mod.Log.Info("ParkManager decoration performance: captured "
                + $"{_decorationObjectBaseline.Count} baseline objects for "
                + $"{_capturedDecorationObjectPrefabs.Count} prefabs from "
                + $"{entities.Length} city objects in "
                + $"{timer.Elapsed.TotalMilliseconds:F1} ms.");
        }

        private void TagPermanentDecorationEntities(out int objectCount,
            out int fenceEdgeCount, out int fenceNodeCount)
        {
            objectCount = TagPermanentDecorationObjects();
            TagPermanentDecorationFence(out fenceEdgeCount, out fenceNodeCount);
        }

        private int TagPermanentDecorationObjects()
        {
            if (_pendingDecorations.Count == 0) return 0;
            BuildDecorationObjectIndex(_permanentObjectQuery,
                _decorationObjectBaseline, out var candidates,
                out var ownedByElementId, out var scanned, out var candidateCount);
            var used = new HashSet<Entity>();
            var tagged = 0;
            for (var pendingIndex = 0; pendingIndex < _pendingDecorations.Count;
                pendingIndex++)
            {
                var pending = _pendingDecorations[pendingIndex];
                var best = FindDecorationObject(pending, candidates,
                    ownedByElementId, used, 1f);
                if (best == Entity.Null) continue;
                used.Add(best);
                if (EntityManager.HasComponent<ParkPathMember>(best))
                {
                    var existing = EntityManager.GetComponentData<ParkPathMember>(best);
                    if (existing.Park != _lastBuildRecord
                        || existing.ElementId != pending.ElementId) continue;
                    if (existing.Kind != pending.Kind)
                    {
                        existing.Kind = pending.Kind;
                        EntityManager.SetComponentData(best, existing);
                    }
                }
                else EntityManager.AddComponentData(best, new ParkPathMember
                {
                    Park = _lastBuildRecord,
                    ElementId = pending.ElementId,
                    Kind = pending.Kind,
                });
                tagged++;
            }
            Mod.Log.Info("ParkManager decoration performance: indexed "
                + $"{candidateCount} nearby candidates from {scanned} permanent "
                + $"city objects for {_pendingDecorations.Count} planned objects.");
            return tagged;
        }

        /// <summary>
        /// Builds a prefab and one-metre spatial index in a single query pass.
        /// This changes materialization matching from O(planned × city objects)
        /// to O(city objects + planned × local neighbours).
        /// </summary>
        private void BuildDecorationObjectIndex(EntityQuery query,
            HashSet<Entity> excluded,
            out Dictionary<Entity, Dictionary<long,
                List<DecorationObjectCandidate>>> candidates,
            out Dictionary<int, DecorationObjectCandidate> ownedByElementId,
            out int scanned, out int candidateCount)
        {
            candidates = new Dictionary<Entity, Dictionary<long,
                List<DecorationObjectCandidate>>>();
            ownedByElementId = new Dictionary<int, DecorationObjectCandidate>();
            candidateCount = 0;
            using var entities = query.ToEntityArray(Allocator.TempJob);
            using var prefabs = query.ToComponentDataArray<PrefabRef>(Allocator.TempJob);
            using var transforms = query
                .ToComponentDataArray<Game.Objects.Transform>(Allocator.TempJob);
            scanned = entities.Length;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var prefab = prefabs[i].m_Prefab;
                if (!_capturedDecorationObjectPrefabs.Contains(prefab)
                    || excluded != null && excluded.Contains(entity)) continue;

                var candidate = new DecorationObjectCandidate(entity, prefab,
                    transforms[i].m_Position);
                if (EntityManager.HasComponent<ParkPathMember>(entity))
                {
                    var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                    if (member.Park == _lastBuildRecord)
                        ownedByElementId[member.ElementId] = candidate;
                    continue;
                }

                if (!candidates.TryGetValue(prefab, out var cells))
                {
                    cells = new Dictionary<long, List<DecorationObjectCandidate>>();
                    candidates.Add(prefab, cells);
                }
                var key = DecorationObjectCell(candidate.Position.xz);
                if (!cells.TryGetValue(key, out var bucket))
                {
                    bucket = new List<DecorationObjectCandidate>();
                    cells.Add(key, bucket);
                }
                bucket.Add(candidate);
                candidateCount++;
            }
        }

        private Entity FindDecorationObject(PendingDecoration pending,
            Dictionary<Entity, Dictionary<long,
                List<DecorationObjectCandidate>>> candidates,
            Dictionary<int, DecorationObjectCandidate> ownedByElementId,
            HashSet<Entity> used, float maximumDistanceSquared)
        {
            if (ownedByElementId.TryGetValue(pending.ElementId, out var owned)
                && owned.Prefab == pending.Prefab && !used.Contains(owned.Entity))
                return owned.Entity;
            if (!candidates.TryGetValue(pending.Prefab, out var cells))
                return Entity.Null;

            var cellX = (int)math.floor(pending.Position.x);
            var cellZ = (int)math.floor(pending.Position.z);
            var best = Entity.Null;
            var bestDistance = maximumDistanceSquared;
            for (var x = cellX - 1; x <= cellX + 1; x++)
            for (var z = cellZ - 1; z <= cellZ + 1; z++)
            {
                if (!cells.TryGetValue(DecorationObjectCell(x, z), out var bucket))
                    continue;
                for (var i = 0; i < bucket.Count; i++)
                {
                    var candidate = bucket[i];
                    if (used.Contains(candidate.Entity)) continue;
                    var distance = math.distancesq(candidate.Position.xz,
                        pending.Position.xz);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = candidate.Entity;
                }
            }
            return best;
        }

        private static long DecorationObjectCell(float2 position)
            => DecorationObjectCell((int)math.floor(position.x),
                (int)math.floor(position.y));

        private static long DecorationObjectCell(int x, int z)
            => ((long)x << 32) | (uint)z;

        private void TagPermanentDecorationFence(out int edgeCount,
            out int nodeCount)
        {
            edgeCount = 0;
            nodeCount = 0;
            if (_pendingFencePrefab == Entity.Null) return;
            var nextElementId = NextMemberElementId(_lastBuildRecord);
            using var entities = _permanentPathQuery
                .ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                        != _pendingFencePrefab
                    || !IsMaterializedBuildEntity(entity, _lastBuildRecord,
                        _decorationFenceBaseline)
                    || !SetMaterializedMember(entity, _lastBuildRecord,
                        ParkPathMemberKind.Fence, ref nextElementId)) continue;
                if (EntityManager.HasComponent<Game.Net.Edge>(entity)) edgeCount++;
                else if (EntityManager.HasComponent<Game.Net.Node>(entity)) nodeCount++;
            }
        }

        /// <summary>Forgets everything tracked for one furnishing build attempt.</summary>
        private void ResetPendingDecorationBuild()
        {
            _pendingDecorations.Clear();
            _pendingFencePrefab = Entity.Null;
            _expectedFenceCourses = 0;
            _decorationObjectBaseline.Clear();
            _decorationFenceBaseline.Clear();
            _capturedDecorationObjectPrefabs.Clear();
        }

        private void FinalizeDecorationBuild()
        {
            Mod.Log.Info($"ParkManager finalizing decoration for {_lastBuildRecord}, seed {_decorationPlan?.Seed}.");
            EnsureMembersAreTopLevel(_lastBuildRecord);
            ApplyPermanentTreeAges();
            LogFurnitureVisibility(_lastBuildRecord);
            RefreshEditableBaseline(_lastBuildRecord);
            var count = CountDecorationMembers(_lastBuildRecord);
            _buildDefinitions.Clear();
            ResetPendingDecorationBuild();
            _decorationBuildPhase = DecorationBuildPhase.Idle;
            _preflightWarning = null;
            _buildIssues.Clear();
            MarkCompleted(_lastBuildRecord);
            PublishState(UiText.Of("status.decorationsBuilt", count));
            PublishDecorationState(UiText.Of("decoration.built",
                DecorationPlanSummary(_decorationPlan)));
            Mod.Log.Info($"ParkManager built {count} top-level decoration entities.");
            StartLakeBuild();
        }

        private void LogFurnitureVisibility(Entity park)
        {
            // Per kind: total, Overridden and Hidden counts.
            var kinds = new[] { ParkPathMemberKind.Bench, ParkPathMemberKind.Lamp,
                ParkPathMemberKind.TrashBin };
            var counts = new int[kinds.Length, 3];
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                var index = Array.IndexOf(kinds, member.Kind);
                if (member.Park != park || index < 0) continue;
                counts[index, 0]++;
                if (EntityManager.HasComponent<Overridden>(entity)) counts[index, 1]++;
                if (EntityManager.HasComponent<Hidden>(entity)) counts[index, 2]++;
            }
            string Describe(string label, int index)
                => $"{label} {counts[index, 0]} (Overridden {counts[index, 1]}, "
                    + $"Hidden {counts[index, 2]})";
            Mod.Log.Info("ParkManager furniture visibility: "
                + Describe("benches", 0) + "; " + Describe("lamps", 1) + "; "
                + Describe("trash bins", 2) + "; "
                + $"path width {_selectedPathWidth:F2} m; terrain-snapped height.");
        }

        private void ApplyPermanentTreeAges()
        {
            var stages = new Dictionary<int, byte>();
            for (var i = 0; i < _pendingDecorations.Count; i++)
            {
                var pending = _pendingDecorations[i];
                if (pending.Kind == ParkPathMemberKind.Tree)
                    stages[pending.ElementId] = pending.AgeStage;
            }
            if (stages.Count == 0) return;

            var applied = 0;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                if (member.Park != _lastBuildRecord
                    || member.Kind != ParkPathMemberKind.Tree
                    || !stages.TryGetValue(member.ElementId, out var stage)
                    || !EntityManager.HasComponent<Game.Objects.Tree>(entity))
                    continue;
                var tree = new Game.Objects.Tree
                {
                    m_State = (Game.Objects.TreeState)(stage == 0
                        ? 0 : 1 << (stage - 1)),
                    m_Growth = 128,
                };
                EntityManager.SetComponentData(entity, tree);
                if (!EntityManager.HasComponent<BatchesUpdated>(entity))
                    EntityManager.AddComponent<BatchesUpdated>(entity);
                applied++;
            }
            Mod.Log.Info($"ParkManager applied deterministic age stages to "
                + $"{applied}/{stages.Count} permanent trees.");
        }

        private static float TreeAgeValue(byte stage)
        {
            switch (stage)
            {
                case 0: return 0.05f;
                case 1: return 0.175f;
                case 2: return 0.425f;
                default: return 0.775f;
            }
        }

        private void RefreshEditableBaseline(Entity park)
        {
            if (park == Entity.Null || !EntityManager.Exists(park)
                || !EntityManager.HasComponent<ParkEditableBuildState>(park)) return;
            var hash = ComputeMemberGeometryHash(park, out var count);
            var state = EntityManager.GetComponentData<ParkEditableBuildState>(park);
            state.MemberCount = count;
            state.GeometryHash = hash;
            state.Modified = false;
            EntityManager.SetComponentData(park, state);
        }

        private int CountDecorationMembers(Entity park)
        {
            if (park == Entity.Null) return 0;
            var count = 0;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var member = EntityManager.GetComponentData<ParkPathMember>(members[i]);
                if (member.Park == park && IsDecorationKind(member.Kind)) count++;
            }
            return count;
        }

        private int DeleteDecorationMembers(Entity park)
        {
            if (EntityManager.Exists(park)
                && EntityManager.HasComponent<ParkCompletedBundle>(park))
                EntityManager.RemoveComponent<ParkCompletedBundle>(park);
            var removed = 0;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                if (member.Park != park || !IsDecorationKind(member.Kind)) continue;
                EntityManager.AddComponent<Deleted>(entity);
                removed++;
            }
            return removed;
        }

        private static bool IsDecorationKind(ParkPathMemberKind kind)
            => kind >= ParkPathMemberKind.Tree
                    && kind <= ParkPathMemberKind.PlazaCenter
                || kind == ParkPathMemberKind.AnimalSpawner;

        private static ParkPathMemberKind ToMemberKind(ParkDecorationKind kind)
        {
            switch (kind)
            {
                case ParkDecorationKind.Tree: return ParkPathMemberKind.Tree;
                case ParkDecorationKind.Bush: return ParkPathMemberKind.Bush;
                case ParkDecorationKind.Bench: return ParkPathMemberKind.Bench;
                case ParkDecorationKind.Lamp: return ParkPathMemberKind.Lamp;
                case ParkDecorationKind.Fence: return ParkPathMemberKind.Fence;
                case ParkDecorationKind.TrashBin: return ParkPathMemberKind.TrashBin;
                case ParkDecorationKind.PlazaCenter: return ParkPathMemberKind.PlazaCenter;
                case ParkDecorationKind.AnimalSpawner: return ParkPathMemberKind.AnimalSpawner;
                default: return ParkPathMemberKind.Bush;
            }
        }

        private string DecorationPlanSummary(ParkDecorationPlan plan)
        {
            if (plan == null) return UiText.Of("decoration.none");
            if (IsPlaza)
            {
                var centerName = _plazaPlan?.HasCenterpiece == true
                    ? _assetCatalog.GetSelectedPlazaCenterName()
                    : UiText.Of("decoration.noCenter");
                return UiText.Of("decoration.plazaSummary", plan.Seed, centerName,
                    plan.Count(ParkDecorationKind.Bench),
                    plan.Count(ParkDecorationKind.Lamp),
                    plan.Count(ParkDecorationKind.TrashBin));
            }
            return UiText.Of("decoration.parkSummary",
                _assetCatalog.GetParkPaletteName(plan.Seed), plan.Seed,
                plan.Count(ParkDecorationKind.Tree),
                plan.Count(ParkDecorationKind.Bush),
                plan.Count(ParkDecorationKind.Bench),
                plan.Count(ParkDecorationKind.Lamp),
                plan.Count(ParkDecorationKind.TrashBin),
                plan.FenceEnabled
                    ? UiText.Of("decoration.fenceRuns",
                        plan.Count(ParkDecorationKind.Fence))
                    : UiText.Of("decoration.noFence"));
        }

        private void AbortDecorationBuild(string reason)
        {
            _preflightWarning = null;
            Mod.Log.Warn($"ParkManager decoration build aborted in "
                + $"{_decorationBuildPhase} (seed {_decorationPlan?.Seed ?? 0}, "
                + $"expected {_pendingDecorations.Count} objects/"
                + $"{_expectedFenceCourses} fence courses): {reason}");
            TagPermanentDecorationEntities(out var permanentObjects,
                out var permanentFenceEdges,
                out var permanentFenceNodes);
            DeleteDecorationMembers(_lastBuildRecord);
            var discardedDefinitions = DiscardBuildDefinitions();
            ResetPendingDecorationBuild();
            applyMode = ApplyMode.Clear;
            _decorationBuildPhase = DecorationBuildPhase.ClearRequested;
            PublishState(reason);
            PublishDecorationState(UiText.Of("decoration.discarding"));
            Mod.Log.Info("ParkManager decoration abort cleanup captured "
                + $"{permanentObjects} permanent objects, "
                + $"{permanentFenceEdges} permanent fence edges and "
                + $"{permanentFenceNodes} permanent fence nodes; discarded "
                + $"{discardedDefinitions} definitions.");
        }

        private void DecorationBuildWasRemoved()
        {
            ResetPendingDecorationBuild();
            _decorationBuildPhase = DecorationBuildPhase.Idle;
            PublishDecorationState(_decorationPlan == null
                ? UiText.Of("decoration.none")
                : UiText.Of("decoration.plannedNotBuilt",
                    DecorationPlanSummary(_decorationPlan)));
        }

        private void PublishDecorationState(string summary)
            => _ui?.SetDecorationState(_vegetationDensity,
                _furnitureDensity, _decorationEnabledMask,
                _decorationPlan != null, DecorationBuildBusy || LakeBuildBusy,
                IsBuildComplete, summary);
    }
}
