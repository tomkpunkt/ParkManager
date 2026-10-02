// Polygon interaction adapted from ParkingLotTool (GPL-3.0).
// This file contains no parking generation, zoning or entity placement.
using System;
using System.Collections.Generic;
using Game.Common;
using Game.Input;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using UnityEngine.Scripting;
using ParkManager.Geometry;

namespace ParkManager.Tools
{
    /// <summary>
    /// Interactive world tool for drawing a park boundary, editing its points,
    /// defining gates and coordinating path and furnishing generation.
    /// Responsibilities split across partial files cover input and undo,
    /// snapping, overlay rendering, ECS materialization and build cleanup.
    /// Generated Vanilla entities remain top-level so external tools can edit
    /// them while ParkManager retains logical group membership.
    /// </summary>
    public sealed partial class ParkToolSystem : ToolBaseSystem
    {
        public const string ToolId = "ParkManager.Polygon";
        private const float CloseDistance = 12f;
        private const float PointHitDistance = 8f;
        private const float EdgeHitDistance = 6f;
        private const float DoubleClickSeconds = 0.4f;
        private const int PathCandidateCount = 6;

        private readonly List<float2> _points = new List<float2>();
        private readonly List<float3> _worldPoints = new List<float3>();
        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private readonly List<float3> _entrances = new List<float3>();
        private readonly List<float3> _pathPreview = new List<float3>();
        private ParkPathPlan _pathPlan;
        private OverlayRenderSystem _overlay;
        private ParkManagerUISystem _ui;
        private bool _closed;
        private bool _hasHover;
        private float3 _hover;
        private int _hoverPoint = -1;
        private int _dragPoint = -1;
        private float2 _pointStart;
        private float3 _pointWorldStart;
        private int _hoverEdge = -1;
        private int _lastClickedEdge = -1;
        private float _lastEdgeClickTime = -10f;
        private bool _plannerMode;
        private int _hoverEntrance = -1;
        private ProceduralSiteKind _selectedSiteKind = ProceduralSiteKind.Park;

        public override string toolID => ToolId;
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        private bool IsPlaza => _selectedSiteKind == ProceduralSiteKind.Plaza;

        /// <summary>A running path or furnishing build blocks all edits.</summary>
        private bool BuildBusy => PathBuildBusy || DecorationBuildBusy || LakeBuildBusy;

        /// <summary>Outline and entrances are fixed while a build exists or runs.</summary>
        private bool OutlineLocked => HasBuiltPaths || BuildBusy;

        internal void SetSiteKind(int value)
        {
            if (OutlineLocked) return;
            var nextKind = value == (int)ProceduralSiteKind.Plaza
                ? ProceduralSiteKind.Plaza
                : ProceduralSiteKind.Park;
            if (_selectedSiteKind == nextKind) return;
            _selectedSiteKind = nextKind;
            _pedestrianPathPrefab = Unity.Entities.Entity.Null;
            _plazaPlan = null;
            DiscardPathAndDecorationPlans();
            PublishPlannerState();
            _ui?.SetSiteType((int)_selectedSiteKind);
            PublishState(IsPlaza
                ? UiText.Of("status.plazaSelected")
                : UiText.Of("status.parkSelected"));
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _ui = World.GetOrCreateSystemManaged<ParkManagerUISystem>();
            InitializePathPlacement();
            InitializeDecorationPlacement();
            InitializeSnappingTargets();
            PublishPlazaArrangement();
            PublishPathBuildState(UiText.Of("path.noParkThisSession"));
        }

