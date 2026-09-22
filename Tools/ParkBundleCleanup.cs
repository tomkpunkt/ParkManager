using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace ParkManager.Tools
{
    /// <summary>
    /// Starts grouped park deletion only when the completed park's surface is
    /// bulldozed. All other members remain independently movable and deletable.
    /// Network edges are marked in Modification2, before Vanilla's
    /// ReferencesSystem runs in Modification2B; doing this later can leave dead
    /// edge references in connected-node buffers.
    /// </summary>
    public sealed partial class ParkBundleNetworkCleanupSystem : GameSystemBase
    {
        private const string ParkPathPrefabName = "PedestrianPathWide01";
        private EntityQuery _deletedSurfaceQuery;
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;
        private EntityQuery _legacyOrphanNodeQuery;
        private PrefabSystem _prefabSystem;
        private int _nextOrphanScanFrame;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _deletedSurfaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<ParkPathMember>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
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
            _legacyOrphanNodeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<ConnectedEdge>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<ParkPathMember>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            CollectBulldozedSurface();
            DeleteNetworkEdges();
            DeleteLegacyOrphanNodes();
        }

        private void CollectBulldozedSurface()
        {
            if (_deletedSurfaceQuery.IsEmptyIgnoreFilter) return;
            using var deletedSurfaces = _deletedSurfaceQuery
                .ToEntityArray(Allocator.Temp);
            for (var i = 0; i < deletedSurfaces.Length; i++)
            {
                var member = EntityManager.GetComponentData<ParkPathMember>(
                    deletedSurfaces[i]);
                if (member.Kind != ParkPathMemberKind.ParkSurface
                    || member.Park == Entity.Null
                    || !EntityManager.Exists(member.Park)
                    || EntityManager.HasComponent<Deleted>(member.Park)
                    || !EntityManager.HasComponent<ParkCompletedBundle>(
                        member.Park)
                    || EntityManager.HasComponent<ParkBundleDeletionRequest>(
                        member.Park)) continue;
                EntityManager.AddComponentData(member.Park,
                    new ParkBundleDeletionRequest());
                Mod.Log.Info($"ParkManager park {member.Park} was bulldozed "
                    + $"through surface anchor {deletedSurfaces[i]}; grouped "
                    + "cleanup started while ordinary members stay independently editable.");
            }
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

        /// <summary>
        /// Repairs nodes detached by 0.5 builds before the cleanup fix. A
        /// permanent network node with this exact ParkManager path prefab and
        /// no live edge cannot represent a usable player network element.
        /// </summary>
        private void DeleteLegacyOrphanNodes()
        {
            if (UnityEngine.Time.frameCount < _nextOrphanScanFrame) return;
            _nextOrphanScanFrame = UnityEngine.Time.frameCount + 120;
            if (_legacyOrphanNodeQuery.IsEmptyIgnoreFilter) return;

            using var nodes = _legacyOrphanNodeQuery.ToEntityArray(Allocator.Temp);
            var deleted = 0;
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                var prefabEntity = EntityManager.GetComponentData<PrefabRef>(node)
                    .m_Prefab;
                if (!_prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity,
                        out var prefab)
                    || prefab == null || !prefab.isBuiltin
                    || prefab.name != ParkPathPrefabName
                    || HasLiveEdge(node)) continue;
                EntityManager.AddComponent<Deleted>(node);
                deleted++;
            }
            if (deleted > 0)
                Mod.Log.Info($"ParkManager removed {deleted} legacy orphan path "
                    + "nodes left by an earlier bundle cleanup.");
        }

        private bool HasLiveEdge(Entity node)
        {
            var connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (var i = 0; i < connected.Length; i++)
            {
                var edge = connected[i].m_Edge;
                if (edge != Entity.Null && EntityManager.Exists(edge)
                    && !EntityManager.HasComponent<Deleted>(edge)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Removes ordinary members and ParkManager-owned network nodes of a
    /// bulldozed park in bounded batches. Gate nodes merged into an existing
    /// street/path were never tagged as members. A tagged node is preserved
    /// only if a later external edit connected it to a non-park edge.
    /// </summary>
    public sealed partial class ParkBundleCleanupSystem : GameSystemBase
    {
        private const int MembersPerPass = 32;
        private ModificationBarrier3 _barrier;
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            _barrier = World.GetOrCreateSystemManaged<ModificationBarrier3>();
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
            if (_requestQuery.IsEmptyIgnoreFilter) return;
            using var requests = _requestQuery.ToEntityArray(Allocator.Temp);
            using var members = _memberQuery.ToEntityArray(Allocator.Temp);
            var buffer = _barrier.CreateCommandBuffer();

            for (var requestIndex = 0; requestIndex < requests.Length;
                 requestIndex++)
                ProcessBundle(requests[requestIndex], members, buffer);
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
