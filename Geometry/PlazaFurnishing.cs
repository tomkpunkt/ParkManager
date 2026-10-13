using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// A design motif: the reason a group of objects exists. Objects are
    /// never scattered on their own; every placement belongs to a motif.
    /// </summary>
    internal enum PlazaMotifKind : byte
    {
        /// <summary>Fountain or statue on the primary focal point.</summary>
        Centerpiece = 0,
        /// <summary>A tree row along a room edge.</summary>
        TreeAvenue = 1,
        /// <summary>An informal group of trees in a green zone.</summary>
        Grove = 2,
        /// <summary>Benches along a seating edge, facing into the room.</summary>
        SeatingEdge = 3,
        /// <summary>Benches around the centerpiece, facing it.</summary>
        SeatingRing = 4,
        /// <summary>A few benches in a quiet room, facing its heart.</summary>
        QuietSeating = 5,
        /// <summary>Lamps in a steady rhythm along the edge of a room.</summary>
        LampRhythm = 6,
        /// <summary>A pair of lamps marking an entrance.</summary>
        EntranceLamps = 7,
        /// <summary>
        /// Two or three benches side by side with one shared orientation,
        /// on the paving where it meets a garden (informal seating).
        /// </summary>
        SeatingGroup = 8,
        /// <summary>Low planting that gives a bed or island its function.</summary>
        BedPlanting = 9,
        /// <summary>
        /// A regular grid of trees on paving that turns a large remainder
        /// between the rooms and the plot edge into a shaded grove.
        /// </summary>
        TreeGrid = 11,
        /// <summary>
        /// Objects that are given, not designed (already standing or pinned);
        /// placed unchecked and the first to go when they block the way.
        /// </summary>
        Preset = 10,
    }

    /// <summary>One motif with the line or ring it is laid out on.</summary>
    internal sealed class PlazaMotif
    {
        internal PlazaMotifKind Kind;
        internal PlazaFurnitureKind Item;
        /// <summary>Room the motif belongs to.</summary>
        internal int Room;
        /// <summary>Lower numbers are placed first and removed last.</summary>
        internal int Priority;
        /// <summary>Layout line for line motifs (avenues, seating, lamps).</summary>
        internal readonly List<float2> Line = new List<float2>();
        internal bool Closed;
        internal float Spacing;
        /// <summary>Free length kept at both ends of an open line.</summary>
        internal float Margin;
        internal int MinimumCount;
        /// <summary>Direction the objects face, when the motif has one.</summary>
        internal float2 Facing;
        /// <summary>Point the objects face, for rings; used when set.</summary>
        internal float2? FacingPoint;
        /// <summary>Motif that must keep the same count (mirrored avenue), or -1.</summary>
        internal int Partner = -1;
        /// <summary>The full line before it was cut to the stretch shared with the partner.</summary>
        internal List<float2> FullLine;
        internal bool Solved;
        internal readonly List<PlazaFurniturePlacement> Placements =
            new List<PlazaFurniturePlacement>();
        internal readonly List<PlazaCenterpiecePlacement> Centerpieces =
            new List<PlazaCenterpiecePlacement>();
    }

    /// <summary>Asset choice for one furniture kind.</summary>
    internal struct PlazaPaletteItem
    {
        internal string AssetName;
        /// <summary>
        /// Collision radius of the object on the ground; for a tree the
        /// trunk with its tree pit, not the crown.
        /// </summary>
        internal float FootprintRadius;
        /// <summary>Approximate visual size (crown diameter for trees).</summary>
        internal float Size;
        /// <summary>Crown radius seen from above; trees only.</summary>
        internal float CrownRadius;
    }

    /// <summary>The assets a furnishing may use; a missing kind is left out.</summary>
    internal sealed class PlazaPalette
    {
        internal readonly Dictionary<PlazaFurnitureKind, PlazaPaletteItem> Items =
            new Dictionary<PlazaFurnitureKind, PlazaPaletteItem>();
        /// <summary>
        /// Further assets of a kind the player chose; laid out with the
        /// footprint of <see cref="Items"/>. Rows keep one asset, groves mix.
        /// </summary>
        internal readonly Dictionary<PlazaFurnitureKind, List<string>> Alternatives =
            new Dictionary<PlazaFurnitureKind, List<string>>();
        internal bool Centerpiece = true;
        internal PlazaCenterpieceKind CenterpieceKind = PlazaCenterpieceKind.Fountain;
        /// <summary>Footprint of the chosen centerpiece asset; 0 lets the square decide.</summary>
        internal float CenterpieceRadius;
        /// <summary>Furnishing density: 1 is the grammar's rhythm, 2 halves every spacing.</summary>
        internal float Density = 1f;

        /// <summary>Trees, benches and lamps with typical vanilla footprints.</summary>
        internal static PlazaPalette Default()
        {
            var palette = new PlazaPalette();
            palette.Items[PlazaFurnitureKind.Tree] = new PlazaPaletteItem
                { AssetName = "Tree", FootprintRadius = 0.8f, Size = 7f, CrownRadius = 3.5f };
            palette.Items[PlazaFurnitureKind.Bush] = new PlazaPaletteItem
                { AssetName = "Planter", FootprintRadius = 0.9f, Size = 1.8f };
            palette.Items[PlazaFurnitureKind.Bench] = new PlazaPaletteItem
                { AssetName = "Bench", FootprintRadius = 1f, Size = 2f };
            palette.Items[PlazaFurnitureKind.Lamp] = new PlazaPaletteItem
                { AssetName = "Lamp", FootprintRadius = 0.35f, Size = 0.5f };
            return palette;
        }
    }

    /// <summary>
    /// Result of furnishing a plaza design: a build-ready plan plus the
    /// motifs it came from, for review and tests.
    /// </summary>
    internal sealed class PlazaFurnishing
    {
        internal PlazaPlan Plan;
        internal readonly List<PlazaMotif> Motifs = new List<PlazaMotif>();
        /// <summary>Every room is reachable from every entrance on a free path.</summary>
        internal bool Walkable;
        /// <summary>Objects the solver took out to keep the plaza walkable.</summary>
        internal int RemovedForMovement;
        internal double Milliseconds;
    }
}
