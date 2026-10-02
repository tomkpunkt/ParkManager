using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Removes a built park or plaza as a whole. The player picks the park by
    /// clicking any of its elements on the map; the park is resolved through
    /// the <see cref="ParkPathMember"/> tag every built element already
    /// carries. No outline, plan or other preview data is kept for this, and
    /// the park no longer depends on its ground surface as a deletion anchor.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private const float RemovePickDistance = 4f;
        private const float RemoveHoverRefreshDistance = 0.75f;
        private const int RemoveRefreshFrames = 20;
        private const int RemoveCurveSegments = 4;

        private bool _removeMode;
        private Entity _removeHoverPark = Entity.Null;
        private Entity _removeSelectedPark = Entity.Null;
        private float2 _removeHoverChecked = new float2(float.NaN, float.NaN);
        private int _removeHoverFrame;
        private Entity _removeHighlightPark = Entity.Null;
        private int _removeHighlightFrame;
        private int _removeSelectedCount;
        private readonly List<float3> _removeHighlightLines = new List<float3>();
        private readonly List<float3> _removeHighlightPoints = new List<float3>();

        /// <summary>
        /// The remove mode needs an empty workspace: while an outline is drawn
        /// or an unfinished build is open, map clicks belong to that park.
        /// </summary>
        internal void SetRemoveMode(bool enabled)
        {
            if (_removeMode == enabled) return;
            if (enabled)
            {
                if (BuildBusy)
                {
                    PublishState(UiText.Of("status.buildBusy"));
                    return;
                }
                if (HasBuiltPaths || _points.Count > 0)
                {
                    PublishState(UiText.Of("status.removeModeNeedsEmptyWorkspace"));
                    return;
                }
                _plannerMode = false;
                _dragPoint = -1;
                ClearSnapFeedback();
                PublishPlannerState();
            }
            _removeMode = enabled;
            ClearRemoveSelection();
            PublishRemoveState();
            PublishState(enabled
                ? UiText.Of("status.removeModeActive")
                : UiText.Of("status.drawHint"));
        }

        /// <summary>Deletes the selected park through the ordered bundle cleanup.</summary>
        internal void RemoveSelectedPark()
        {
            if (!_removeMode || BuildBusy) return;
            var park = _removeSelectedPark;
            if (!IsRemovablePark(park))
            {
                ClearRemoveSelection();
                PublishRemoveState();
                return;
            }
            var count = CountMembers(park);
            RequestBundleDeletion(park);
            Mod.Log.Info($"ParkManager removes park {park} with {count} "
                + "elements on player request.");
            ClearRemoveSelection();
            PublishRemoveState();
            PublishState(UiText.Of("status.parkRemoved", count));
        }

        /// <summary>One frame of the remove mode: hover, pick and cancel.</summary>
        private void UpdateRemoveMode(bool inputAllowed)
        {
            if (_removeSelectedPark != Entity.Null
                && !IsRemovablePark(_removeSelectedPark))
            {
                _removeSelectedPark = Entity.Null;
                PublishRemoveState();
            }

            if (!_hasHover) _removeHoverPark = Entity.Null;
            else
            {
                var point = _hover.xz;
                var frame = UnityEngine.Time.frameCount;
                // Scanning every park element is only needed after the cursor
                // moved; the periodic refresh follows edits by other tools.
                if (!math.all(math.isfinite(_removeHoverChecked))
                    || math.distancesq(point, _removeHoverChecked)
                        > RemoveHoverRefreshDistance * RemoveHoverRefreshDistance
                    || frame - _removeHoverFrame > RemoveRefreshFrames)
                {
                    _removeHoverChecked = point;
                    _removeHoverFrame = frame;
                    _removeHoverPark = FindParkAt(point);
                }
            }

            if (inputAllowed && applyAction != null
                && applyAction.WasPressedThisFrame())
            {
                // A click on free ground clears the selection.
                _removeSelectedPark = _removeHoverPark;
                PublishRemoveState();
                PublishState(_removeSelectedPark != Entity.Null
                    ? UiText.Of("status.removeParkSelected", _removeSelectedCount)
                    : UiText.Of("status.removeModeActive"));
            }

            var highlighted = _removeSelectedPark != Entity.Null
                ? _removeSelectedPark : _removeHoverPark;
            RefreshRemoveHighlight(highlighted);
        }

        private void ClearRemoveSelection()
        {
            _removeHoverPark = Entity.Null;
            _removeSelectedPark = Entity.Null;
            _removeHighlightPark = Entity.Null;
            _removeHoverChecked = new float2(float.NaN, float.NaN);
            _removeHighlightLines.Clear();
            _removeHighlightPoints.Clear();
            _removeSelectedCount = 0;
        }

        private void PublishRemoveState()
        {
            _removeSelectedCount = _removeSelectedPark != Entity.Null
                ? CountMembers(_removeSelectedPark) : 0;
            _ui?.SetRemoveState(_removeMode, _removeSelectedCount);
        }

        private bool IsRemovablePark(Entity park)
            => park != Entity.Null && EntityManager.Exists(park)
                && EntityManager.HasComponent<ParkPathBuildMarker>(park)
                && !EntityManager.HasComponent<Deleted>(park)
                && !EntityManager.HasComponent<ParkBundleDeletionRequest>(park);

        /// <summary>
        /// Resolves the park whose nearest element lies under the cursor.
        /// Areas count when the cursor is inside them; paths and objects when
        /// it is within <see cref="RemovePickDistance"/>.
        /// </summary>
        private Entity FindParkAt(float2 point)
        {
            var best = Entity.Null;
            var bestDistance = float.MaxValue;
            using var members = _pathMemberQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                var distance = DistanceToMember(entity, point);
                if (distance >= bestDistance) continue;
                var park = EntityManager.GetComponentData<ParkPathMember>(entity).Park;
                if (!IsRemovablePark(park)) continue;
                bestDistance = distance;
                best = park;
            }
            return best;
        }

        private float DistanceToMember(Entity entity, float2 point)
        {
            if (EntityManager.HasBuffer<Game.Areas.Node>(entity))
            {
                var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, true);
                return PointInsideArea(nodes, point) ? 0f : float.MaxValue;
            }
            float distance;
            if (EntityManager.HasComponent<Game.Net.Curve>(entity))
                distance = MathUtils.Distance(EntityManager
                    .GetComponentData<Game.Net.Curve>(entity).m_Bezier.xz,
                    point, out _);
            else if (EntityManager.HasComponent<Game.Net.Node>(entity))
                distance = math.distance(point, EntityManager
                    .GetComponentData<Game.Net.Node>(entity).m_Position.xz);
            else if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
                distance = math.distance(point, EntityManager
                    .GetComponentData<Game.Objects.Transform>(entity).m_Position.xz);
            else return float.MaxValue;
            return distance <= RemovePickDistance ? distance : float.MaxValue;
        }

        private static bool PointInsideArea(DynamicBuffer<Game.Areas.Node> nodes,
            float2 point)
        {
            var inside = false;
            for (int i = 0, j = nodes.Length - 1; i < nodes.Length; j = i++)
            {
                var a = nodes[i].m_Position.xz;
                var b = nodes[j].m_Position.xz;
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// Collects the outline of every element of the park for the overlay.
        /// Rebuilt when the park changes and periodically while it is shown,
        /// so deletions by other tools disappear from the highlight.
        /// </summary>
        private void RefreshRemoveHighlight(Entity park)
        {
            var frame = UnityEngine.Time.frameCount;
            if (park == _removeHighlightPark
                && frame - _removeHighlightFrame <= RemoveRefreshFrames) return;
            _removeHighlightPark = park;
            _removeHighlightFrame = frame;
            _removeHighlightLines.Clear();
            _removeHighlightPoints.Clear();
            if (park == Entity.Null) return;

            using var members = _pathMemberQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                    != park) continue;
                if (EntityManager.HasBuffer<Game.Areas.Node>(entity))
                {
                    var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, true);
                    for (int n = 0, p = nodes.Length - 1; n < nodes.Length; p = n++)
                    {
                        _removeHighlightLines.Add(nodes[p].m_Position);
                        _removeHighlightLines.Add(nodes[n].m_Position);
                    }
                }
                else if (EntityManager.HasComponent<Game.Net.Curve>(entity))
                {
                    var bezier = EntityManager
                        .GetComponentData<Game.Net.Curve>(entity).m_Bezier;
                    var previous = bezier.a;
                    for (var step = 1; step <= RemoveCurveSegments; step++)
                    {
                        var next = MathUtils.Position(bezier,
                            (float)step / RemoveCurveSegments);
                        _removeHighlightLines.Add(previous);
                        _removeHighlightLines.Add(next);
                        previous = next;
                    }
                }
                else if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
                    _removeHighlightPoints.Add(EntityManager
                        .GetComponentData<Game.Objects.Transform>(entity).m_Position);
            }
        }
    }
}