        [Preserve]
        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            InitializeRaycast();
            if (applyAction != null) applyAction.shouldBeEnabled = true;
            if (secondaryApplyAction != null) secondaryApplyAction.shouldBeEnabled = true;
            if (cancelAction != null) cancelAction.shouldBeEnabled = true;
            _ui.SetToolActive(true);
            MonitorExternalPathEdits(true);
            PublishState(UiText.Of("status.drawHint"));
            Mod.Log.Info("ParkManager polygon tool activated.");
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            _buildDecorationsAfterPaths = false;
            if (PathBuildBusy && _pathBuildPhase != PathBuildPhase.ClearRequested)
                AbortPathBuild(UiText.Of("path.toolLeft"));
            if (DecorationBuildBusy
                && _decorationBuildPhase != DecorationBuildPhase.ClearRequested)
                AbortDecorationBuild(UiText.Of("decoration.toolLeft"));
            AbortLakeBuild();
            if (_removeMode)
            {
                _removeMode = false;
                ClearRemoveSelection();
                PublishRemoveState();
            }
            _ui?.SetToolActive(false);
            base.OnStopRunning();
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Terrain;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround;
            m_ToolRaycastSystem.raycastFlags = 0;
            m_SnapOnMask = SupportedSnapKinds;
            m_SnapOffMask = SupportedSnapKinds;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            var deps = base.OnUpdate(inputDeps);
            if (m_ToolSystem.activeTool != this) return deps;
            MonitorExternalPathEdits();
            if (ProcessBuild()) return Render(deps);

            var inputAllowed = WorldInputAllowed();
            _hasHover = inputAllowed && TryGetGroundPoint(out _hover);
            UpdateHoverTargets();

            if (EscapePressed())
            {
                // Escape first leaves the remove mode, then the tool.
                if (_removeMode)
                {
                    SetRemoveMode(false);
                    return Render(deps);
                }
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return deps;
            }

            if (_removeMode)
            {
                UpdateRemoveMode(inputAllowed);
                return Render(deps);
            }

            if (!_plannerMode && UndoPressed())
            {
                Undo();
                return Render(deps);
            }

            if (_plannerMode)
            {
                if (inputAllowed && secondaryApplyAction != null
                    && secondaryApplyAction.WasPressedThisFrame())
                    HandlePlannerRightClick();
                else if (inputAllowed && applyAction != null
                    && applyAction.WasPressedThisFrame()) HandlePlannerClick();
            }
            else if (_dragPoint >= 0) UpdatePointDrag();
            else if (inputAllowed && secondaryApplyAction != null
                     && secondaryApplyAction.WasPressedThisFrame()) HandleRightClick();
            else if (inputAllowed && applyAction != null
                     && applyAction.WasPressedThisFrame()) HandleLeftClick();

            return Render(deps);
        }

