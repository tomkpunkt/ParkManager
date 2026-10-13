using Game.Common;
using Game.Tools;
using Game.Simulation;
using Unity.Mathematics;
using Unity.Entities;

namespace ParkManager.Tools
{
    public sealed partial class ParkToolSystem
    {
        private bool CreatePolygonArea(Entity prefab,
            ref TerrainHeightData heightData,
            System.Collections.Generic.IReadOnlyList<float2> polygon = null)
        {
            polygon = polygon ?? _points;
            if (polygon.Count < 3) return false;
            var definition = CreateBuildDefinition();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
            });
            EntityManager.AddComponent<Updated>(definition);
            var nodes = EntityManager.AddBuffer<Game.Areas.Node>(definition);
            nodes.ResizeUninitialized(polygon.Count + 1);
            for (var i = 0; i < polygon.Count; i++)
            {
                var point = polygon[i];
                var height = TerrainUtils.SampleHeight(ref heightData,
                    new float3(point.x, 0f, point.y));
                if (!math.isfinite(height)) return false;
                nodes[i] = new Game.Areas.Node(
                    new float3(point.x, height, point.y), float.MinValue);
            }
            nodes[polygon.Count] = nodes[0];
            return true;
        }

    }
}
