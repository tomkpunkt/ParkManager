using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>Geometric arrangement used for a generated plaza.</summary>
    internal enum PlazaLayoutMode : byte
    {
        Axial = 0,
        Radial = 1,
        Boundary = 2,
        Open = 3,
    }

    /// <summary>Marker kind for the plaza's reserved central feature.</summary>
    internal enum PlazaCenterpieceKind : byte
    {
        Fountain = 0,
        Statue = 1,
    }

    /// <summary>Furniture category in a plaza placement instruction.</summary>
    internal enum PlazaFurnitureKind : byte
    {
        Bench = 0,
        Lamp = 1,
        TrashBin = 2,
        Tree = 3,
        Bush = 4,
    }

    /// <summary>
    /// A straight segment in the plaza's hidden pedestrian routing network.
    /// These are routing data only and should not be rendered as paths.
    /// </summary>
    internal struct PlazaRoutingSegment
    {
        internal float2 A;
        internal float2 B;
    }

    /// <summary>Reserved central fountain/statue position and footprint.</summary>
    internal struct PlazaCenterpiecePlacement
    {
        internal PlazaCenterpieceKind Kind;
        internal float2 Position;
        internal float Radius;
    }

    /// <summary>
    /// One deterministic furniture placement. Rotation is in radians and Size
    /// is the requested approximate footprint dimension in planning units.
    /// </summary>
    internal struct PlazaFurniturePlacement
    {
        internal PlazaFurnitureKind Kind;
        internal string AssetName;
        internal int ArrangementId;
        internal float FootprintRadius;
        internal float2 Position;
        internal float Rotation;
        internal float Size;
    }

    /// <summary>One ordered, user-selected slot in a plaza arrangement.</summary>
    internal struct PlazaArrangementItem
    {
        internal PlazaFurnitureKind Kind;
        internal string AssetName;
        internal float FootprintRadius;
        internal float Size;
    }

    /// <summary>
    /// Shared result for plaza preview and construction. Routing remains
    /// invisible; only the centerpiece and furniture placements are visible.
    /// </summary>
    internal sealed class PlazaPlan
    {
        internal int Seed { get; }
        internal PlazaLayoutMode LayoutMode { get; }
        internal bool HasCenterpiece => Centerpieces.Count > 0;
        internal IReadOnlyList<PlazaCenterpiecePlacement> Centerpieces { get; }
        internal IReadOnlyList<PlazaFurniturePlacement> Furniture { get; }
        internal IReadOnlyList<PlazaRoutingSegment> RoutingSegments { get; }

        internal PlazaPlan(int seed, PlazaLayoutMode layoutMode,
            List<PlazaCenterpiecePlacement> centerpieces,
            List<PlazaFurniturePlacement> furniture,
            List<PlazaRoutingSegment> routingSegments)
        {
            Seed = seed;
            LayoutMode = layoutMode;
            Centerpieces = centerpieces ?? new List<PlazaCenterpiecePlacement>();
            Furniture = furniture ?? new List<PlazaFurniturePlacement>();
            RoutingSegments = routingSegments ?? new List<PlazaRoutingSegment>();
        }
    }
}
