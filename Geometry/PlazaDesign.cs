using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Composition style of a plaza. Each style has its own composition
    /// rules (preferred core shapes, axes, framing), not just other values.
    /// </summary>
    internal enum PlazaStyle : byte
    {
        Auto = 0,
        Classic = 1,
        Modern = 2,
        Promenade = 3,
        Sequence = 4,
    }

    /// <summary>Geometric primitive the main plaza was fitted from.</summary>
    internal enum PlazaCoreShape : byte
    {
        Rectangle = 0,
        ChamferedRectangle = 1,
        Ellipse = 2,
        ConvexPolygon = 3,
        BoundaryAligned = 4,
    }

    internal enum PlazaFocalKind : byte
    {
        /// <summary>Main visual centre, the place for a centerpiece.</summary>
        Primary = 0,
        /// <summary>Centre of a secondary space.</summary>
        Secondary = 1,
    }

    internal enum PlazaFurnitureZoneKind : byte
    {
        /// <summary>Seating along the edge of a space, facing into it.</summary>
        EdgeSeating = 0,
        /// <summary>A quiet secondary space as a whole.</summary>
        QuietCorner = 1,
    }

    /// <summary>A composition axis: a line the design is ordered along.</summary>
    internal struct PlazaAxis
    {
        internal float2 Origin;
        internal float2 Direction;
        internal float HalfLength;
        /// <summary>True for the dominant axis of the composition.</summary>
        internal bool Primary;
    }

    internal struct PlazaFocalPoint
    {
        internal float2 Position;
        internal PlazaFocalKind Kind;
        /// <summary>Free radius around the point inside its space.</summary>
        internal float Clearance;
    }

    /// <summary>Functional pedestrian connection, not a visible path.</summary>
    internal sealed class PlazaCorridor
    {
        internal readonly List<float2> Points = new List<float2>();
        internal float Width;
        /// <summary>Index of the entrance it serves, or -1 between spaces.</summary>
        internal int Entrance = -1;
    }

    /// <summary>
    /// Design intent for a row of trees: positions follow from Start, End
    /// and Spacing in a later phase; no game object is chosen here.
    /// </summary>
    internal struct PlazaPlacementLine
    {
        internal float2 Start;
        internal float2 End;
        internal float Spacing;
        internal int Count;
    }

    /// <summary>Design intent for furniture: where and facing which way.</summary>
    internal sealed class PlazaPlacementZone
    {
        internal PlazaFurnitureZoneKind Kind;
        internal readonly List<float2> Polygon = new List<float2>();
        /// <summary>Direction seats should face (into the space).</summary>
        internal float2 Facing;
    }

    /// <summary>Measured qualities of a design, used by tests and later scoring.</summary>
    internal sealed class PlazaDesignMetrics
    {
        internal float PolygonArea;
        internal float MainPlazaArea;
        internal float MainPlazaMinimumWidth;
        internal float SecondaryArea;
        internal float GreenArea;
        internal float CoreScore;
        internal float Compactness;
        internal float Convexity;
        internal float Elongation;
        internal float CellSize;
        internal readonly Dictionary<PlazaStyle, float> StyleSuitability =
            new Dictionary<PlazaStyle, float>();
        internal double Milliseconds;
    }

    /// <summary>
    /// Abstract spatial composition of a plaza, independent of CS2 entities:
    /// a coherent main plaza, secondary spaces, green zones, movement
    /// corridors and the design intent for focal points, tree rows and
    /// seating. Phase 2 turns the intent into placements.
    /// </summary>
    internal sealed class PlazaDesign
    {
        internal PlazaStyle RequestedStyle;
        internal PlazaStyle Style;
        internal int Seed;
        internal PlazaCoreShape CoreShape;
        internal readonly List<float2> MainPlaza = new List<float2>();
        internal readonly List<List<float2>> SecondarySpaces = new List<List<float2>>();
        internal readonly List<List<float2>> GreenZones = new List<List<float2>>();
        internal readonly List<PlazaAxis> CompositionAxes = new List<PlazaAxis>();
        internal readonly List<PlazaCorridor> MovementCorridors = new List<PlazaCorridor>();
        internal readonly List<PlazaFocalPoint> FocalPoints = new List<PlazaFocalPoint>();
        internal readonly List<PlazaPlacementLine> TreeRows = new List<PlazaPlacementLine>();
        internal readonly List<PlazaPlacementZone> FurnitureZones = new List<PlazaPlacementZone>();
        internal readonly PlazaDesignMetrics Metrics = new PlazaDesignMetrics();
        /// <summary>Why no design could be composed; null on success.</summary>
        internal string Failure;
        internal bool Succeeded => Failure == null && MainPlaza.Count >= 3;
    }
}
