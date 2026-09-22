using Colossal.Serialization.Entities;
using ParkManager.Geometry;
using Unity.Entities;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    public enum ParkPathType : byte
    {
        Narrow = 0,
        Wide = 1,
    }

    /// <summary>
    /// Separately versioned path-style choice. Keeping it outside the original
    /// receipt preserves the binary layout of parks saved before path selection
    /// was introduced; a missing component means the former wide default.
    /// </summary>
    public struct ParkPathStyle : IComponentData, IQueryTypeParameter,
                                  ISerializable
    {
        public ParkPathType Type;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
            => writer.Write((int)Type);

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out int type);
            Type = type == (int)ParkPathType.Narrow
                ? ParkPathType.Narrow : ParkPathType.Wide;
        }
    }

    /// <summary>
    /// Versioned recipe from which ParkManager can reopen the latest generated
    /// park after a save/load cycle. This is deliberately a separate component:
    /// CS2 serializes component instances consecutively, so extending an older
    /// unrelated component would make its previous binary layout ambiguous.
    /// </summary>
    public struct ParkPlacementReceipt : IComponentData, IQueryTypeParameter,
                                         ISerializable
    {
        public const int CurrentVersion = 1;

        public int Version;
        public int PathSeed;
        public int DecorationSeed;
        public int MemberCount;
        public bool FenceEnabled;
        public bool DecorationsBuilt;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CurrentVersion);
            writer.Write(PathSeed);
            writer.Write(DecorationSeed);
            writer.Write(MemberCount);
            writer.Write(FenceEnabled);
            writer.Write(DecorationsBuilt);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Version);
            reader.Read(out PathSeed);
            reader.Read(out DecorationSeed);
            reader.Read(out MemberCount);
            reader.Read(out FenceEnabled);
            reader.Read(out DecorationsBuilt);
        }
    }

    /// <summary>
    /// One world-space vertex of the closed park outline stored on the logical
    /// build record. World height is retained so reopening does not flatten the
    /// editor preview or depend on a later terrain sample.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ParkBuildPoint : IBufferElementData, ISerializable
    {
        public const int CurrentVersion = 1;

        public int Version;
        public float3 Position;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CurrentVersion);
            writer.Write(Position);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Version);
            reader.Read(out Position);
        }
    }

    /// <summary>
    /// World-space location of a manually chosen park entrance. Coordinates,
    /// rather than entity references, survive save/load and are sufficient to
    /// deterministically reconstruct the generated path graph.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct ParkBuildEntrance : IBufferElementData, ISerializable
    {
        public const int CurrentVersion = 1;

        public int Version;
        public float3 Position;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(CurrentVersion);
            writer.Write(Position);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Version);
            reader.Read(out Position);
        }
    }

    public sealed partial class ParkToolSystem
    {
        private Entity _restoredReceiptRecord = Entity.Null;

        /// <summary>Writes the immutable inputs for a newly started build.</summary>
        private void WriteBuildReceipt(Entity record, int pathSeed)
        {
            var decorationSeed = _decorationPlan?.Seed ?? 0;
            EntityManager.AddComponentData(record, new ParkPlacementReceipt
            {
                Version = ParkPlacementReceipt.CurrentVersion,
                PathSeed = pathSeed,
                DecorationSeed = decorationSeed,
                FenceEnabled = _fenceEnabled,
                DecorationsBuilt = false,
            });
            EntityManager.AddComponentData(record, new ParkPathStyle
            {
                Type = _selectedPathType,
            });

            var points = EntityManager.AddBuffer<ParkBuildPoint>(record);
            for (var i = 0; i < _worldPoints.Count; i++)
                points.Add(new ParkBuildPoint
                {
                    Version = ParkBuildPoint.CurrentVersion,
                    Position = _worldPoints[i],
                });

            var entrances = EntityManager.AddBuffer<ParkBuildEntrance>(record);
            for (var i = 0; i < _entrances.Count; i++)
                entrances.Add(new ParkBuildEntrance
                {
                    Version = ParkBuildEntrance.CurrentVersion,
                    Position = _entrances[i],
                });
        }

        /// <summary>
        /// Updates only mutable build-result facts. Geometry inputs stay in the
        /// buffers so a failed furnishing pass cannot corrupt the saved recipe.
        /// </summary>
        private void UpdateBuildReceipt(Entity record, bool? decorationsBuilt = null)
        {
            if (record == Entity.Null || !EntityManager.Exists(record)
                || !EntityManager.HasComponent<ParkPlacementReceipt>(record)) return;

            var receipt = EntityManager.GetComponentData<ParkPlacementReceipt>(record);
            receipt.Version = ParkPlacementReceipt.CurrentVersion;
            receipt.PathSeed = EntityManager.HasComponent<ParkPathBuildMarker>(record)
                ? EntityManager.GetComponentData<ParkPathBuildMarker>(record).Seed
                : receipt.PathSeed;
            receipt.DecorationSeed = _decorationPlan?.Seed ?? receipt.DecorationSeed;
            receipt.FenceEnabled = _fenceEnabled;
            receipt.MemberCount = CountMembers(record);
            if (decorationsBuilt.HasValue)
                receipt.DecorationsBuilt = decorationsBuilt.Value;
            EntityManager.SetComponentData(record, receipt);
        }

        /// <summary>
        /// Reconstructs editor state and deterministic previews from a saved
        /// receipt. Existing draft geometry is never overwritten.
        /// </summary>
        private void RestoreBuildReceipt(Entity record)
        {
            if (record == Entity.Null || record == _restoredReceiptRecord
                || _points.Count > 0 || !EntityManager.Exists(record)
                || !IsParkBuilderRecord(record)
                || !EntityManager.HasComponent<ParkPlacementReceipt>(record)
                || !EntityManager.HasBuffer<ParkBuildPoint>(record)
                || !EntityManager.HasBuffer<ParkBuildEntrance>(record)) return;

            var receipt = EntityManager.GetComponentData<ParkPlacementReceipt>(record);
            if (receipt.Version != ParkPlacementReceipt.CurrentVersion) return;
            var savedPoints = EntityManager.GetBuffer<ParkBuildPoint>(record);
            var savedEntrances = EntityManager.GetBuffer<ParkBuildEntrance>(record);
            if (receipt.PathSeed == 0 || savedPoints.Length < 3
                || savedEntrances.Length == 0) return;

            for (var i = 0; i < savedPoints.Length; i++)
            {
                if (savedPoints[i].Version != ParkBuildPoint.CurrentVersion
                    || !math.all(math.isfinite(savedPoints[i].Position))) return;
            }
            for (var i = 0; i < savedEntrances.Length; i++)
                if (savedEntrances[i].Version != ParkBuildEntrance.CurrentVersion
                    || !math.all(math.isfinite(savedEntrances[i].Position))) return;

            for (var i = 0; i < savedPoints.Length; i++)
            {
                var world = savedPoints[i].Position;
                _worldPoints.Add(world);
                _points.Add(world.xz);
            }
            for (var i = 0; i < savedEntrances.Length; i++)
                _entrances.Add(savedEntrances[i].Position);

            _closed = true;
            _plannerMode = true;
            _fenceEnabled = receipt.FenceEnabled;
            _selectedPathType = EntityManager.HasComponent<ParkPathStyle>(record)
                ? EntityManager.GetComponentData<ParkPathStyle>(record).Type
                : ParkPathType.Wide;
            _pedestrianPathPrefab = Entity.Null;
            ResolvePlacementPrefabs();
            _ui?.SetPathType((int)_selectedPathType);
            _undo.Clear();
            _pointAxes.Clear();
            SyncSnapAxes();

            var gates = new System.Collections.Generic.List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++) gates.Add(_entrances[i].xz);
            _pathPlan = ParkPathPlanner.Generate(_points, gates, FindInteriorHub(),
                    receipt.PathSeed)
                .Smooth(_points, gates);
            _pathPreview.Clear();
            for (var i = 0; i < _pathPlan.Edges.Count; i++)
            {
                var edge = _pathPlan.Edges[i];
                var a = _pathPlan.Nodes[edge.A].Position;
                var b = _pathPlan.Nodes[edge.B].Position;
                _pathPreview.Add(new float3(a.x, HeightForPreview(a), a.y));
                _pathPreview.Add(new float3(b.x, HeightForPreview(b), b.y));
            }

            _restoredReceiptRecord = record;
            if (receipt.DecorationSeed != 0 && _assetCatalog != null)
                GenerateDecorationPlan(receipt.DecorationSeed);
            PublishState("Gespeicherter Parkentwurf wiederhergestellt.");
            PublishPlannerState();
            Mod.Log.Info($"ParkManager restored build receipt for {record}: "
                + $"{_points.Count} points, {_entrances.Count} entrances, "
                + $"path seed {receipt.PathSeed}, decoration seed {receipt.DecorationSeed}.");
        }

        /// <summary>
        /// Keeps the park restorer away from future PlazaBuilder records.
        /// Missing identity means a legacy ParkManager record and is accepted
        /// for backwards compatibility.
        /// </summary>
        private bool IsParkBuilderRecord(Entity record)
        {
            if (!EntityManager.HasComponent<ProceduralSiteBuilder>(record))
                return true;
            var builder = EntityManager.GetComponentData<ProceduralSiteBuilder>(record);
            return builder.Version == ProceduralSiteBuilder.CurrentVersion
                && builder.Kind == ProceduralSiteKind.Park;
        }
    }
}
