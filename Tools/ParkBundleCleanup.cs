using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
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
        private EntityQuery _deletedSurfaceQuery;
        private EntityQuery _requestQuery;
        private EntityQuery _memberQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
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
        }

        [Preserve]
        protected override void OnUpdate()
        {
            CollectBulldozedSurface();
            DeleteNetworkEdges();
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
            for (var requestIndex = 0; requestIndex < requests.Length;
                 requestIndex++)
            {
                var park = requests[requestIndex];
                for (var memberIndex = 0; memberIndex < members.Length;
                     memberIndex++)
                {
                    var entity = members[memberIndex];
                    if (!EntityManager.HasComponent<Edge>(entity)
                        || EntityManager.GetComponentData<ParkPathMember>(entity).Park
                            != park) continue;
                    EntityManager.AddComponent<Deleted>(entity);
                    deleted++;
                }
            }
            if (deleted > 0)
                Mod.Log.Info($"ParkManager bundle cleanup marked {deleted} network "
                    + "edges in Modification2 before ReferencesSystem.");
        }
    }

    /// <summary>
    /// Removes ordinary members of a bulldozed park in bounded batches. Network
    /// nodes are left to Vanilla after their edges disappear; their logical
    /// membership is detached before the headless build record is removed.
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
            var marked = 0;
            var remaining = 0;
            var liveEdges = 0;
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                    != park) continue;
                if (EntityManager.HasComponent<Edge>(entity))
                {
                    liveEdges++;
                    continue;
                }
                if (EntityManager.HasComponent<Game.Net.Node>(entity)) continue;
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
                        + $"ordinary members; {remaining} wait for a later pass.");
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

            var detachedNodes = 0;
            for (var i = 0; i < members.Length; i++)
            {
                var entity = members[i];
                if (EntityManager.GetComponentData<ParkPathMember>(entity).Park
                        != park
                    || !EntityManager.HasComponent<Game.Net.Node>(entity)) continue;
                buffer.RemoveComponent<ParkPathMember>(entity);
                detachedNodes++;
            }
            buffer.AddComponent<Deleted>(park);
            Mod.Log.Info($"ParkManager bundle cleanup completed for {park}; "
                + $"detached {detachedNodes} surviving network nodes.");
        }
    }
}
