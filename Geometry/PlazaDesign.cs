using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// UI preset. Internally a preset fixes either the spatial topology or
    /// the design grammar (or neither for Auto); the other decision follows
    /// from the geometry.
    /// </summary>
    internal enum PlazaStyle : byte
    {
        Auto = 0,
        Classic = 1,
        Modern = 2,
        Promenade = 3,
        Sequence = 4,
        /// <summary>Natural grammar; reachable for review, not yet a UI preset.</summary>
        Natural = 5,
    }

    /// <summary>How the spaces of the plaza are organised.</summary>
    internal enum PlazaTopology : byte
    {
        /// <summary>One dominant square, optionally with entrance courts.</summary>
        Central = 0,
        /// <summary>A linear space with accompanying pockets.</summary>
        Linear = 1,
        /// <summary>Two or more distinct, connected rooms.</summary>
        Sequence = 2,
    }

    /// <summary>The design language applied to the spaces.</summary>
    internal enum PlazaGrammar : byte
    {
        Classic = 0,
        Modern = 1,
        Natural = 2,
    }

    /// <summary>Geometric primitive a room was fitted from.</summary>
    internal enum PlazaCoreShape : byte
    {
        Rectangle = 0,
        ChamferedRectangle = 1,
        Ellipse = 2,
        ConvexPolygon = 3,
        BoundaryAligned = 4,
        Parallelogram = 5,
        Strip = 6,
        /// <summary>Softly curved, slightly asymmetric figure (natural grammar).</summary>
        Organic = 7,
    }

    internal enum PlazaRoomRole : byte
    {
        MainSquare = 0,
        SecondarySquare = 1,
        EntranceCourt = 2,
        Passage = 3,
        GardenCourt = 4,
    }

    /// <summary>Design decision for one edge of a room.</summary>
    internal enum PlazaEdgeRole : byte
    {
        Open = 0,
        TreeRow = 1,
        GreenBorder = 2,
        SeatingEdge = 3,
        ArchitecturalEdge = 4,
    }

    internal enum PlazaGreenKind : byte
    {
        /// <summary>A bed along a room edge, between room and plot boundary.</summary>
        Border = 0,
        /// <summary>Formal lawn panel inside a large square.</summary>
        Parterre = 1,
        /// <summary>Long planting island structuring a large square.</summary>
        Island = 2,
        /// <summary>Organic garden area.</summary>
        Garden = 3,
    }

    internal enum PlazaFocalKind : byte
    {
        /// <summary>Main visual centre, the place for a centerpiece.</summary>
        Primary = 0,
        /// <summary>Centre of a secondary room.</summary>
        Secondary = 1,
    }

    internal enum PlazaFurnitureZoneKind : byte
    {
        /// <summary>Seating along the edge of a room, facing into it.</summary>
        EdgeSeating = 0,
        /// <summary>A quiet room as a whole.</summary>
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
        /// <summary>Free radius around the point inside its room.</summary>
        internal float Clearance;
    }

    /// <summary>Functional pedestrian connection, not a visible path.</summary>
    internal sealed class PlazaCorridor
    {
        internal readonly List<float2> Points = new List<float2>();
        internal float Width;
        /// <summary>Index of the entrance it serves, or -1 between rooms.</summary>
        internal int Entrance = -1;
        /// <summary>Room the corridor leads into.</summary>
        internal int Room = -1;
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
        /// <summary>Direction seats should face (into the room).</summary>
        internal float2 Facing;
    }

    /// <summary>One edge of a room with its own design decision.</summary>
    internal sealed class PlazaRoomEdge
    {
        internal readonly List<float2> Points = new List<float2>();
        internal PlazaEdgeRole Role;
        /// <summary>Unit direction from the room towards the plot boundary.</summary>
        internal float2 Outward;
        /// <summary>Free depth between this edge and the plot boundary.</summary>
        internal float Rim;
        /// <summary>A corridor or passage arrives at this edge.</summary>
        internal bool Arrival;
    }

    /// <summary>A spatial chamber of the plaza and its place in the hierarchy.</summary>
    internal sealed class PlazaRoom
    {
        internal readonly List<float2> Boundary = new List<float2>();
        internal PlazaRoomRole Role;
        /// <summary>Design weight 0–1; the main square has 1.</summary>
        internal float Importance;
        internal readonly List<int> ConnectedRooms = new List<int>();
        internal readonly List<PlazaRoomEdge> Edges = new List<PlazaRoomEdge>();
        internal float2 Center;
        internal float2 Axis;
        internal PlazaShape Shape;
    }

    /// <summary>A deliberately shaped green area.</summary>
    internal sealed class PlazaGreenZone
    {
        internal readonly List<float2> Polygon = new List<float2>();
        internal PlazaGreenKind Kind;
        /// <summary>Room whose edge or interior it belongs to.</summary>
        internal int Room;
        /// <summary>
        /// The dominant landscape motif of the design; all other zones are
        /// secondary features that relate to it.
        /// </summary>
        internal bool Primary;
    }

    /// <summary>Measured qualities of a design, used by tests and scoring.</summary>
    internal sealed class PlazaDesignMetrics
    {
        internal float PolygonArea;
        internal float MainPlazaArea;
        internal float MainPlazaMinimumWidth;
        internal float RoomArea;
        internal float GreenArea;
        /// <summary>Share of the plot with a green ground surface.</summary>
        internal float GreenSurfaceRatio;
        /// <summary>Share of the plot covered by tree crowns seen from above.</summary>
        internal float CanopyCoverageRatio;
        internal float CoreScore;
        internal float Compactness;
        internal float Convexity;
        internal float Elongation;
        internal float CellSize;
        internal readonly Dictionary<PlazaTopology, float> TopologySuitability =
            new Dictionary<PlazaTopology, float>();
        internal readonly Dictionary<PlazaGrammar, float> GrammarSuitability =
            new Dictionary<PlazaGrammar, float>();
        // Soft quality scores, 0–1.
        internal float SpatialHierarchy;
        internal float GreenComposition;
        internal float EdgeDiversity;
        internal float StyleIdentity;
        internal float EntranceIntegration;
        internal double Milliseconds;
    }

    /// <summary>
    /// Abstract spatial composition of a plaza, independent of CS2 entities:
    /// a room graph with roles and edge decisions, designed green zones,
    /// movement corridors and the design intent for focal points, tree rows,
    /// groves and seating. Phase 2 turns the intent into placements.
    /// </summary>
    internal sealed class PlazaDesign
    {
        internal PlazaStyle RequestedStyle;
        internal PlazaTopology Topology;
        internal PlazaGrammar Grammar;
        internal int Seed;
        internal PlazaCoreShape CoreShape;
        internal readonly List<PlazaRoom> Rooms = new List<PlazaRoom>();
        internal readonly List<PlazaGreenZone> GreenZones = new List<PlazaGreenZone>();
        internal readonly List<PlazaAxis> CompositionAxes = new List<PlazaAxis>();
        internal readonly List<PlazaCorridor> MovementCorridors = new List<PlazaCorridor>();
        internal readonly List<PlazaFocalPoint> FocalPoints = new List<PlazaFocalPoint>();
        internal readonly List<PlazaPlacementLine> TreeRows = new List<PlazaPlacementLine>();
        /// <summary>Areas for informal groups of trees inside green zones.</summary>
        internal readonly List<List<float2>> TreeGroups = new List<List<float2>>();
        internal readonly List<PlazaPlacementZone> FurnitureZones = new List<PlazaPlacementZone>();
        internal readonly PlazaDesignMetrics Metrics = new PlazaDesignMetrics();
        /// <summary>Why no design could be composed; null on success.</summary>
        internal string Failure;

        /// <summary>Outline of the main square (room 0).</summary>
        internal List<float2> MainPlaza => Rooms.Count > 0 ? Rooms[0].Boundary
            : new List<float2>();
        internal bool Succeeded => Failure == null && Rooms.Count > 0
            && Rooms[0].Boundary.Count >= 3;
    }
}
