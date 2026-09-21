using Colossal.Serialization.Entities;
using Unity.Entities;

namespace ParkManager.Tools
{
    /// <summary>
    /// Selects the generator family that owns a procedural site. Park and
    /// plaza generation may share outline capture, asset discovery and the
    /// placement lifecycle, but they deliberately do not share a settings or
    /// layout schema.
    /// </summary>
    public enum ProceduralSiteKind
    {
        Park = 0,
        Plaza = 1,
    }

    /// <summary>
    /// Persisted discriminator placed on the otherwise headless build record.
    /// Records from builds predating this component are interpreted as parks.
    /// The reserved Plaza value is an architectural seam only; no PlazaBuilder
    /// is exposed or executed until its own definition and planner exist.
    /// </summary>
    public struct ProceduralSiteBuilder : IComponentData, IQueryTypeParameter,
                                          ISerializable
    {
        public const int CurrentVersion = 1;

        public int Version;
        public ProceduralSiteKind Kind;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CurrentVersion);
            writer.Write((int)Kind);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Version);
            reader.Read(out int kind);
            Kind = kind == (int)ProceduralSiteKind.Plaza
                ? ProceduralSiteKind.Plaza
                : ProceduralSiteKind.Park;
        }
    }
}
