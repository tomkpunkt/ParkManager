using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
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
        private const int MaximumFenceObjects = 500;

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
        private int _vegetationDensity = 100;
        private DecorationBuildPhase _decorationBuildPhase;
        private readonly List<PendingDecoration> _pendingDecorations
            = new List<PendingDecoration>();
        private Entity _pendingSurfacePrefab = Entity.Null;
        private int _pendingSurfaceElementId;
        private Entity _pendingFencePrefab = Entity.Null;
        private int _expectedFenceCourses;
        private int _pendingFenceElementIdStart;
        private int _decorationStartedFrame;
        private int _decorationApplyFrame;
        private int _expectedDecorationMembers;
        private readonly HashSet<Entity> _decorationObjectBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _decorationAreaBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _decorationFenceBaseline =
            new HashSet<Entity>();
        private readonly HashSet<Entity> _capturedDecorationObjectPrefabs =
            new HashSet<Entity>();

        private bool DecorationBuildBusy
            => _decorationBuildPhase != DecorationBuildPhase.Idle;

        private bool HasBuiltDecorations
            => HasBuiltPaths && CountDecorationMembers(_lastBuildRecord) > 0;

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
            PublishDecorationState("Noch keine Ausstattung geplant.");
        }

        internal void GenerateDecorations()
        {
            if (PathBuildBusy || DecorationBuildBusy || HasBuiltDecorations)
            {
                PublishState(HasBuiltDecorations
                    ? "Zum Neuplanen zuerst die gebaute Ausstattung entfernen."
                    : "Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            var seed = Guid.NewGuid().GetHashCode() & int.MaxValue;
            if (seed == 0) seed = 1;
            GenerateDecorationPlan(seed);
        }

        internal void RefreshDecorationPlan()
        {
            if (PathBuildBusy || DecorationBuildBusy || HasBuiltDecorations
                || _decorationPlan == null || _pathPlan == null) return;
            GenerateDecorationPlan(_decorationPlan.Seed);
        }

        internal void ToggleFence()
        {
            if (PathBuildBusy || DecorationBuildBusy || HasBuiltDecorations)
            {
                PublishState(HasBuiltDecorations
                    ? "Zum Ändern des Zauns zuerst die Ausstattung entfernen."
                    : "Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            _fenceEnabled = !_fenceEnabled;
            if (_pathPlan != null)
            {
                var seed = _decorationPlan?.Seed
                    ?? unchecked(_pathPlan.Seed * 1103515245 + 12345) & int.MaxValue;
                if (seed == 0) seed = 1;
                GenerateDecorationPlan(seed);
            }
            else PublishDecorationState(_fenceEnabled
                ? "Zaun aktiviert · zuerst Wege planen."
                : "Zaun deaktiviert · zuerst Wege planen.");
        }

        internal void SetVegetationDensity(int density)
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            density = math.clamp(density, 25, 200);
            if (_vegetationDensity == density) return;
            if (HasBuiltDecorations)
            {
                PublishState("Zum Ändern der Pflanzendichte zuerst die Ausstattung entfernen.");
                PublishDecorationState(DecorationPlanSummary(_decorationPlan));
                return;
            }
            _vegetationDensity = density;
            if (_decorationPlan != null)
                GenerateDecorationPlan(_decorationPlan.Seed);
            else PublishDecorationState("Pflanzendichte geändert · Ausstattung noch nicht geplant.");
        }

        private void GenerateDecorationPlan(int seed)
        {
            if (!_plannerMode || _pathPlan == null || _pathPlan.Edges.Count == 0)
            {
                PublishState("Zuerst ein Wegenetz im Parkplaner erzeugen.");
                return;
            }
            var gates = new List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++) gates.Add(_entrances[i].xz);
            // Resolve the real Vanilla net before furnishing. The procedural
            // edge width is only a design value and PedestrianPathWide01 is
            // considerably broader in the rendered game.
            ResolvePlacementPrefabs();
            _decorationPlan = ParkDecorationPlanner.Generate(_points, _pathPlan,
                gates, seed, _selectedPathWidth, _fenceEnabled,
                _vegetationDensity);
            FitFurnitureToSelectedAssets();
            UpdateBuildReceipt(_lastBuildRecord);
            var summary = DecorationPlanSummary(_decorationPlan);
            PublishState("Ausstattungsvariante geplant: " + summary);
            PublishDecorationState(summary);
        }

        internal void BuildDecorations()
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState("Zuerst die geplanten Wege bauen.");
                return;
            }
            if (HasBuiltDecorations)
            {
                PublishState("Vor dem Neubau zuerst die vorhandene Ausstattung entfernen.");
                return;
            }
            if (_decorationPlan == null)
            {
                PublishState("Zuerst eine Ausstattungsvariante planen.");
                return;
            }

            try
            {
                var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
                _pendingDecorations.Clear();
                _pendingSurfacePrefab = Entity.Null;
                _pendingSurfaceElementId = 0;
                _pendingFencePrefab = Entity.Null;
                _expectedFenceCourses = 0;
                _pendingFenceElementIdStart = 0;
                ClearDecorationMaterializationBaselines();
                var nextElementId = NextElementId(_lastBuildRecord);
                var heightData = _terrainSystem.GetHeightData(waitForPending: true);

                if (_assetCatalog.TryGetSelected(ParkAssetCategory.Surface,
                    out var surfacePrefab, out _))
                {
                    CapturePrefabBaseline(_permanentAreaQuery, surfacePrefab,
                        _decorationAreaBaseline);
                    if (CreateParkSurface(surfacePrefab, ref heightData))
                    {
                        _pendingSurfacePrefab = surfacePrefab;
                        _pendingSurfaceElementId = nextElementId++;
                    }
                }

                var random = new Unity.Mathematics.Random(
                    (uint)Math.Max(1, _decorationPlan.Seed));
                var fenceRandomSeed = random.NextInt();
                var fenceObjects = 0;
                for (var i = 0; i < _decorationPlan.Placements.Count; i++)
                {
                    var placement = _decorationPlan.Placements[i];
                    if (!TryResolveParkAsset(placement, out var prefab)) continue;
                    if (placement.Kind == ParkDecorationKind.Fence)
                    {
                        if (_assetCatalog.IsNetworkFence(prefab))
                        {
                            if (_pendingFencePrefab == Entity.Null)
                            {
                                _pendingFencePrefab = prefab;
                                CapturePrefabBaseline(_permanentPathQuery, prefab,
                                    _decorationFenceBaseline);
                            }
                            if (_pendingFencePrefab == prefab
                                && CreateFenceNetworkRun(placement, prefab,
                                    ref heightData, fenceRandomSeed))
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
                    if (!CreateObjectDefinition(placement, prefab,
                        ref heightData, random.NextInt(), out var position)) continue;
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

                _expectedDecorationMembers = _pendingDecorations.Count
                    + (_pendingSurfacePrefab == Entity.Null ? 0 : 1)
                    + _expectedFenceCourses;
                if (_expectedDecorationMembers == 0)
                    throw new InvalidOperationException(
                        "Keine der gewählten Vanilla-Assetklassen ist verfügbar.");

                _decorationStartedFrame = UnityEngine.Time.frameCount;
                _decorationBuildPhase = DecorationBuildPhase.WaitingForMaterialization;
                preparationTimer.Stop();
                Mod.Log.Info("ParkManager decoration palette: "
                    + _assetCatalog.GetParkPaletteName(_decorationPlan.Seed)
                    + $"; prepared {_pendingDecorations.Count} object definitions "
                    + $"in {preparationTimer.Elapsed.TotalMilliseconds:F1} ms.");
                PublishState($"Ausstattungsbau gestartet: {_expectedDecorationMembers} Elemente.");
                PublishDecorationState("CS2 materialisiert Parkfläche und Ausstattung …");
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager could not create decoration definitions.");
                AbortDecorationBuild("Ausstattung konnte nicht vorbereitet werden: "
                    + exception.Message);
            }
        }

        internal void RemoveBuiltDecorations()
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Entfernen ist erst nach Abschluss des Baues möglich.");
                return;
            }
            if (!HasBuiltDecorations)
            {
                PublishState("Es ist keine Parkausstattung gebaut.");
                return;
            }
            var removed = DeleteDecorationMembers(_lastBuildRecord);
            RefreshEditableBaseline(_lastBuildRecord);
            UpdateBuildReceipt(_lastBuildRecord, false);
            PublishState($"Parkfläche und Ausstattung entfernt ({removed} Elemente); Wege bleiben bestehen.");
            PublishDecorationState("Geplant · noch nicht gebaut · "
                + DecorationPlanSummary(_decorationPlan));
        }

        private bool ProcessDecorationPlacement()
        {
            switch (_decorationBuildPhase)
            {
                case DecorationBuildPhase.Idle:
                    return false;
                case DecorationBuildPhase.WaitingForMaterialization:
                    applyMode = ApplyMode.None;
                    var temporaryTimer = System.Diagnostics.Stopwatch.StartNew();
                    var taggedObjects = TagMaterializedObjects();
                    var taggedSurface = _pendingSurfacePrefab == Entity.Null
                        || TagMaterializedSurface();
                    var fenceReady = _pendingFencePrefab == Entity.Null
                        || CountOwnTempEdges(_tempPathQuery, _pendingFencePrefab)
                            >= _expectedFenceCourses;
                    if (taggedObjects >= _pendingDecorations.Count && taggedSurface
                        && fenceReady)
                    {
                        var taggedFence = TagMaterializedNetworkFence();
                        _expectedDecorationMembers = _pendingDecorations.Count
                            + (_pendingSurfacePrefab == Entity.Null ? 0 : 1)
                            + taggedFence;
                        applyMode = ApplyMode.Apply;
                        _decorationApplyFrame = UnityEngine.Time.frameCount;
                        _decorationBuildPhase = DecorationBuildPhase.ApplyRequested;
                        temporaryTimer.Stop();
                        Mod.Log.Info("ParkManager decoration performance: matched "
                            + $"{taggedObjects} temporary objects in "
                            + $"{temporaryTimer.Elapsed.TotalMilliseconds:F1} ms.");
                        PublishDecorationState("Ausstattung wird als frei editierbare Vanilla-Objekte übernommen …");
                        return true;
                    }
                    if (UnityEngine.Time.frameCount - _decorationStartedFrame
                        <= MaterializationTimeoutFrames) return true;
                    AbortDecorationBuild("Zeitüberschreitung beim Erzeugen der Ausstattung.");
                    return true;
                case DecorationBuildPhase.ApplyRequested:
                    applyMode = ApplyMode.None;
                    if (UnityEngine.Time.frameCount - _decorationApplyFrame
                        < GeometrySettleFrames) return true;
                    var permanentTimer = System.Diagnostics.Stopwatch.StartNew();
                    TagPermanentDecorationEntities(out var permanentObjects,
                        out var permanentSurfaces, out var permanentFenceEdges,
                        out var permanentFenceNodes);
                    permanentTimer.Stop();
                    var surfaceReady = _pendingSurfacePrefab == Entity.Null
                        || permanentSurfaces >= 1;
                    var permanentFenceReady = _pendingFencePrefab == Entity.Null
                        || permanentFenceEdges >= _expectedFenceCourses;
                    if (permanentObjects < _pendingDecorations.Count
                        || !surfaceReady || !permanentFenceReady)
                    {
                        if (UnityEngine.Time.frameCount - _decorationApplyFrame
                            <= MaterializationTimeoutFrames) return true;
                        AbortDecorationBuild("Die materialisierte Ausstattung blieb "
                            + $"unvollständig: {permanentObjects}/"
                            + $"{_pendingDecorations.Count} Objekte, "
                            + $"{permanentSurfaces}/"
                            + $"{(_pendingSurfacePrefab == Entity.Null ? 0 : 1)} Flächen, "
                            + $"{permanentFenceEdges}/{_expectedFenceCourses} Zaunkanten "
                            + $"und {permanentFenceNodes} Zaunknoten.");
                        return true;
                    }
                    Mod.Log.Info("ParkManager rediscovered permanent decoration: "
                        + $"{permanentObjects}/{_pendingDecorations.Count} objects, "
                        + $"{permanentSurfaces}/"
                        + $"{(_pendingSurfacePrefab == Entity.Null ? 0 : 1)} surfaces, "
                        + $"{permanentFenceEdges}/{_expectedFenceCourses} fence edges "
                        + $"and {permanentFenceNodes} fence nodes in "
                        + $"{permanentTimer.Elapsed.TotalMilliseconds:F1} ms.");
                    FinalizeDecorationBuild();
                    return true;
                case DecorationBuildPhase.ClearRequested:
                    applyMode = ApplyMode.Clear;
                    _decorationBuildPhase = DecorationBuildPhase.Idle;
                    PublishDecorationState("Ausstattungsbau wurde verworfen.");
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
                    category = ParkAssetCategory.Bush;
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
                default:
                    prefab = Entity.Null;
                    return false;
            }
            if (_assetCatalog.TryGetParkVariant(category, _decorationPlan.Seed,
                placement.Variant, out prefab, out _)) return true;
            Mod.Log.Warn($"ParkManager has no usable {category} prefab; layer skipped.");
            return false;
        }

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
                var a = _pathPlan.Nodes[edge.A].Position;
                var b = _pathPlan.Nodes[edge.B].Position;
                var delta = b - a;
                var length = math.lengthsq(delta);
                var t = length < 0.0001f ? 0f
                    : math.clamp(math.dot(point - a, delta) / length, 0f, 1f);
                var candidate = a + delta * t;
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
            var definition = EntityManager.CreateEntity();
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
            if (placement.Kind == ParkDecorationKind.Bench
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
            Entity prefab, ref TerrainHeightData heightData, int randomSeed)
        {
            if (run.Size < 0.5f) return false;
            var forward = new float2(math.sin(run.Rotation),
                math.cos(run.Rotation));
            var half = forward * (run.Size * 0.5f);
            var start2 = run.Position - half;
            var end2 = run.Position + half;
            var start = new float3(start2.x, 0f, start2.y);
            var end = new float3(end2.x, 0f, end2.y);
            start.y = TerrainUtils.SampleHeight(ref heightData, start);
            end.y = TerrainUtils.SampleHeight(ref heightData, end);
            if (!math.all(math.isfinite(start)) || !math.all(math.isfinite(end)))
                return false;

            var curve = NetUtils.StraightCurve(start, end);
            var definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_RandomSeed = randomSeed,
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponentData(definition, new NetCourse
            {
                m_Curve = curve,
                m_Length = math.distance(start, end),
                m_FixedIndex = -1,
                m_Elevation = float2.zero,
                m_StartPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = start,
                    m_Rotation = NetUtils.GetNodeRotation(
                        MathUtils.StartTangent(curve)),
                    m_CourseDelta = 0f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsFirst,
                    m_ParentMesh = -1,
                    m_SplitPosition = 0f,
                },
                m_EndPosition = new CoursePos
                {
                    m_Entity = Entity.Null,
                    m_Position = end,
                    m_Rotation = NetUtils.GetNodeRotation(
                        MathUtils.EndTangent(curve)),
                    m_CourseDelta = 1f,
                    m_Elevation = float2.zero,
                    m_Flags = CoursePosFlags.IsLast,
                    m_ParentMesh = -1,
                    m_SplitPosition = 0f,
                },
            });
            return true;
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

        private bool CreateParkSurface(Entity prefab,
            ref TerrainHeightData heightData)
        {
            if (_points.Count < 3) return false;
            var definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
            });
            EntityManager.AddComponent<Updated>(definition);
            var nodes = EntityManager.AddBuffer<Game.Areas.Node>(definition);
            nodes.ResizeUninitialized(_points.Count + 1);
            for (var i = 0; i < _points.Count; i++)
            {
                var point = _points[i];
                var height = TerrainUtils.SampleHeight(ref heightData,
                    new float3(point.x, 0f, point.y));
                if (!math.isfinite(height)) return false;
                nodes[i] = new Game.Areas.Node(
                    new float3(point.x, height, point.y), float.MinValue);
            }
            nodes[_points.Count] = nodes[0];
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

        private bool TagMaterializedSurface()
        {
            using var areas = _tempAreaQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < areas.Length; i++)
            {
                var entity = areas[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                    != _pendingSurfacePrefab) continue;
                if (!EntityManager.HasComponent<ParkPathMember>(entity))
                    EntityManager.AddComponentData(entity, new ParkPathMember
                    {
                        Park = _lastBuildRecord,
                        ElementId = _pendingSurfaceElementId,
                        Kind = ParkPathMemberKind.ParkSurface,
                    });
                return true;
            }
            return false;
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
                var member = new ParkPathMember
                {
                    Park = _lastBuildRecord,
                    ElementId = nextElementId++,
                    Kind = ParkPathMemberKind.Fence,
                };
                if (EntityManager.HasComponent<ParkPathMember>(entity))
                    EntityManager.SetComponentData(entity, member);
                else EntityManager.AddComponentData(entity, member);
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
            if (prefab != Entity.Null)
                _capturedDecorationObjectPrefabs.Add(prefab);
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
            out int surfaceCount, out int fenceEdgeCount, out int fenceNodeCount)
        {
            objectCount = TagPermanentDecorationObjects();
            surfaceCount = TagPermanentDecorationSurface();
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

        private int TagPermanentDecorationSurface()
        {
            if (_pendingSurfacePrefab == Entity.Null) return 0;
            using var areas = _permanentAreaQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < areas.Length; i++)
            {
                var entity = areas[i];
                if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab
                        != _pendingSurfacePrefab
                    || !IsMaterializedBuildEntity(entity, _lastBuildRecord,
                        _decorationAreaBaseline)) continue;
                if (EntityManager.HasComponent<ParkPathMember>(entity))
                {
                    var existing = EntityManager.GetComponentData<ParkPathMember>(entity);
                    if (existing.Park != _lastBuildRecord) continue;
                    existing.ElementId = _pendingSurfaceElementId;
                    existing.Kind = ParkPathMemberKind.ParkSurface;
                    EntityManager.SetComponentData(entity, existing);
                }
                else EntityManager.AddComponentData(entity, new ParkPathMember
                {
                    Park = _lastBuildRecord,
                    ElementId = _pendingSurfaceElementId,
                    Kind = ParkPathMemberKind.ParkSurface,
                });
                return 1;
            }
            return 0;
        }

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

        private void ClearDecorationMaterializationBaselines()
        {
            _decorationObjectBaseline.Clear();
            _decorationAreaBaseline.Clear();
            _decorationFenceBaseline.Clear();
            _capturedDecorationObjectPrefabs.Clear();
        }

        private void FinalizeDecorationBuild()
        {
            EnsureMembersAreTopLevel(_lastBuildRecord);
            ApplyPermanentTreeAges();
            LogFurnitureVisibility(_lastBuildRecord);
            RefreshEditableBaseline(_lastBuildRecord);
            UpdateBuildReceipt(_lastBuildRecord, true);
            var count = CountDecorationMembers(_lastBuildRecord);
            _pendingDecorations.Clear();
            _pendingSurfacePrefab = Entity.Null;
            _pendingFencePrefab = Entity.Null;
            _expectedFenceCourses = 0;
            _expectedDecorationMembers = 0;
            ClearDecorationMaterializationBaselines();
            _decorationBuildPhase = DecorationBuildPhase.Idle;
            PublishState($"Parkfläche und Ausstattung gebaut: {count} frei editierbare Elemente.");
            PublishDecorationState("Gebaut · frei editierbar · "
                + DecorationPlanSummary(_decorationPlan));
            Mod.Log.Info($"ParkManager built {count} top-level decoration entities.");
        }

        private void LogFurnitureVisibility(Entity park)
        {
            var benches = 0;
            var lamps = 0;
            var trashBins = 0;
            var overriddenBenches = 0;
            var overriddenLamps = 0;
            var overriddenTrashBins = 0;
            var hiddenBenches = 0;
            var hiddenLamps = 0;
            var hiddenTrashBins = 0;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                if (member.Park != park
                    || (member.Kind != ParkPathMemberKind.Bench
                        && member.Kind != ParkPathMemberKind.Lamp
                        && member.Kind != ParkPathMemberKind.TrashBin)) continue;
                var overridden = EntityManager.HasComponent<Overridden>(entity);
                var hidden = EntityManager.HasComponent<Hidden>(entity);
                if (member.Kind == ParkPathMemberKind.Bench)
                {
                    benches++;
                    if (overridden) overriddenBenches++;
                    if (hidden) hiddenBenches++;
                }
                else if (member.Kind == ParkPathMemberKind.Lamp)
                {
                    lamps++;
                    if (overridden) overriddenLamps++;
                    if (hidden) hiddenLamps++;
                }
                else
                {
                    trashBins++;
                    if (overridden) overriddenTrashBins++;
                    if (hidden) hiddenTrashBins++;
                }
            }
            Mod.Log.Info("ParkManager furniture visibility: "
                + $"benches {benches} (Overridden {overriddenBenches}, Hidden {hiddenBenches}); "
                + $"lamps {lamps} (Overridden {overriddenLamps}, Hidden {hiddenLamps}); "
                + $"trash bins {trashBins} (Overridden {overriddenTrashBins}, Hidden {hiddenTrashBins}); "
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

        private int NextElementId(Entity park)
        {
            var maximum = 0;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < members.Length; i++)
            {
                var member = EntityManager.GetComponentData<ParkPathMember>(members[i]);
                if (member.Park == park) maximum = Math.Max(maximum, member.ElementId);
            }
            return maximum + 1;
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
            => kind >= ParkPathMemberKind.ParkSurface;

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
                default: return ParkPathMemberKind.Bush;
            }
        }

        private string DecorationPlanSummary(ParkDecorationPlan plan)
        {
            if (plan == null) return "Noch keine Ausstattung geplant.";
            return $"Vorlage {_assetCatalog.GetParkPaletteName(plan.Seed)} · "
                + $"Seed {plan.Seed} · {plan.Count(ParkDecorationKind.Tree)} Bäume · "
                + $"{plan.Count(ParkDecorationKind.Bush)} Büsche · "
                + $"{plan.Count(ParkDecorationKind.Bench)} Bänke · "
                + $"{plan.Count(ParkDecorationKind.Lamp)} Lampen · "
                + $"{plan.Count(ParkDecorationKind.TrashBin)} Mülleimer · "
                + (plan.FenceEnabled
                    ? $"{plan.Count(ParkDecorationKind.Fence)} Zaunläufe"
                    : "ohne Zaun");
        }

        private void AbortDecorationBuild(string reason)
        {
            Mod.Log.Warn("ParkManager decoration build aborted: " + reason);
            TagPermanentDecorationEntities(out var permanentObjects,
                out var permanentSurfaces, out var permanentFenceEdges,
                out var permanentFenceNodes);
            DeleteDecorationMembers(_lastBuildRecord);
            _pendingDecorations.Clear();
            _pendingSurfacePrefab = Entity.Null;
            _pendingFencePrefab = Entity.Null;
            _expectedFenceCourses = 0;
            _expectedDecorationMembers = 0;
            ClearDecorationMaterializationBaselines();
            applyMode = ApplyMode.Clear;
            _decorationBuildPhase = DecorationBuildPhase.ClearRequested;
            PublishState(reason);
            PublishDecorationState("Ausstattungsbau wird verworfen …");
            Mod.Log.Info("ParkManager decoration abort cleanup captured "
                + $"{permanentObjects} permanent objects, "
                + $"{permanentSurfaces} permanent surfaces, "
                + $"{permanentFenceEdges} permanent fence edges and "
                + $"{permanentFenceNodes} permanent fence nodes.");
        }

        private void DecorationBuildWasRemoved()
        {
            _pendingDecorations.Clear();
            _pendingSurfacePrefab = Entity.Null;
            _pendingFencePrefab = Entity.Null;
            _expectedFenceCourses = 0;
            ClearDecorationMaterializationBaselines();
            _decorationBuildPhase = DecorationBuildPhase.Idle;
            PublishDecorationState(_decorationPlan == null
                ? "Noch keine Ausstattung geplant."
                : "Geplant · noch nicht gebaut · "
                    + DecorationPlanSummary(_decorationPlan));
        }

        private void PublishDecorationState(string summary)
            => _ui?.SetDecorationState(_fenceEnabled, _vegetationDensity,
                _decorationPlan != null, DecorationBuildBusy,
                HasBuiltDecorations, summary);
    }
}
