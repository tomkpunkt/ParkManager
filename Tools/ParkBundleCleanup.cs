using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace ParkManager.Tools
{
    /// <summary>
    /// First stage of grouped park deletion. A deletion is requested only by
    /// ParkManager itself (remove mode, "Remove park", aborted builds) through
    /// a <see cref="ParkBundleDeletionRequest"/> on the park record. Deleting
    /// or replacing a single element with another tool, including the ground
    /// surface, never removes the rest of the park.
    /// Network edges are marked in Modification2, before Vanilla's
    /// ReferencesSystem runs in Modification2B; doing this later can leave dead
    /// edge references in connected-node buffers.
    /// </summary>
    public sealed partial class ParkBundleNetworkCleanupSystem : GameSystemBase
    {
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _requestQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ParkPathBuildMarker>(),
                    ComponentType.ReadOnly<ParkBundleDeletionRequest>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _memberQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ParkPathMember>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            DeleteNetworkEdges();
        }

        private void DeleteNetworkEdges()
        {
            if (_requestQuery.IsEmptyIgnoreFilter
                || _memberQuery.IsEmptyIgnoreFilter) return;
            using var requests = _requestQuery.ToEntityArray(Allocator.Temp);
            using var members = _memberQuery.ToEntityArray(Allocator.Temp);
            var deleted = 0;
            var adoptedEndpoints = 0;
            for (var requestIndex = 0; requestIndex < requests.Length;
                 requestIndex++)
            {
                var park = requests[requestIndex];
                for (var memberIndex = 0; memberIndex < members.Length;
                     memberIndex++)
                {
                    var entity = members[memberIndex];
                    if (!EntityManager.HasComponent<Edge>(entity)) continue;
                    var member = EntityManager.GetComponentData<ParkPathMember>(
                        entity);
                    if (member.Park != park) continue;
                    var edge = EntityManager.GetComponentData<Edge>(entity);
                    adoptedEndpoints += AdoptEndpoint(edge.m_Start, member);
                    adoptedEndpoints += AdoptEndpoint(edge.m_End, member);
                    EntityManager.AddComponent<Deleted>(entity);
                    deleted++;
                }
            }
            if (deleted > 0)
                Mod.Log.Info($"ParkManager bundle cleanup marked {deleted} network "
                    + "edges in Modification2 before ReferencesSystem; adopted "
                    + $"{adoptedEndpoints} materialized endpoint nodes.");
        }

        /// <summary>
        /// CS2 can materialize endpoint nodes that were not present in the
        /// creation definitions. Attach those real endpoints to the same park
        /// immediately before deleting their edge so the later cleanup pass
        /// can remove them after ReferencesSystem has refreshed ConnectedEdge.
        /// Existing membership is never overwritten, which protects nodes
        /// shared with another completed park.
        /// </summary>
        private int AdoptEndpoint(Entity node, ParkPathMember edgeMember)
        {
            if (node == Entity.Null || !EntityManager.Exists(node)
                || EntityManager.HasComponent<Deleted>(node)
                || !EntityManager.HasComponent<Game.Net.Node>(node)
                || EntityManager.HasComponent<ParkPathMember>(node)) return 0;

            EntityManager.AddComponentData(node, new ParkPathMember
            {
                Park = edgeMember.Park,
                ElementId = edgeMember.ElementId,
                Kind = ParkPathMemberKind.Node,
            });
            return 1;
        }

    }

    /// <summary>
    /// Removes ordinary members and ParkManager-owned network nodes of a
    /// bulldozed park in bounded batches. Gate nodes merged into an existing
    /// street/path were never tagged as members. A tagged node is preserved
    /// only if a later external edit connected it to a non-park edge.
    /// Also drops the record of a completed park once its last element was
    /// deleted by hand, so no empty park data stays in the save.
    /// </summary>
    public sealed partial class ParkBundleCleanupSystem : GameSystemBase
    {
        private const int MembersPerPass = 32;
        private ModificationBarrier3 _barrier;
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;
        private EntityQuery _deletedMemberQuery;
        private EntityQuery _completedParkQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _barrier = World.GetOrCreateSystemManaged<ModificationBarrier3>();
            _deletedMemberQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ParkPathMember>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
            // Only completed parks: a record under construction has no
            // members yet and must not be mistaken for an empty park.
            _completedParkQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ParkPathBuildMarker>(),
                    ComponentType.ReadOnly<ParkCompletedBundle>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<ParkBundleDeletionRequest>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _requestQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ParkPathBuildMarker>(),
                    ComponentType.ReadWrite<ParkBundleDeletionRequest>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            _memberQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<ParkPathMember>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Members only disappear in frames that delete one, so the sweep
            // costs nothing while the city is left alone.
            if (!_deletedMemberQuery.IsEmptyIgnoreFilter) RemoveEmptyParks();
            if (_requestQuery.IsEmptyIgnoreFilter) return;
            using var requests = _requestQuery.ToEntityArray(Allocator.Temp);
            using var members = _memberQuery.ToEntityArray(Allocator.Temp);
            var buffer = _barrier.CreateCommandBuffer();

            for (var requestIndex = 0; requestIndex < requests.Length;
                 requestIndex++)
                ProcessBundle(requests[requestIndex], members, buffer);
        }

        private void RemoveEmptyParks()
        {
            if (_completedParkQuery.IsEmptyIgnoreFilter) return;
            var populated = new System.Collections.Generic.HashSet<Entity>();
            using (var members = _memberQuery.ToEntityArray(Allocator.Temp))
                for (var i = 0; i < members.Length; i++)
                    populated.Add(EntityManager
                        .GetComponentData<ParkPathMember>(members[i]).Park);
            using var parks = _completedParkQuery.ToEntityArray(Allocator.Temp);
            var buffer = _barrier.CreateCommandBuffer();
            for (var i = 0; i < parks.Length; i++)
            {
                if (populated.Contains(parks[i])) continue;
                buffer.AddComponent<Deleted>(parks[i]);
                Mod.Log.Info($"ParkManager removed the record of park {parks[i]}: "
                    + "all of its elements were deleted individually.");
            }
        }

        private void ProcessBundle(Entity park, NativeArray<Entity> members,
            EntityCommandBuffer buffer)
        {
            var liveEdges = CountLiveParkEdges(park, members);
            var marked = 0;
            var remaining = 0;
            var deletedNodes = 0;
            var detachedExternalNodes = 0;
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                    != park) continue;
                if (EntityManager.HasComponent<Edge>(entity)) continue;
                if (EntityManager.HasComponent<Game.Net.Node>(entity))
                {
                    // Wait until ReferencesSystem has consumed the Deleted
                    // park edges and updated ConnectedEdge buffers.
                    if (liveEdges > 0)
                    {
                        remaining++;
                        continue;
                    }
                    if (HasLiveExternalEdge(entity, park))
                    {
                        buffer.RemoveComponent<ParkPathMember>(entity);
                        detachedExternalNodes++;
                        continue;
                    }
                    if (marked >= MembersPerPass)
                    {
                        remaining++;
                        continue;
                    }
                    buffer.AddComponent<Deleted>(entity);
                    marked++;
                    deletedNodes++;
                    continue;
                }
                if (marked >= MembersPerPass)
                {
                    remaining++;
                    continue;
                }
                buffer.AddComponent<Deleted>(entity);
                marked++;
            }

            var request = EntityManager
                .GetComponentData<ParkBundleDeletionRequest>(park);
            if (marked > 0 || remaining > 0 || liveEdges > 0)
            {
                request.EmptyPasses = 0;
                EntityManager.SetComponentData(park, request);
                if (marked > 0)
                    Mod.Log.Info($"ParkManager bundle cleanup marked {marked} "
                        + $"ordinary members/network nodes ({deletedNodes} nodes); "
                        + $"{remaining} wait for a later pass; "
                        + $"{detachedExternalNodes} externally connected nodes preserved.");
                return;
            }

            // One completely empty pass separates the last member batch from
            // record removal and avoids stacking both deletion cascades.
            request.EmptyPasses++;
            if (request.EmptyPasses < 2)
            {
                EntityManager.SetComponentData(park, request);
                return;
            }

            buffer.AddComponent<Deleted>(park);
            Mod.Log.Info($"ParkManager bundle cleanup completed for {park}; "
                + "all unshared network nodes were removed.");
        }

        private int CountLiveParkEdges(Entity park, NativeArray<Entity> members)
        {
            var count = 0;
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                        == park
                    && EntityManager.HasComponent<Edge>(entity)) count++;
            }
            return count;
        }

        private bool HasLiveExternalEdge(Entity node, Entity park)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (var i = 0; i < connected.Length; i++)
            {
                var edge = connected[i].m_Edge;
                if (edge == Entity.Null || !EntityManager.Exists(edge)
                    || EntityManager.HasComponent<Deleted>(edge)) continue;
                if (EntityManager.HasComponent<ParkPathMember>(edge)
                    && EntityManager.GetComponentData<ParkPathMember>(edge).Park
                        == park) continue;
                return true;
            }
            return false;
        }
    }
}
