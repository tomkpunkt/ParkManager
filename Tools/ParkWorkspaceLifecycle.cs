using Game.Common;
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
        private bool _freshDraftActive;

        /// <summary>
        /// Detaches the completed park from the editor without deleting any of
        /// its Vanilla entities, then prepares an empty outline for the next
        /// independently persisted park.
        /// </summary>
        internal void FinishPark()
        {
            if (PathBuildBusy || DecorationBuildBusy)
            {
                PublishState("Park kann erst nach Abschluss des Bauvorgangs fertiggestellt werden.");
                return;
            }
            if (!HasBuiltPaths)
            {
                PublishState("Es ist kein gebauter Park zum Fertigstellen aktiv.");
                return;
            }

            UpdateBuildReceipt(_lastBuildRecord, HasBuiltDecorations);
            var finishedRecord = _lastBuildRecord;
            if (EntityManager.HasComponent<ParkCompletedBundle>(finishedRecord))
                EntityManager.SetComponentData(finishedRecord,
                    new ParkCompletedBundle
                    {
                        Version = ParkCompletedBundle.CurrentVersion,
                    });
            else EntityManager.AddComponentData(finishedRecord,
                new ParkCompletedBundle
                {
                    Version = ParkCompletedBundle.CurrentVersion,
                });
            _lastBuildRecord = Entity.Null;
            _lastBuildIsLegacy = false;
            _restoredReceiptRecord = Entity.Null;
            _freshDraftActive = true;
            ResetWorkspaceDraft();
            PublishState("Park fertiggestellt. Zeichne den Umriss für den nächsten Park.");
            PublishPathBuildState("Neuer Park · noch keine Wege gebaut.");
            PublishDecorationState("Neuer Park · noch keine Ausstattung geplant.");
            PublishWorkspaceState();
            Mod.Log.Info($"ParkManager finalized park {finishedRecord}; "
                + $"{CountBuiltParks()} park records now coexist in the city.");
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
            _pathPlan = null;
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

        private int CountBuiltParks()
        {
            var count = 0;
            using (var records = _parkBuildQuery.ToEntityArray(Allocator.TempJob))
                for (var i = 0; i < records.Length; i++)
                    if (IsParkBuilderRecord(records[i])) count++;
            using (var records = _legacyBuildQuery.ToEntityArray(Allocator.TempJob))
                count += records.Length;
            return count;
        }

        private void PublishWorkspaceState()
            => _ui?.SetWorkspaceState(CountBuiltParks());

        private bool RecoverPathBuildRecord()
        {
            if (_freshDraftActive) return false;
            if (HasBuiltPaths)
            {
                RestoreBuildReceipt(_lastBuildRecord);
                PublishWorkspaceState();
                return true;
            }
            _lastBuildRecord = Entity.Null;
            _lastBuildIsLegacy = false;

            using (var records = _parkBuildQuery.ToEntityArray(Allocator.TempJob))
            {
                for (var i = 0; i < records.Length; i++)
                {
                    if (!IsParkBuilderRecord(records[i])) continue;
                    if (EntityManager.HasComponent<ParkCompletedBundle>(records[i]))
                        continue;
                    if (_lastBuildRecord == Entity.Null
                        || records[i].Index > _lastBuildRecord.Index)
                        _lastBuildRecord = records[i];
                }
            }

            if (_lastBuildRecord != Entity.Null)
            {
                var marker = EntityManager.GetComponentData<ParkPathBuildMarker>(
                    _lastBuildRecord);
                var state = EntityManager.GetComponentData<ParkEditableBuildState>(
                    _lastBuildRecord);
                RestoreBuildReceipt(_lastBuildRecord);
                PublishPathBuildState(state.Modified
                    ? $"Manuell bearbeitet · {CountMembers(_lastBuildRecord)} von "
                        + $"{state.MemberCount} Elementen · Seed {marker.Seed}"
                    : $"Gebaut · frei editierbar · {state.MemberCount} Elemente · "
                        + $"Seed {marker.Seed}");
                PublishWorkspaceState();
                return true;
            }

            using (var records = _legacyBuildQuery.ToEntityArray(Allocator.TempJob))
            {
                for (var i = 0; i < records.Length; i++)
                    if (_lastBuildRecord == Entity.Null
                        || records[i].Index > _lastBuildRecord.Index)
                        _lastBuildRecord = records[i];
            }
            PublishWorkspaceState();
            if (_lastBuildRecord == Entity.Null) return false;
            _lastBuildIsLegacy = true;
            PublishPathBuildState("Alter gebundener Testbau erkannt · vor Neubau entfernen");
            return true;
        }

        private void MonitorExternalPathEdits(bool force = false)
        {
            if (PathBuildBusy || DecorationBuildBusy) return;
            if (!HasBuiltPaths && !RecoverPathBuildRecord()) return;
            if (_lastBuildIsLegacy
                || !EntityManager.HasComponent<ParkEditableBuildState>(
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
            PublishState("Der gebaute Park wurde mit einem externen Werkzeug verändert. "
                + "ParkManager behält diese Änderungen und überschreibt sie nicht.");
            PublishPathBuildState($"Manuell bearbeitet · {count} von "
                + $"{state.MemberCount} Elementen · Seed {seed}");
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
