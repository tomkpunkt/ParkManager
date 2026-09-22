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

        internal void SetSiteKind(int value)
        {
            if (HasBuiltPaths || PathBuildBusy || DecorationBuildBusy) return;
            _selectedSiteKind = value == (int)ProceduralSiteKind.Plaza
                ? ProceduralSiteKind.Plaza
                : ProceduralSiteKind.Park;
            _ui?.SetSiteType((int)_selectedSiteKind);
            PublishState(_selectedSiteKind == ProceduralSiteKind.Plaza
                ? "Plaza als Flächentyp ausgewählt."
                : "Park als Flächentyp ausgewählt.");
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
            ConfigureSnapping();
            if (!RecoverPathBuildRecord())
                PublishPathBuildState("Noch keine Testwege gebaut.");
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
            RecoverPathBuildRecord();
            PublishWorkspaceState();
            MonitorExternalPathEdits(true);
            PublishState("Linksklick setzt Punkte; ersten Punkt anklicken zum Schließen.");
            Mod.Log.Info("ParkManager polygon tool activated.");
        }

        [Preserve]
        protected override void OnStopRunning()
        {
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
            if (ProcessDecorationPlacement()) return Render(deps);
            if (ProcessPathPlacement()) return Render(deps);

            var inputAllowed = WorldInputAllowed();
            _hasHover = inputAllowed && TryGetGroundPoint(out _hover);
            UpdateHoverTargets();

            if (EscapePressed())
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return deps;
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
            if (!_hasHover || HasBuiltPaths || PathBuildBusy
                || DecorationBuildBusy) return;
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
                            PublishState("Teilung würde ein ungültiges Polygon erzeugen.");
                        }
                        else PublishState("Polygonseite per Doppelklick geteilt.");
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
                    ? "Polygon geschlossen und gültig; Punkte können verschoben werden."
                    : "Polygon geschlossen, aber geometrisch ungültig.");
                return;
            }

            var point = _hover.xz;
            if (_points.Count == 0 || math.distancesq(point, _points[_points.Count - 1]) > 0.0625f)
            {
                _points.Add(point);
                _worldPoints.Add(_hover);
                StoreSnapAxis(_points.Count - 1);
            }
            PublishState("Weitere Punkte setzen oder den ersten Punkt anklicken.");
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
                PublishState("Ungültige Verschiebung verworfen.");
            }
            else PublishState("Punkt verschoben.");
            _dragPoint = -1;
        }

        private void HandleRightClick()
        {
            if (_points.Count == 0 || HasBuiltPaths || PathBuildBusy
                || DecorationBuildBusy) return;
            PushUndo();
            InvalidatePlannerData();
            if (_hoverPoint >= 0)
            {
                _points.RemoveAt(_hoverPoint);
                _worldPoints.RemoveAt(_hoverPoint);
                RemoveSnapAxis(_hoverPoint);
                if (_points.Count < 3) _closed = false;
                PublishState("Punkt entfernt.");
            }
            else if (_closed)
            {
                _closed = false;
                PublishState("Polygon geöffnet.");
            }
            else
            {
                var last = _points.Count - 1;
                _points.RemoveAt(last);
                _worldPoints.RemoveAt(last);
                RemoveSnapAxis(last);
                PublishState("Letzten Punkt entfernt.");
            }
        }

        internal void ClearPolygon()
        {
            if (HasBuiltPaths || PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Der Umriss eines gebauten Parks ist gesperrt. "
                    + "Zum Neuzeichnen zuerst den Park entfernen oder fertigstellen.");
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
            PublishState("Polygon zurückgesetzt; neuen ersten Punkt setzen.");
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
                var distance = DistanceToSegmentSquared(cursor, _points[i],
                    _points[(i + 1) % _points.Count]);
                if (distance < bestEdge) { bestEdge = distance; _hoverEdge = i; }
            }
        }

        internal void TogglePlannerMode()
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            if (_plannerMode)
            {
                if (HasBuiltPaths)
                {
                    PublishState("Der Umriss eines gebauten Parks bleibt gesperrt. "
                        + "Zum Bearbeiten zuerst den Park entfernen.");
                    return;
                }
                _plannerMode = false;
                PublishState("Zeichenmodus aktiv; Polygon kann bearbeitet werden.");
                PublishPlannerState();
                return;
            }

            if (!_closed || !IsValidPolygon())
            {
                PublishState("Parkplaner benötigt ein geschlossenes, gültiges Polygon.");
                return;
            }

            _dragPoint = -1;
            _plannerMode = true;
            PublishState("Parkplaner aktiv; Linksklick auf eine Kante setzt einen Eingang.");
            PublishPlannerState();
        }

        private void HandlePlannerClick()
        {
            if (!_hasHover || _hoverEdge < 0 || HasBuiltPaths
                || PathBuildBusy || DecorationBuildBusy) return;
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
                    PublishState("Hier ist bereits ein Eingang markiert.");
                    return;
                }
            _entrances.Add(entrance);
            _pathPlan = null;
            _pathPreview.Clear();
            _decorationPlan = null;
            PublishDecorationState("Noch keine Ausstattung geplant.");
            PublishState("Eingang markiert; weitere Eingänge setzen oder Wege erzeugen.");
            PublishPlannerState();
        }

        private void HandlePlannerRightClick()
        {
            if (HasBuiltPaths || PathBuildBusy || DecorationBuildBusy) return;
            if (_hoverEntrance < 0 || _hoverEntrance >= _entrances.Count)
            {
                PublishState("Zum Entfernen direkt über einen Eingang hovern.");
                return;
            }
            _entrances.RemoveAt(_hoverEntrance);
            _hoverEntrance = -1;
            _pathPlan = null;
            _pathPreview.Clear();
            _decorationPlan = null;
            PublishDecorationState("Noch keine Ausstattung geplant.");
            PublishState("Eingang entfernt; Wegentwurf zurückgesetzt.");
            PublishPlannerState();
        }

        internal void GeneratePaths()
        {
            if (PathBuildBusy || DecorationBuildBusy || HasBuiltPaths)
            {
                PublishState(HasBuiltPaths
                    ? "Zum Neuplanen zuerst den gebauten Park entfernen."
                    : "Der aktuelle Bau wird noch von CS2 verarbeitet.");
                return;
            }
            if (!_plannerMode || !_closed || !IsValidPolygon())
            {
                PublishState("Zuerst den Parkplaner für ein gültiges Polygon öffnen.");
                return;
            }
            if (_entrances.Count == 0)
            {
                PublishState("Mindestens einen Eingang an einer Polygonkante markieren.");
                return;
            }

            _pathPreview.Clear();
            var hub2 = FindInteriorHub();
            var entrancePoints = new List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++)
                entrancePoints.Add(_entrances[i].xz);
            var familySeed = Guid.NewGuid().GetHashCode() & int.MaxValue;
            if (familySeed == 0) familySeed = 1;
            ParkPathPlan best = null;
            var bestScore = double.MaxValue;
            for (var i = 0; i < PathCandidateCount; i++)
            {
                var seed = (int)(((long)familySeed + i * 104729L)
                    % int.MaxValue);
                if (seed == 0) seed = 1;
                var candidate = ParkPathPlanner.Generate(_points, entrancePoints,
                    hub2, seed);
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
                var decorationSeed = unchecked(_pathPlan.Seed * 1103515245 + 12345)
                    & int.MaxValue;
                if (decorationSeed == 0) decorationSeed = 1;
                GenerateDecorationPlan(decorationSeed);
            }
            PublishState(_pathPlan.Edges.Count > 0
                ? $"Beste von {PathCandidateCount} Varianten erzeugt: "
                    + $"Seed {_pathPlan.Seed}, {_pathPlan.Edges.Count} Segmente, "
                    + $"{_pathPlan.TotalLength:F0} m, Bewertung {bestScore:F1}."
                : "Für diese Eingänge konnte kein zusammenhängender Weg gefunden werden.");
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
            var min = _points[0];
            var max = _points[0];
            for (var i = 1; i < _points.Count; i++)
            {
                min = math.min(min, _points[i]);
                max = math.max(max, _points[i]);
            }
            var best = _points[0];
            var bestClearance = -1f;
            const int steps = 16;
            for (var y = 0; y <= steps; y++)
            for (var x = 0; x <= steps; x++)
            {
                var candidate = math.lerp(min, max,
                    new float2((float)x / steps, (float)y / steps));
                if (!PointInside(candidate)) continue;
                var clearance = float.MaxValue;
                for (var i = 0; i < _points.Count; i++)
                    clearance = math.min(clearance, DistanceToSegmentSquared(candidate,
                        _points[i], _points[(i + 1) % _points.Count]));
                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = candidate;
                }
            }
            return best;
        }

        private bool PointInside(float2 point)
        {
            var inside = false;
            for (int i = 0, j = _points.Count - 1; i < _points.Count; j = i++)
            {
                var a = _points[i]; var b = _points[j];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                       / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private void InvalidatePlannerData()
        {
            _entrances.Clear();
            _pathPlan = null;
            _pathPreview.Clear();
            _decorationPlan = null;
            PublishDecorationState("Noch keine Ausstattung geplant.");
            PublishPlannerState();
        }

        private void PublishPlannerState()
            => _ui?.SetPlannerState(_plannerMode, _entrances.Count,
                _pathPlan != null && _pathPlan.Edges.Count > 0);

        private bool CanClose() => !_closed && _points.Count >= 3 && _hasHover
            && math.distancesq(_hover.xz, _points[0]) <= CloseDistance * CloseDistance;

        private bool IsValidPolygon()
        {
            if (!_closed || _points.Count < 3 || Math.Abs(SignedArea()) < 4.0) return false;
            for (var i = 0; i < _points.Count; i++)
            for (var j = i + 1; j < _points.Count; j++)
            {
                if (j == i || j == (i + 1) % _points.Count
                    || i == (j + 1) % _points.Count) continue;
                if (SegmentsCross(_points[i], _points[(i + 1) % _points.Count],
                    _points[j], _points[(j + 1) % _points.Count])) return false;
            }
            return true;
        }

        private double SignedArea()
        {
            double sum = 0;
            for (var i = 0; i < _points.Count; i++)
            {
                var a = _points[i]; var b = _points[(i + 1) % _points.Count];
                sum += (double)a.x * b.y - (double)b.x * a.y;
            }
            return sum * 0.5;
        }

        private static bool SegmentsCross(float2 a, float2 b, float2 c, float2 d)
        {
            var ab1 = Cross(a, b, c); var ab2 = Cross(a, b, d);
            var cd1 = Cross(c, d, a); var cd2 = Cross(c, d, b);
            return ab1 * ab2 < 0f && cd1 * cd2 < 0f;
        }

        private static float Cross(float2 a, float2 b, float2 p)
            => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static float DistanceToSegmentSquared(float2 p, float2 a, float2 b)
        {
            var ab = b - a;
            var length = math.lengthsq(ab);
            if (length < 0.0001f) return math.distancesq(p, a);
            var t = math.clamp(math.dot(p - a, ab) / length, 0f, 1f);
            return math.distancesq(p, a + ab * t);
        }

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
            PublishState("Letzten Schritt rückgängig gemacht.");
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
            => _ui?.SetPolygonState(_points.Count,
                _points.Count >= 3 ? (float)Math.Abs(SignedArea()) : 0f,
                _closed, IsValidPolygon(), status);

        private JobHandle Render(JobHandle inputDeps)
        {
            var buffer = _overlay.GetBuffer(out var overlayDeps);
            var deps = JobHandle.CombineDependencies(inputDeps, overlayDeps);
            deps.Complete();
            ParkOverlay.Draw(buffer, _worldPoints, _closed, _hasHover, _hover,
                CanClose(), _hoverPoint, _dragPoint, _hoverEdge, -1,
                _plannerMode, _entrances, _hoverEntrance, _pathPreview,
                _decorationPlan, LastSnap, HasSnapGuide, SnapGuide);
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
