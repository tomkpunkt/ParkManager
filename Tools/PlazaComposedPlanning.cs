using System.Collections.Generic;
using ParkManager.Assets;
using ParkManager.Geometry;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Experimental plaza composer in the game: composes the plaza as an
    /// urban space (<see cref="PlazaComposer"/>) and furnishes it with the
    /// player's trees, benches, lamps and planters
    /// (<see cref="PlazaFurnisher"/>). The result is an ordinary
    /// <see cref="PlazaPlan"/>, so preview, validation and build stay the
    /// same. The classic arrangement planner remains the default.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private int _plazaDesignStyle = -1;
        // Composing is the expensive step; furnishing reruns whenever the
        // assets or the density change, composing only for a new outline,
        // entrances, style or seed.
        private PlazaDesign _composedDesign;
        private ulong _composedDesignKey;

        private PlazaPlan PlanComposedPlaza(bool useCenter, float centerRadius)
        {
            var entrances = EntrancePoints();
            var key = ComposedDesignKey(entrances);
            var composeTime = 0.0;
            if (_composedDesign == null || key != _composedDesignKey)
            {
                _composedDesign = PlazaComposer.Compose(_points, entrances,
                    (PlazaStyle)_plazaDesignStyle, _plazaSeed);
                _composedDesignKey = key;
                composeTime = _composedDesign.Metrics.Milliseconds;
            }
            var design = _composedDesign;
            if (!design.Succeeded)
            {
                Mod.Log.Warn($"ParkManager plaza design {(PlazaStyle)_plazaDesignStyle} failed "
                    + $"({design.Failure}); using the classic arrangement.");
                return null;
            }
            var palette = ComposedPalette(useCenter, centerRadius);
            var furnishing = PlazaFurnisher.Furnish(design, palette, _plazaSeed);
            var counts = new Dictionary<PlazaFurnitureKind, int>();
            foreach (var placement in furnishing.Plan.Furniture)
                counts[placement.Kind] = counts.TryGetValue(placement.Kind, out var count)
                    ? count + 1 : 1;
            var summary = new System.Text.StringBuilder();
            foreach (var pair in counts) summary.Append(' ').Append(pair.Value).Append(' ')
                .Append(pair.Key);
            Mod.Log.Info($"ParkManager plaza design {design.RequestedStyle} seed {_plazaSeed}: "
                + $"{design.Topology}/{design.Grammar}, {design.Rooms.Count} rooms, "
                + $"{design.GreenZones.Count} green zones, {furnishing.Motifs.Count} motifs;"
                + $"{summary}, {furnishing.Plan.Centerpieces.Count} centerpiece; "
                + $"walkable {furnishing.Walkable} (removed {furnishing.RemovedForMovement}); "
                + $"compose {composeTime:F1} ms{(composeTime == 0.0 ? " (cached)" : "")}, "
                + $"furnish {furnishing.Milliseconds:F1} ms.");
            return furnishing.Plan;
        }

        private ulong ComposedDesignKey(List<float2> entrances)
        {
            var hash = 1469598103934665603UL;
            void Mix(uint value) => hash = (hash ^ value) * 1099511628211UL;
            foreach (var point in _points)
            {
                Mix(math.asuint(point.x));
                Mix(math.asuint(point.y));
            }
            Mix(0xffffffffu);
            foreach (var point in entrances)
            {
                Mix(math.asuint(point.x));
                Mix(math.asuint(point.y));
            }
            Mix((uint)_plazaDesignStyle);
            Mix((uint)_plazaSeed);
            return hash;
        }

        /// <summary>
        /// The player's asset choice as a palette: the asset named in the
        /// arrangement for each kind, otherwise the selected one; planters
        /// only when the arrangement contains one. Footprints come from the
        /// prefab; a tree's measured radius is its crown, its trunk and pit
        /// are a fraction of that.
        /// </summary>
        private PlazaPalette ComposedPalette(bool useCenter, float centerRadius)
        {
            var palette = new PlazaPalette
            {
                Centerpiece = useCenter, CenterpieceRadius = useCenter ? centerRadius : 0f,
                Density = math.clamp(_furnitureDensity / 100f, 0.25f, 2f),
            };
            foreach (var kind in new[] { PlazaFurnitureKind.Tree, PlazaFurnitureKind.Bench,
                         PlazaFurnitureKind.Lamp, PlazaFurnitureKind.Bush })
            {
                if (!TryPaletteAsset(kind, out var prefab, out var name)) continue;
                var measured = _assetCatalog.TryGetPlanarRadius(prefab, out var radius)
                    ? radius : 0f;
                PlazaPaletteItem item;
                switch (kind)
                {
                    case PlazaFurnitureKind.Tree:
                        var crown = measured > 0f ? math.clamp(measured, 1.5f, 8f) : 3.25f;
                        // Size 6.5 keeps the asset at its own scale when built.
                        item = new PlazaPaletteItem
                        {
                            AssetName = name, CrownRadius = crown, Size = 6.5f,
                            FootprintRadius = math.clamp(crown * 0.22f, 0.5f, 1.5f),
                        };
                        break;
                    case PlazaFurnitureKind.Bench:
                        item = new PlazaPaletteItem { AssetName = name,
                            FootprintRadius = measured > 0f ? math.clamp(measured, 0.6f, 3f) : 1.35f };
                        break;
                    case PlazaFurnitureKind.Lamp:
                        item = new PlazaPaletteItem { AssetName = name,
                            FootprintRadius = measured > 0f ? math.clamp(measured, 0.3f, 1.5f) : 0.55f };
                        break;
                    default:
                        item = new PlazaPaletteItem { AssetName = name,
                            FootprintRadius = measured > 0f ? math.clamp(measured, 0.5f, 2.5f) : 1.65f };
                        break;
                }
                if (item.Size <= 0f) item.Size = item.FootprintRadius * 2f;
                palette.Items[kind] = item;
            }
            return palette;
        }

        private bool TryPaletteAsset(PlazaFurnitureKind kind, out Unity.Entities.Entity prefab,
            out string name)
        {
            prefab = Unity.Entities.Entity.Null;
            name = string.Empty;
            var category = ArrangementCategory(kind);
            var listed = false;
            foreach (var item in _plazaArrangement)
            {
                if (item.Kind != kind) continue;
                listed = true;
                if (string.IsNullOrEmpty(item.AssetName)) continue;
                if (!_assetCatalog.TryGetNamed(category, item.AssetName, out prefab)) continue;
                name = item.AssetName;
                return true;
            }
            // Planters are an option the player adds to the arrangement.
            if (kind == PlazaFurnitureKind.Bush && !listed) return false;
            return _assetCatalog.TryGetSelected(category, out prefab, out name);
        }
    }
}
