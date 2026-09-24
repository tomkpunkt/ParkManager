using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>Supported output layers of the furnishing planner.</summary>
    public enum ParkDecorationKind : byte
    {
        Tree = 1,
        Bush = 2,
        Bench = 3,
        Lamp = 4,
        Fence = 5,
        TrashBin = 6,
        PlazaCenter = 7,
    }

    /// <summary>
    /// Engine-independent placement instruction produced by the furnishing
    /// planner. Position is expressed in the XZ planning plane; rotation is in
    /// radians and variant selects a deterministic prefab from its category.
    /// </summary>
    public struct ParkDecorationPlacement
    {
        public ParkDecorationKind Kind;
        public float2 Position;
        public float Rotation;
        public float Size;
        public uint Variant;
        public byte AgeStage;
        public string ExplicitAssetName;
    }

    /// <summary>
    /// Deterministic collection of furnishing placement instructions for one
    /// park variant. The plan is shared by overlay preview and ECS placement so
    /// the preview describes the objects that will actually be built.
    /// </summary>
    public sealed class ParkDecorationPlan
    {
        public int Seed { get; }
        public bool FenceEnabled { get; }
        public List<ParkDecorationPlacement> Placements { get; }

        public ParkDecorationPlan(int seed, bool fenceEnabled,
            List<ParkDecorationPlacement> placements)
        {
            Seed = seed;
            FenceEnabled = fenceEnabled;
            Placements = placements ?? new List<ParkDecorationPlacement>();
        }

        public int Count(ParkDecorationKind kind)
        {
            var count = 0;
            for (var i = 0; i < Placements.Count; i++)
                if (Placements[i].Kind == kind) count++;
            return count;
        }
    }
}
