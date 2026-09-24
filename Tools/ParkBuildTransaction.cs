using System.Collections.Generic;
using Game.Common;
using Unity.Entities;

namespace ParkManager.Tools
{
    /// <summary>Tracks definitions created by the active construction attempt.</summary>
    public sealed partial class ParkToolSystem
    {
        private readonly HashSet<Entity> _buildDefinitions = new HashSet<Entity>();

        private Entity CreateBuildDefinition()
        {
            var entity = EntityManager.CreateEntity();
            _buildDefinitions.Add(entity);
            return entity;
        }

        private int DiscardBuildDefinitions()
        {
            var removed = 0;
            foreach (var entity in _buildDefinitions)
            {
                if (!EntityManager.Exists(entity)
                    || EntityManager.HasComponent<Deleted>(entity)) continue;
                EntityManager.AddComponent<Deleted>(entity);
                removed++;
            }
            _buildDefinitions.Clear();
            return removed;
        }
    }
}
