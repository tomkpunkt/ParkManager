using System.Collections.Generic;
using Unity.Mathematics;

namespace ParkManager.Geometry
{
    /// <summary>
    /// Structural plaza settings chosen by one seed. An empty asset name
    /// means "no centerpiece" or "no fence"; an empty surface keeps the
    /// current selection.
    /// </summary>
    internal sealed class PlazaLayoutVariant
    {
        internal string CenterAsset = string.Empty;
        internal PlazaCenterPlacementMode CenterPlacement;
        internal int CenterpieceSpacing;
        internal PlazaArrangementPlacementMode ArrangementPlacement;
        internal int ArrangementSpacing;
        internal string SurfaceAsset = string.Empty;
        internal string FenceAsset = string.Empty;
    }

    /// <summary>Furniture arrangement and density chosen by one seed.</summary>
    internal sealed class PlazaFurnishingVariant
    {
        internal int Density;
        internal readonly List<PlazaArrangementItem> Arrangement =
            new List<PlazaArrangementItem>();
    }

    /// <summary>
    /// Derives the user-adjustable plaza settings from a seed. Every value
    /// stays inside the range of its UI control, so a rolled variant can be
    /// refined manually afterwards. Layout and furnishing use separate random
    /// streams: rerolling only the furnishing leaves the layout unchanged.
    /// </summary>
    internal static class PlazaVariantRoller
    {
        internal const int MaximumArrangementItems = 5;
        private const int ArrangementKindCount = 5;
        private const float NoCenterpieceChance = 0.15f;
        private const float FenceChance = 0.35f;

        /// <param name="centers">Centerpieces that fit the polygon.</param>
        internal static PlazaLayoutVariant RollLayout(int seed,
            IReadOnlyList<string> centers, IReadOnlyList<string> surfaces,
            IReadOnlyList<string> fences)
        {
            var random = new Unity.Mathematics.Random(MixSeed(seed, 0x6a09e667u));
            // Draw every value unconditionally so that an empty asset list
            // does not shift the values of the following settings.
            var withCenter = random.NextFloat() >= NoCenterpieceChance;
            var center = Pick(centers, ref random);
            var result = new PlazaLayoutVariant
            {
                CenterPlacement = (PlazaCenterPlacementMode)random.NextInt(0, 3),
                CenterpieceSpacing = random.NextInt(5, 61),
                ArrangementPlacement =
                    (PlazaArrangementPlacementMode)random.NextInt(0, 2),
                ArrangementSpacing = random.NextInt(0, 21),
                SurfaceAsset = Pick(surfaces, ref random),
            };
            var withFence = random.NextFloat() < FenceChance;
            var fence = Pick(fences, ref random);
            result.CenterAsset = withCenter ? center : string.Empty;
            result.FenceAsset = withFence ? fence : string.Empty;
            return result;
        }

        /// <summary>
        /// Rolls a mirror-symmetric arrangement such as bench–bush–bench,
        /// because the planner places it as one row. All slots of the same
        /// kind share one asset.
        /// </summary>
        /// <param name="assetsByKind">Selectable asset names indexed by
        /// <see cref="PlazaFurnitureKind"/>.</param>
        internal static PlazaFurnishingVariant RollFurnishing(int seed,
            IReadOnlyList<IReadOnlyList<string>> assetsByKind)
        {
            var random = new Unity.Mathematics.Random(MixSeed(seed, 0xbb67ae85u));
            var result = new PlazaFurnishingVariant
            {
                Density = random.NextInt(1, 9) * 25,
            };
            var length = random.NextInt(1, MaximumArrangementItems + 1);

            var available = new List<PlazaFurnitureKind>(ArrangementKindCount);
            var assets = new string[ArrangementKindCount];
            for (var kind = 0; kind < ArrangementKindCount; kind++)
            {
                var names = assetsByKind != null && kind < assetsByKind.Count
                    ? assetsByKind[kind] : null;
                assets[kind] = Pick(names, ref random);
                if (!string.IsNullOrEmpty(assets[kind]))
                    available.Add((PlazaFurnitureKind)kind);
            }

            var half = (length + 1) / 2;
            var kinds = new PlazaFurnitureKind[half];
            for (var i = 0; i < half; i++)
            {
                var index = random.NextInt(0, math.max(1, available.Count));
                kinds[i] = available.Count > 0 ? available[index]
                    : PlazaFurnitureKind.Bench;
            }
            for (var i = 0; i < length; i++)
            {
                var kind = kinds[math.min(i, length - 1 - i)];
                result.Arrangement.Add(new PlazaArrangementItem
                {
                    Kind = kind,
                    AssetName = assets[(int)kind] ?? string.Empty,
                });
            }
            return result;
        }

        private static string Pick(IReadOnlyList<string> names,
            ref Unity.Mathematics.Random random)
        {
            var index = random.NextInt(0, int.MaxValue);
            return names == null || names.Count == 0
                ? string.Empty : names[index % names.Count] ?? string.Empty;
        }

        private static uint MixSeed(int seed, uint salt)
        {
            var value = unchecked((uint)seed) ^ salt;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value == 0 ? 1u : value;
        }
    }
}
