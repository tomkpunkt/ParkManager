using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ParkManager.Tools
{
    /// <summary>
    /// Persistent marker for the logical build record that groups all entities
    /// created for one generated park.
    /// </summary>
    public struct ParkPathBuildMarker : IComponentData, IQueryTypeParameter,
                                        ISerializable
    {
        public int Seed;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
            => writer.Write(Seed);

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
            => reader.Read(out Seed);
    }

    /// <summary>Role of a top-level Vanilla entity in a ParkManager build.</summary>
    public enum ParkPathMemberKind : byte
    {
        Node = 1,
        Edge = 2,
        Surface = 3,
        ParkSurface = 4,
        Tree = 5,
        Bush = 6,
        Bench = 7,
        Lamp = 8,
        Fence = 9,
        TrashBin = 10,
        PlazaCenter = 11,
        NavigationArea = 12,
        AccessMarker = 13,
    }

    /// <summary>
    /// Persistent logical membership without Game.Common.Owner. Keeping the
    /// generated Vanilla entity top-level makes it selectable by Move It and
    /// other network tools while ParkManager can still remove it as a group.
    /// </summary>
    public struct ParkPathMember : IComponentData, IQueryTypeParameter,
                                   ISerializable
    {
        public Entity Park;
        public int ElementId;
        public ParkPathMemberKind Kind;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Park);
            writer.Write(ElementId);
            writer.Write((int)Kind);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Park);
            reader.Read(out ElementId);
            reader.Read(out int kind);
            Kind = (ParkPathMemberKind)kind;
        }
    }

    /// <summary>
    /// Baseline for detecting edits made by external tools. It lives on the
    /// logical record rather than turning Vanilla elements into Owner children.
    /// </summary>
    public struct ParkEditableBuildState : IComponentData, IQueryTypeParameter,
                                           ISerializable
    {
        public const int CurrentVersion = 1;
        public int Version;
        public int MemberCount;
        public long GeometryHash;
        public bool Modified;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CurrentVersion);
            writer.Write(MemberCount);
            writer.Write(GeometryHash);
            writer.Write(Modified);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Version);
            reader.Read(out MemberCount);
            reader.Read(out GeometryHash);
            reader.Read(out Modified);
        }
    }

    /// <summary>
    /// Persistent marker for a park whose creation workflow is complete. A
    /// completed park is treated as a closed city bundle and is no longer
    /// restored into the editor automatically.
    /// </summary>
    public struct ParkCompletedBundle : IComponentData, IQueryTypeParameter,
                                        ISerializable
    {
        public const int CurrentVersion = 1;
        public int Version;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
            => writer.Write(CurrentVersion);

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
            => reader.Read(out Version);
    }

    /// <summary>
    /// Transient hand-off between the early network cleanup and the ordinary
    /// object/area cleanup after the user bulldozes the park surface anchor.
    /// </summary>
    public struct ParkBundleDeletionRequest : IComponentData,
                                              IQueryTypeParameter
    {
        public int EmptyPasses;
    }
}
