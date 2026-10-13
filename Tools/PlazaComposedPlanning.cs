using System.Collections.Generic;
using ParkManager.Assets;
using ParkManager.Geometry;
using Unity.Entities;
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
        /// <summary>Seed of the furnishing alone: rearranging keeps the design.</summary>
        private int _plazaFurnishingSeed = 1;
        // Composing is the expensive step; furnishing reruns whenever the
        // assets or the density change, composing only for a new outline,
        // entrances, style or seed.
        private PlazaDesign _composedDesign;
        private ulong _composedDesignKey;
        /// <summary>Paving and green of the composed plaza; null for the classic arrangement.</summary>
        private PlazaSurfacePlan _composedSurfaces;
        /// <summary>The surfaces to build, grouped by prefab (paving, squares, green).</summary>
        private readonly List<(Entity prefab, List<List<float2>> polygons)> _composedGroups =
            new List<(Entity, List<List<float2>>)>();
        /// <summary>Pre-build areas of every extra surface prefab, to find the new ones.</summary>
        private readonly Dictionary<Entity, HashSet<Entity>> _composedBaselines =
            new Dictionary<Entity, HashSet<Entity>>();
        /// <summary>Paving of the squares: "" picks a contrasting one, <see cref="SameSquareSurface"/> none.</summary>
        private string _plazaSquareSurface = string.Empty;
        internal const string SameSquareSurface = "__same__";
        /// <summary>Objects of the last composed plan left out on steep ground.</summary>
        private int _composedSlopeRemoved;
        /// <summary>Most height difference across a centerpiece's footprint.</summary>
        private const float CenterpieceMaximumRise = 0.5f;
        /// <summary>Most height difference along a bench, lamp or planter.</summary>
        private const float FurnitureMaximumRise = 0.4f;
        private bool _plazaSurfacesComposed;

        private PlazaPlan PlanComposedPlaza(bool useCenter, float centerRadius)
        {
            var entrances = EntrancePoints();
            var key = ComposedDesignKey(entrances);
            var composeTime = 0.0;
            if (_composedDesign == null || key != _composedDesignKey)
            {
                _composedDesign = PlazaComposer.Compose(_points, entrances,
                    (PlazaStyle)_plazaDesignStyle, _plazaSeed);
                // The exact input, so a game case can be replayed in the batch tests.
                Mod.Log.Info($"ParkManager plaza design input: {DesignInputJson(entrances)}");
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
            _composedSurfaces = PlazaSurfaceComposer.Compose(_points, design);
            var palette = ComposedPalette(useCenter, centerRadius);
            var furnishing = PlazaFurnisher.Furnish(design, palette, _plazaFurnishingSeed);
            var plan = KeepOffSlopes(furnishing.Plan);
            var counts = new Dictionary<PlazaFurnitureKind, int>();
            foreach (var placement in furnishing.Plan.Furniture)
                counts[placement.Kind] = counts.TryGetValue(placement.Kind, out var count)
                    ? count + 1 : 1;
            var summary = new System.Text.StringBuilder();
            foreach (var pair in counts) summary.Append(' ').Append(pair.Value).Append(' ')
                .Append(pair.Key);
            Mod.Log.Info($"ParkManager plaza design {design.RequestedStyle} seed {_plazaSeed}: "
                + $"{design.Topology}/{design.Grammar}, {design.Rooms.Count} rooms, "
                + $"{design.GreenZones.Count} green zones, {furnishing.Motifs.Count} motifs, "
                + $"surfaces {_composedSurfaces.Paving.Count} paving + {_composedSurfaces.Green.Count} green;"
                + $"{summary}, {furnishing.Plan.Centerpieces.Count} centerpiece; "
                + $"walkable {furnishing.Walkable} (removed {furnishing.RemovedForMovement}); "
                + $"compose {composeTime:F1} ms{(composeTime == 0.0 ? " (cached)" : "")}, "
                + $"furnish {furnishing.Milliseconds:F1} ms.");
            return plan;
        }

        /// <summary>
        /// No terraces or steps yet: on a slope, a fountain or a bench would
        /// float on one side and sink on the other, so objects whose footprint
        /// rises too much are left out (trees stand on slopes well). The
        /// player is told how many.
        /// </summary>
        private PlazaPlan KeepOffSlopes(PlazaPlan plan)
        {
            _composedSlopeRemoved = 0;
            var terrain = _terrainSystem.GetHeightData(waitForPending: true);
            float Rise(float2 center, float radius)
            {
                var low = float.MaxValue;
                var high = float.MinValue;
                for (var i = 0; i < 8; i++)
                {
                    var angle = math.PI * 2f * i / 8f;
                    var point = center + new float2(math.cos(angle), math.sin(angle)) * radius;
                    var height = Game.Simulation.TerrainUtils.SampleHeight(ref terrain,
                        new float3(point.x, 0f, point.y));
                    if (!math.isfinite(height)) return float.MaxValue;
                    low = math.min(low, height);
                    high = math.max(high, height);
                }
                return high - low;
            }
            var centerpieces = new List<PlazaCenterpiecePlacement>();
            foreach (var centerpiece in plan.Centerpieces)
                if (Rise(centerpiece.Position, centerpiece.Radius) <= CenterpieceMaximumRise)
                    centerpieces.Add(centerpiece);
                else _composedSlopeRemoved++;
            var furniture = new List<PlazaFurniturePlacement>();
            foreach (var item in plan.Furniture)
                if (item.Kind == PlazaFurnitureKind.Tree
                    || Rise(item.Position, math.max(0.5f, item.FootprintRadius)) <= FurnitureMaximumRise)
                    furniture.Add(item);
                else _composedSlopeRemoved++;
            if (_composedSlopeRemoved == 0) return plan;
            Mod.Log.Info($"ParkManager plaza design left out {_composedSlopeRemoved} objects on steep ground.");
            return new PlazaPlan(plan.Seed, plan.CenterPlacement, plan.ArrangementPlacement,
                plan.CenterpieceSpacing, plan.ArrangementSpacing, centerpieces, furniture);
        }

        /// <summary>
        /// Plans the plaza's ground for the build: the surrounding paving in
        /// the plaza surface, the squares in their own paving (when it
        /// differs) and the green in grass (when it differs). Surfaces of
        /// the same prefab are one group. Without a composed design the plot
        /// stays one surface as before.
        /// </summary>
        private bool PrepareComposedSurfaces()
        {
            _composedGroups.Clear();
            _composedBaselines.Clear();
            _plazaSurfacesComposed = false;
            if (_composedSurfaces == null || _composedDesign == null) return false;
            var square = ResolveSquareSurface(out var squareName);
            if (square == _parkSurfacePrefab) square = Entity.Null;
            if (!_assetCatalog.TryGetGreenSurface(out var green, out var greenName)
                || green == _parkSurfacePrefab) green = Entity.Null;
            if (square == Entity.Null && green == Entity.Null) return false;
            var plan = PlazaSurfaceComposer.Compose(_points, _composedDesign, square != Entity.Null);
            void Add(Entity prefab, List<List<float2>> polygons)
            {
                if (polygons.Count == 0) return;
                for (var i = 0; i < _composedGroups.Count; i++)
                    if (_composedGroups[i].prefab == prefab)
                    {
                        _composedGroups[i].polygons.AddRange(polygons);
                        return;
                    }
                _composedGroups.Add((prefab, new List<List<float2>>(polygons)));
                if (prefab != _parkSurfacePrefab) _composedBaselines[prefab] = new HashSet<Entity>();
            }
            Add(_parkSurfacePrefab, plan.Paving);
            Add(square != Entity.Null ? square : _parkSurfacePrefab, plan.Square);
            // Without a separate grass the beds keep the plaza's own ground.
            Add(green != Entity.Null ? green : _parkSurfacePrefab, plan.Green);
            _plazaSurfacesComposed = true;
            Mod.Log.Info($"ParkManager plaza surfaces: {plan.Paving.Count} paving, "
                + $"{plan.Square.Count} square ('{squareName}'), {plan.Green.Count} green "
                + $"('{(green != Entity.Null ? greenName : "plaza surface")}').");
            return true;
        }

        /// <summary>The squares' paving as chosen; empty for "same as the plaza".</summary>
        private Entity ResolveSquareSurface(out string name)
        {
            name = string.Empty;
            if (_plazaSquareSurface == SameSquareSurface) return Entity.Null;
            if (!string.IsNullOrEmpty(_plazaSquareSurface)
                && _assetCatalog.TryGetNamed(Assets.ParkAssetCategory.Surface,
                    _plazaSquareSurface, out var chosen))
            {
                name = _plazaSquareSurface;
                return chosen;
            }
            _assetCatalog.TryGetSelected(Assets.ParkAssetCategory.Surface, out _, out var plaza);
            return _assetCatalog.TryGetContrastingPaving(plaza, out var prefab, out name)
                ? prefab : Entity.Null;
        }

        /// <summary>The player's choice for the squares' paving, published to the UI.</summary>
        internal void SetPlazaSquareSurface(string name)
        {
            if (!CanChangePlazaSettings()) return;
            _plazaSquareSurface = name ?? string.Empty;
            PublishPlazaSquareSurface();
        }

        private void PublishPlazaSquareSurface()
        {
            if (_plazaSquareSurface == SameSquareSurface)
            {
                _ui?.SetPlazaSquareSurface(SameSquareSurface);
                return;
            }
            ResolveSquareSurface(out var name);
            _ui?.SetPlazaSquareSurface(string.IsNullOrEmpty(name) ? SameSquareSurface : name);
        }

        private void CaptureComposedBaselines()
        {
            foreach (var pair in _composedBaselines)
            {
                pair.Value.Clear();
                CapturePrefabBaseline(_permanentAreaQuery, pair.Key, pair.Value);
            }
        }

        private int CountComposedTemps()
        {
            var count = 0;
            foreach (var group in _composedGroups)
                count += CountOwnTempEntities(_tempAreaQuery, group.prefab);
            return count;
        }

        private int TagComposedTemps(ref int nextId)
        {
            var count = 0;
            foreach (var group in _composedGroups)
                count += TagPlazaTempEntities(_tempAreaQuery, group.prefab,
                    ParkPathMemberKind.ParkSurface, ref nextId);
            return count;
        }

        /// <summary>Creates every planned surface; false when one cannot be made.</summary>
        private bool CreateComposedSurfaces(ref Game.Simulation.TerrainHeightData terrain)
        {
            _expectedParkSurfaceAreas = 0;
            foreach (var group in _composedGroups)
                foreach (var polygon in group.polygons)
                {
                    if (!CreatePolygonArea(group.prefab, ref terrain, polygon)) return false;
                    _expectedParkSurfaceAreas++;
                }
            return true;
        }

        private string DesignInputJson(List<float2> entrances)
        {
            var json = new System.Text.StringBuilder("{\"style\":")
                .Append(_plazaDesignStyle).Append(",\"seed\":").Append(_plazaSeed)
                .Append(",\"polygon\":[");
            void Append(List<float2> points)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append('[').Append(points[i].x.ToString("F2",
                            System.Globalization.CultureInfo.InvariantCulture))
                        .Append(',').Append(points[i].y.ToString("F2",
                            System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                }
            }
            Append(_points);
            json.Append("],\"entrances\":[");
            Append(entrances);
            return json.Append("]}").ToString();
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
                var chosen = PaletteAssets(kind);
                if (chosen.Count == 0) continue;
                var (prefab, name) = chosen[0];
                var names = new List<string>();
                foreach (var asset in chosen) names.Add(asset.name);
                palette.Alternatives[kind] = names;
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

        /// <summary>
        /// The assets the player chose for a kind: every one named in the
        /// plaza arrangement, otherwise the park chooser's selection
        /// (several when it is a mix). Planters only when the arrangement
        /// contains one.
        /// </summary>
        private List<(Entity prefab, string name)> PaletteAssets(PlazaFurnitureKind kind)
        {
            var result = new List<(Entity, string)>();
            var category = ArrangementCategory(kind);
            var listed = false;
            foreach (var item in _plazaArrangement)
            {
                if (item.Kind != kind) continue;
                listed = true;
                if (string.IsNullOrEmpty(item.AssetName)
                    || result.Exists(asset => asset.Item2 == item.AssetName)
                    || !_assetCatalog.TryGetNamed(category, item.AssetName, out var prefab)) continue;
                result.Add((prefab, item.AssetName));
            }
            if (result.Count > 0) return result;
            if (kind == PlazaFurnitureKind.Bush && !listed) return result;
            return _assetCatalog.GetChosen(category);
        }
    }
}
