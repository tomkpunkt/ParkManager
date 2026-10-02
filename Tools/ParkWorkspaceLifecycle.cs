using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Owns the editor workspace lifecycle independently from ECS placement.
    /// A workspace either follows one persisted park record or represents a
    /// deliberately empty draft for the next park. This separation is what
    /// allows several completed parks to coexist in one city.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private bool _buildDecorationsAfterPaths;

        /// <summary>Commits both previews; the panel offers it once both are planned.</summary>
        internal void BuildPark()
        {
            if (BuildBusy || DecorationEditingLocked) return;
            if (_pathPlan == null || _decorationPlan == null)
            {
                PublishState(UiText.Of("status.planSurfaceAndDecorationsFirst"));
                return;
            }
            Mod.Log.Info($"ParkManager BUILD-START type={_selectedSiteKind} "
                + $"pathSeed={_pathPlan.Seed} decorationSeed={_decorationPlan.Seed} "
                + $"placements={_decorationPlan.Placements.Count} retry={HasBuiltPaths}.");
            // A failed furnishing build can be retried without duplicating paths.
            if (HasBuiltPaths)
            {
                BuildDecorations();
                return;
            }
            _buildDecorationsAfterPaths = true;
            BuildPaths();
            if (!PathBuildBusy) _buildDecorationsAfterPaths = false;
        }

        /// <summary>
        /// Detaches the completed park from the editor without deleting any of
        /// its Vanilla entities, then prepares an empty outline for the next
        /// independently persisted park.
        /// </summary>
        internal void FinishPark()
        {
            if (BuildBusy)
            {
                PublishState(UiText.Of("status.finishAfterBuild"));
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState(UiText.Of("status.noParkToFinish"));
                return;
            }

            var finishedRecord = _lastBuildRecord;
            MarkCompleted(finishedRecord);
            // The edit baseline only serves the open workspace. A finished
            // park keeps nothing but its record and the member tags that let
            // it be removed as a whole.
            if (EntityManager.HasComponent<ParkEditableBuildState>(finishedRecord))
                EntityManager.RemoveComponent<ParkEditableBuildState>(finishedRecord);
            _lastBuildRecord = Entity.Null;
            ResetWorkspaceDraft();
            PublishState(UiText.Of("status.parkFinished"));
            PublishPathBuildState(UiText.Of("path.newPark"));
            PublishDecorationState(UiText.Of("decoration.newPark"));
            Mod.Log.Info($"ParkManager finalized park {finishedRecord}.");
        }

        /// <summary>Clears transient editor data, never persisted park entities.</summary>
        private void ResetWorkspaceDraft()
        {
            _points.Clear();
            _worldPoints.Clear();
            _pointAxes.Clear();
            _undo.Clear();
            _entrances.Clear();
            _pathPreview.Clear();
            _buildIssues.Clear();
            _preflightWarning = null;
            _pathPlan = null;
            _plazaPlan = null;
            _decorationPlan = null;
            _pendingDecorations.Clear();
            _closed = false;
            _plannerMode = false;
            _dragPoint = -1;
            _hoverPoint = -1;
            _hoverEdge = -1;
            _hoverEntrance = -1;
            ResetEdgeDoubleClick();
            ClearSnapFeedback();
            PublishPlannerState();
        }

        private void MonitorExternalPathEdits(bool force = false)
        {
            if (BuildBusy) return;
            if (!HasBuiltPaths) return;
            if (!EntityManager.HasComponent<ParkEditableBuildState>(
                    _lastBuildRecord)) return;
            var frame = UnityEngine.Time.frameCount;
            if (!force && frame - _lastModificationCheckFrame
                < ModificationCheckIntervalFrames) return;
            _lastModificationCheckFrame = frame;

            var state = EntityManager.GetComponentData<ParkEditableBuildState>(
                _lastBuildRecord);
            var hash = ComputeMemberGeometryHash(_lastBuildRecord, out var count);
            if (state.Modified || count == state.MemberCount
                && hash == state.GeometryHash) return;

            state.Modified = true;
            EntityManager.SetComponentData(_lastBuildRecord, state);
            var seed = EntityManager.GetComponentData<ParkPathBuildMarker>(
                _lastBuildRecord).Seed;
            PublishState(UiText.Of("status.externallyEdited"));
            PublishPathBuildState(UiText.Of("path.manuallyEdited", count,
                state.MemberCount, seed));
            Mod.Log.Info($"ParkManager detected external edits on build {_lastBuildRecord}: "
                + $"members {state.MemberCount}->{count}, hash {state.GeometryHash}->{hash}.");
        }

        private long ComputeMemberGeometryHash(Entity park, out int memberCount)
        {
            memberCount = 0;
            ulong xor = 0;
            ulong sum = 0;
            using var entities = _pathMemberQuery.ToEntityArray(Allocator.TempJob);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                var member = EntityManager.GetComponentData<ParkPathMember>(entity);
                if (member.Park != park) continue;
                memberCount++;
                var item = HashValue(1469598103934665603UL,
                    unchecked((uint)member.ElementId));
                item = HashValue(item, (uint)member.Kind);
                item = HashValue(item, unchecked((uint)entity.Index));
                item = HashValue(item, unchecked((uint)entity.Version));

                if (EntityManager.HasComponent<Game.Net.Node>(entity))
                    item = HashFloat3(item, EntityManager
                        .GetComponentData<Game.Net.Node>(entity).m_Position);
                if (EntityManager.HasComponent<Game.Net.Curve>(entity))
                {
                    var bezier = EntityManager.GetComponentData<Game.Net.Curve>(
                        entity).m_Bezier;
                    item = HashFloat3(item, bezier.a);
                    item = HashFloat3(item, bezier.b);
                    item = HashFloat3(item, bezier.c);
                    item = HashFloat3(item, bezier.d);
                }
                if (EntityManager.HasBuffer<Game.Areas.Node>(entity))
                {
                    var nodes = EntityManager.GetBuffer<Game.Areas.Node>(entity, true);
                    item = HashValue(item, unchecked((uint)nodes.Length));
                    for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                        item = HashFloat3(item, nodes[nodeIndex].m_Position);
                }
                if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
                {
                    var transform = EntityManager
                        .GetComponentData<Game.Objects.Transform>(entity);
                    item = HashFloat3(item, transform.m_Position);
                    item = HashValue(item, math.asuint(transform.m_Rotation.value.x));
                    item = HashValue(item, math.asuint(transform.m_Rotation.value.y));
                    item = HashValue(item, math.asuint(transform.m_Rotation.value.z));
                    item = HashValue(item, math.asuint(transform.m_Rotation.value.w));
                }

                xor ^= RotateLeft(item, member.ElementId & 63);
                sum += item * 0x9e3779b97f4a7c15UL;
            }
            var result = HashValue(xor ^ sum, unchecked((uint)memberCount));
            return unchecked((long)result);
        }

        private static ulong HashFloat3(ulong hash, float3 value)
        {
            hash = HashValue(hash, math.asuint(value.x));
            hash = HashValue(hash, math.asuint(value.y));
            return HashValue(hash, math.asuint(value.z));
        }

        private static ulong HashValue(ulong hash, uint value)
            => (hash ^ value) * 1099511628211UL;

        private static ulong RotateLeft(ulong value, int count)
            => count == 0 ? value : value << count | value >> (64 - count);
    }
}