        /// <summary>
        /// Advances the running build by one frame. An exception inside a
        /// build step would otherwise repeat every frame with the phase
        /// unchanged, which shows as a panel that stays on "Building".
        /// </summary>
        private bool ProcessBuild()
        {
            try
            {
                return ProcessLakeBuild() || ProcessDecorationPlacement()
                    || ProcessPathPlacement();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager build step failed in path phase "
                    + $"{_pathBuildPhase}, decoration phase {_decorationBuildPhase}, "
                    + $"lake phase {_lakeBuildPhase}; the build is cancelled.");
                if (!BuildBusy) return false;
                CancelFailedBuild(UiText.Of("status.buildFailed", exception.Message));
                return true;
            }
        }

        /// <summary>
        /// Cancels through the regular abort paths so already materialized
        /// entities are removed; falls back to a plain phase reset if the
        /// cleanup itself fails.
        /// </summary>
        private void CancelFailedBuild(string reason)
        {
            _buildDecorationsAfterPaths = false;
            try
            {
                if (LakeBuildBusy)
                {
                    DiscardLakeBrushes();
                    _lakeBuildPhase = LakeBuildPhase.Idle;
                    applyMode = ApplyMode.None;
                    PublishState(reason);
                    PublishDecorationState(reason);
                }
                if (DecorationBuildBusy
                    && _decorationBuildPhase != DecorationBuildPhase.ClearRequested)
                    AbortDecorationBuild(reason);
                if (PathBuildBusy && _pathBuildPhase != PathBuildPhase.ClearRequested)
                    AbortPathBuild(reason);
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "ParkManager build cleanup failed; "
                    + "resetting the build state without further cleanup.");
                ResetBuildPhases();
                applyMode = ApplyMode.Clear;
                PublishState(reason);
                PublishPathBuildState(UiText.Of("path.failed", reason),
                    PathBuildStatus.Error);
                PublishDecorationState(reason);
            }
        }

        /// <summary>Returns every build state machine to idle without touching entities.</summary>
        private void ResetBuildPhases()
        {
            _buildDecorationsAfterPaths = false;
            _preflightWarning = null;
            _buildDefinitions.Clear();
            _lakeBrushDefinitions.Clear();
            _lakeBuildPhase = LakeBuildPhase.Idle;
            _pathBuildPhase = PathBuildPhase.Idle;
            _decorationBuildPhase = DecorationBuildPhase.Idle;
            _pendingBuildRecord = Unity.Entities.Entity.Null;
            ClearMaterializationBaselines();
            ResetPendingDecorationBuild();
        }

        /// <summary>
        /// The tool system outlives a loaded city, but its entity references
        /// and build phases belong to the previous one. Without this reset a
        /// build that was running (or stuck) before loading kept the panel on
        /// "Building" in the newly loaded save.
        /// </summary>
        protected override void OnGameLoadingComplete(
            Colossal.Serialization.Entities.Purpose purpose, Game.GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            ResetBuildPhases();
            _lastBuildRecord = Unity.Entities.Entity.Null;
            applyMode = ApplyMode.None;
            _removeMode = false;
            ClearRemoveSelection();
            PublishRemoveState();
            ResetWorkspaceDraft();
            PublishState(UiText.Of("status.drawHint"));
            PublishPathBuildState(UiText.Of("path.noParkThisSession"));
            PublishDecorationState(UiText.Of("decoration.none"));
            Mod.Log.Info("ParkManager reset its workspace for the loaded game.");
        }

        private bool TryGetGroundPoint(out float3 world)
        {
            world = default;
            if (!GetRaycastResult(out ControlPoint point)) return false;
            world = point.m_Position;
            if (!math.all(math.isfinite(world))) return false;
            // Snap only while placing or dragging polygon points. Hovering a
            // finished edge and placing gates must remain cursor-exact.
            if (!_plannerMode && (!_closed || _dragPoint >= 0))
            {
                var snapped = ApplySnapping(world);
                if (math.all(math.isfinite(snapped))) world = snapped;
            }
            else ClearSnapFeedback();
            return true;
        }

        private void HandleLeftClick()
        {
            if (!_hasHover || OutlineLocked) return;
            InvalidatePlannerData();
            if (_closed)
            {
                if (_hoverPoint >= 0)
                {
                    ResetEdgeDoubleClick();
                    PushUndo();
                    _dragPoint = _hoverPoint;
                    _pointStart = _points[_dragPoint];
                    _pointWorldStart = _worldPoints[_dragPoint];
                    _dragStartAxis = GetSnapAxis(_dragPoint);
                }
                else if (_hoverEdge >= 0)
                {
                    var now = UnityEngine.Time.unscaledTime;
                    if (_lastClickedEdge == _hoverEdge
                        && now - _lastEdgeClickTime <= DoubleClickSeconds)
                    {
                        PushUndo();
                        var insertIndex = _hoverEdge + 1;
                        _points.Insert(insertIndex, _hover.xz);
                        _worldPoints.Insert(insertIndex, _hover);
                        InsertSnapAxis(insertIndex);
                        if (!IsValidPolygon())
                        {
                            _points.RemoveAt(insertIndex);
                            _worldPoints.RemoveAt(insertIndex);
                            RemoveSnapAxis(insertIndex);
                            DiscardLastUndo();
                            PublishState(UiText.Of("status.splitInvalid"));
                        }
                        else PublishState(UiText.Of("status.edgeSplit"));
                        ResetEdgeDoubleClick();
                    }
                    else
                    {
                        _lastClickedEdge = _hoverEdge;
                        _lastEdgeClickTime = now;
                    }
                }
                return;
            }

            PushUndo();
            if (CanClose())
            {
                _closed = true;
                PublishState(IsValidPolygon()
                    ? UiText.Of("status.polygonClosedValid")
                    : UiText.Of("status.polygonClosedInvalid"));
                return;
            }

            var point = _hover.xz;
            if (_points.Count == 0 || math.distancesq(point, _points[_points.Count - 1]) > 0.0625f)
            {
                _points.Add(point);
                _worldPoints.Add(_hover);
                StoreSnapAxis(_points.Count - 1);
            }
            PublishState(UiText.Of("status.addMorePoints"));
        }

        private void UpdatePointDrag()
        {
            if (applyAction != null && applyAction.IsPressed())
            {
                if (!_hasHover) return;
                _points[_dragPoint] = _hover.xz;
                _worldPoints[_dragPoint] = _hover;
                StoreSnapAxis(_dragPoint);
                InvalidatePlannerData();
                return;
            }

            if (!IsValidPolygon())
            {
                _points[_dragPoint] = _pointStart;
                _worldPoints[_dragPoint] = _pointWorldStart;
                RestoreSnapAxis(_dragPoint, _dragStartAxis);
                DiscardLastUndo();
                PublishState(UiText.Of("status.moveRejected"));
            }
            else PublishState(UiText.Of("status.pointMoved"));
            _dragPoint = -1;
        }

        private void HandleRightClick()
        {
            if (_points.Count == 0 || OutlineLocked) return;
            PushUndo();
            InvalidatePlannerData();
            if (_hoverPoint >= 0)
            {
                _points.RemoveAt(_hoverPoint);
                _worldPoints.RemoveAt(_hoverPoint);
                RemoveSnapAxis(_hoverPoint);
                if (_points.Count < 3) _closed = false;
                PublishState(UiText.Of("status.pointRemoved"));
            }
            else if (_closed)
            {
                _closed = false;
                PublishState(UiText.Of("status.polygonOpened"));
            }
            else
            {
                var last = _points.Count - 1;
                _points.RemoveAt(last);
                _worldPoints.RemoveAt(last);
                RemoveSnapAxis(last);
                PublishState(UiText.Of("status.lastPointRemoved"));
            }
        }

        internal void ClearPolygon()
        {
            if (OutlineLocked)
            {
                PublishState(UiText.Of("status.outlineLockedRedraw"));
                return;
            }
            if (_points.Count == 0) return;
            PushUndo();
            _points.Clear();
            _worldPoints.Clear();
            _pointAxes.Clear();
            _closed = false;
            _dragPoint = -1;
            _plannerMode = false;
            InvalidatePlannerData();
            ResetEdgeDoubleClick();
            PublishState(UiText.Of("status.polygonReset"));
        }

        private void UpdateHoverTargets()
        {
            _hoverPoint = -1;
            _hoverEdge = -1;
            _hoverEntrance = -1;
            if (!_hasHover) return;
            var cursor = _hover.xz;
            if (_plannerMode)
            {
                var bestEntrance = PointHitDistance * PointHitDistance;
                for (var i = 0; i < _entrances.Count; i++)
                {
                    var distance = math.distancesq(cursor, _entrances[i].xz);
                    if (distance < bestEntrance)
                    {
                        bestEntrance = distance;
                        _hoverEntrance = i;
                    }
                }
            }
            var bestPoint = PointHitDistance * PointHitDistance;
            for (var i = 0; !_plannerMode && i < _points.Count; i++)
            {
                var distance = math.distancesq(cursor, _points[i]);
                if (distance < bestPoint) { bestPoint = distance; _hoverPoint = i; }
            }
            if (!_closed || _hoverPoint >= 0) return;
            var bestEdge = EdgeHitDistance * EdgeHitDistance;
            for (var i = 0; i < _points.Count; i++)
            {
                var distance = PolygonMath.DistanceToSegmentSquared(cursor, _points[i],
                    _points[(i + 1) % _points.Count]);
                if (distance < bestEdge) { bestEdge = distance; _hoverEdge = i; }
            }
        }

        internal void SetPlannerMode(bool enabled)
        {
            if (BuildBusy)
            {
                PublishState(UiText.Of("status.buildBusy"));
                return;
            }
            if (_plannerMode == enabled) return;
            if (!enabled)
            {
                if (HasBuiltPaths)
                {
                    PublishState(UiText.Of("status.outlineLockedEdit"));
                    return;
                }
                _plannerMode = false;
                PublishState(UiText.Of("status.drawMode"));
                PublishPlannerState();
                return;
            }

            if (!_closed || !IsValidPolygon())
            {
                PublishState(UiText.Of("status.plannerNeedsPolygon"));
                return;
            }

            _dragPoint = -1;
            _plannerMode = true;
            PublishState(UiText.Of("status.plannerActive"));
            PublishPlannerState();
        }

        private void HandlePlannerClick()
        {
            if (!_hasHover || _hoverEdge < 0 || OutlineLocked) return;
            var a = _worldPoints[_hoverEdge];
            var b = _worldPoints[(_hoverEdge + 1) % _worldPoints.Count];
            var ab = b.xz - a.xz;
            var length = math.lengthsq(ab);
            var t = length < 0.0001f ? 0f
                : math.clamp(math.dot(_hover.xz - a.xz, ab) / length, 0f, 1f);
            var entrance = math.lerp(a, b, t);
            for (var i = 0; i < _entrances.Count; i++)
                if (math.distancesq(_entrances[i].xz, entrance.xz) < 16f)
                {
                    PublishState(UiText.Of("status.entranceExists"));
                    return;
                }
            _entrances.Add(entrance);
            DiscardPathAndDecorationPlans();
            PublishState(UiText.Of("status.entranceAdded"));
            PublishPlannerState();
        }

        private void HandlePlannerRightClick()
        {
            if (OutlineLocked) return;
            if (_hoverEntrance < 0 || _hoverEntrance >= _entrances.Count)
            {
                PublishState(UiText.Of("status.hoverEntranceToRemove"));
                return;
            }
            _entrances.RemoveAt(_hoverEntrance);
            _hoverEntrance = -1;
            DiscardPathAndDecorationPlans();
            PublishState(UiText.Of("status.entranceRemoved"));
            PublishPlannerState();
        }

        internal void GeneratePaths()
        {
            if (OutlineLocked)
            {
                PublishState(HasBuiltPaths
                    ? UiText.Of("status.removeParkToReplan")
                    : UiText.Of("status.buildBusy"));
                return;
            }
            if (!_plannerMode || !_closed || !IsValidPolygon())
            {
                PublishState(UiText.Of("status.openPlannerFirst"));
                return;
            }
            if (_entrances.Count == 0)
            {
                PublishState(UiText.Of("status.needEntrance"));
                return;
            }

            _pathPreview.Clear();
            _buildIssues.Clear();
            _preflightWarning = null;
            if (IsPlaza)
            {
                PublishPathBuildState(UiText.Of("path.plazaVariantCalculating"));
                // The first design uses the current settings; every further
                // request is a new variant with rolled settings.
                if (_plazaPlan != null) RollPlazaVariant(Seeds.NewSeed());
                GeneratePlazaPlan();
                return;
            }
            _plazaPlan = null;
            PublishPathBuildState(UiText.Of("path.variantCalculating"));
            var hub = FindInteriorHub();
            var entrancePoints = EntrancePoints();
            var familySeed = Seeds.NewSeed();
            ParkPathPlan best = null;
            var bestScore = double.MaxValue;
            for (var i = 0; i < PathCandidateCount; i++)
            {
                var seed = Seeds.NonZero((int)(((long)familySeed + i * 104729L)
                    % int.MaxValue));
                var candidate = ParkPathPlanner.Generate(_points, entrancePoints,
                    hub, seed);
                var score = candidate.NaturalnessScore(_points);
                if (!(score < bestScore)) continue;
                best = candidate;
                bestScore = score;
            }
            _pathPlan = (best ?? ParkPathPlan.Empty(familySeed))
                .Smooth(_points, entrancePoints);
            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                var a = _pathPlan.Nodes[edge.A].Position;
                var b = _pathPlan.Nodes[edge.B].Position;
                _pathPreview.Add(new float3(a.x, HeightForPreview(a), a.y));
                _pathPreview.Add(new float3(b.x, HeightForPreview(b), b.y));
            }
            if (_pathPlan.Edges.Count > 0)
            {
                GenerateDecorationPlan(Seeds.NonZero(
                    unchecked(_pathPlan.Seed * 1103515245 + 12345) & int.MaxValue));
            }
            PublishState(_pathPlan.Edges.Count > 0
                ? UiText.Of("status.pathVariantCreated", PathCandidateCount,
                    _pathPlan.Seed, _pathPlan.Edges.Count, _pathPlan.TotalLength,
                    bestScore)
                : UiText.Of("status.noConnectedPath"));
            PublishPlannerState();
        }

        private float HeightForPreview(float2 point)
        {
            var best = float.MaxValue;
            var height = 0f;
            for (var i = 0; i < _worldPoints.Count; i++)
            {
                var distance = math.distancesq(point, _worldPoints[i].xz);
                if (distance >= best) continue;
                best = distance;
                height = _worldPoints[i].y;
            }
            for (var i = 0; i < _entrances.Count; i++)
            {
                var distance = math.distancesq(point, _entrances[i].xz);
                if (distance >= best) continue;
                best = distance;
                height = _entrances[i].y;
            }
            return height;
        }

        private float2 FindInteriorHub()
        {
            PolygonMath.Bounds(_points, out var min, out var max);
            var best = _points[0];
            var bestClearance = -1f;
            const int steps = 16;
            for (var y = 0; y <= steps; y++)
            for (var x = 0; x <= steps; x++)
            {
                var candidate = math.lerp(min, max,
                    new float2((float)x / steps, (float)y / steps));
                if (!PointInside(candidate)) continue;
                var clearance = PolygonMath.DistanceToBoundarySquared(candidate,
                    _points);
                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = candidate;
                }
            }
            return best;
        }

        private bool PointInside(float2 point)
            => PolygonMath.PointInside(point, _points);

        private void InvalidatePlannerData()
        {
            _entrances.Clear();
            _plazaPlan = null;
            _buildIssues.Clear();
            DiscardPathAndDecorationPlans();
            PublishPlannerState();
        }

        /// <summary>Drops the path/surface and furnishing previews.</summary>
        private void DiscardPathAndDecorationPlans()
        {
            _pathPlan = null;
            _pathPreview.Clear();
            _decorationPlan = null;
            PublishDecorationState(UiText.Of("decoration.none"));
        }

        /// <summary>Entrance positions in the XZ planning plane.</summary>
        private List<float2> EntrancePoints()
        {
            var result = new List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++) result.Add(_entrances[i].xz);
            return result;
        }

        private void PublishPlannerState()
            => _ui?.SetPlannerState(_plannerMode, _entrances.Count,
                IsPlaza ? _plazaPlan != null
                    : _pathPlan != null && _pathPlan.Edges.Count > 0);

        private bool CanClose() => !_closed && _points.Count >= 3 && _hasHover
            && math.distancesq(_hover.xz, _points[0]) <= CloseDistance * CloseDistance;

        private bool IsValidPolygon()
        {
            if (!_closed || _points.Count < 3 || Math.Abs(SignedArea()) < 4.0) return false;
            for (var i = 0; i < _points.Count; i++)
            for (var j = i + 1; j < _points.Count; j++)
            {
                // Adjacent edges share a vertex and cannot cross properly.
                if (j == i + 1 || i == (j + 1) % _points.Count) continue;
                if (SegmentsCross(_points[i], _points[(i + 1) % _points.Count],
                    _points[j], _points[(j + 1) % _points.Count])) return false;
            }
            return true;
        }

        private double SignedArea() => PolygonMath.SignedArea(_points);

        private static bool SegmentsCross(float2 a, float2 b, float2 c, float2 d)
        {
            var ab1 = Cross(a, b, c); var ab2 = Cross(a, b, d);
            var cd1 = Cross(c, d, a); var cd2 = Cross(c, d, b);
            return ab1 * ab2 < 0f && cd1 * cd2 < 0f;
        }

        private static float Cross(float2 a, float2 b, float2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static bool WorldInputAllowed()
        {
            var input = InputManager.instance;
            return input != null && input.controlOverWorld && !input.hasInputFieldFocus;
        }

        private static bool UndoPressed()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || InputManager.instance == null
                || InputManager.instance.hasInputFieldFocus) return false;
            var control = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
            return control && keyboard.zKey.wasPressedThisFrame;
        }

        private static bool EscapePressed()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
        }

        private void ResetEdgeDoubleClick()
        {
            _lastClickedEdge = -1;
            _lastEdgeClickTime = -10f;
        }

        private void PushUndo() => _undo.Push(new Snapshot
        {
            Points = _points.ToArray(), WorldPoints = _worldPoints.ToArray(),
            Axes = SnapAxesSnapshot(), Closed = _closed
        });

        private void DiscardLastUndo() { if (_undo.Count > 0) _undo.Pop(); }

        private void Undo()
        {
            if (_undo.Count == 0) return;
            var snapshot = _undo.Pop();
            Restore(snapshot.Points, snapshot.WorldPoints);
            RestoreSnapAxes(snapshot.Axes);
            _closed = snapshot.Closed;
            _dragPoint = -1;
            PublishState(UiText.Of("status.undone"));
        }

        private void Restore(float2[] points, float3[] worldPoints)
        {
            _points.Clear(); _points.AddRange(points);
            _worldPoints.Clear(); _worldPoints.AddRange(worldPoints);
        }

        private float2[] SnapAxesSnapshot()
        {
            SyncSnapAxes();
            return _pointAxes.ToArray();
        }

        private void RestoreSnapAxes(float2[] axes)
        {
            _pointAxes.Clear();
            if (axes != null) _pointAxes.AddRange(axes);
            SyncSnapAxes();
        }

        private void PublishState(string status)
        {
            _ui?.SetPolygonState(_points.Count,
                _points.Count >= 3 ? (float)Math.Abs(SignedArea()) : 0f,
                _closed, IsValidPolygon(), status);
            RefreshPlazaCenterChoices();
        }

        private JobHandle Render(JobHandle inputDeps)
        {
            var buffer = _overlay.GetBuffer(out var overlayDeps);
            var deps = JobHandle.CombineDependencies(inputDeps, overlayDeps);
            deps.Complete();
            ParkOverlay.Draw(buffer, _worldPoints, _closed, _hasHover, _hover,
                CanClose(), _hoverPoint, _dragPoint, _hoverEdge,
                _plannerMode, _entrances, _hoverEntrance, _pathPreview,
                _decorationPlan, IsPlaza,
                _buildIssues, LastSnap, HasSnapGuide, SnapGuide);
            if (_removeMode)
                ParkOverlay.DrawParkHighlight(buffer, _removeHighlightLines,
                    _removeHighlightPoints,
                    _removeSelectedPark != Unity.Entities.Entity.Null);
            return deps;
        }

        /// <summary>
        /// Complete polygon-editing state captured for one undo step, including
        /// world heights and the direction axes used by guide snapping.
        /// </summary>
        private sealed class Snapshot
        {
            internal float2[] Points;
            internal float3[] WorldPoints;
            internal float2[] Axes;
            internal bool Closed;
        }
    }
}
