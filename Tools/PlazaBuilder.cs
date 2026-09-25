using System;
using System.Collections.Generic;
using System.Text;
using ParkManager.Assets;
using ParkManager.Geometry;
using Unity.Mathematics;

namespace ParkManager.Tools
{
    /// <summary>
    /// Plaza-specific planning adapter. It deliberately reuses only the
    /// materialization and ownership pipeline, not the organic park planner.
    /// The plaza uses explicit, symmetric layout rules. Its whole polygon is
    /// pedestrian-accessible; the surface and editable objects remain visible.
    /// A new variant rolls the user-adjustable settings from a seed; the rules
    /// themselves stay deterministic for the chosen settings.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private int _plazaSeed = 1;
        private PlazaCenterPlacementMode _plazaCenterPlacement =
            PlazaCenterPlacementMode.Centered;
        private PlazaArrangementPlacementMode _plazaArrangementPlacement =
            PlazaArrangementPlacementMode.AroundCenter;
        private float _plazaCenterpieceSpacing = 20f;
        private float _plazaArrangementSpacing = 4f;
        private bool _plazaFenceEnabled;
        private bool _plazaNoCenter;
        private PlazaPlan _plazaPlan;
        private ulong _plazaCenterGeometryHash;
        private string _plazaCenterOptionsCache;
        private readonly List<PlazaArrangementItem> _plazaArrangement =
            new List<PlazaArrangementItem>
            {
                new PlazaArrangementItem { Kind = PlazaFurnitureKind.Lamp },
                new PlazaArrangementItem { Kind = PlazaFurnitureKind.Bench },
                new PlazaArrangementItem { Kind = PlazaFurnitureKind.Lamp },
            };

        internal void EditPlazaArrangement(string command)
        {
            if (PathBuildBusy || DecorationBuildBusy || HasBuiltDecorations
                || string.IsNullOrEmpty(command)) return;
            var parts = command.Split(new[] { '\n' }, 3);
            var action = parts[0];
            if (action == "add" && _plazaArrangement.Count < 5)
                _plazaArrangement.Add(new PlazaArrangementItem
                    { Kind = PlazaFurnitureKind.Bench });
            else if (parts.Length >= 2 && int.TryParse(parts[1], out var index)
                && index >= 0 && index < _plazaArrangement.Count)
            {
                if (action == "remove" && _plazaArrangement.Count > 1)
                    _plazaArrangement.RemoveAt(index);
                else if (action == "move" && parts.Length == 3
                    && int.TryParse(parts[2], out var target)
                    && target >= 0 && target < _plazaArrangement.Count)
                {
                    var item = _plazaArrangement[index];
                    _plazaArrangement.RemoveAt(index);
                    _plazaArrangement.Insert(target, item);
                }
                else if (action == "kind" && parts.Length == 3
                    && int.TryParse(parts[2], out var kind)
                    && kind >= (int)PlazaFurnitureKind.Bench
                    && kind <= (int)PlazaFurnitureKind.Bush)
                    _plazaArrangement[index] = new PlazaArrangementItem
                        { Kind = (PlazaFurnitureKind)kind };
                else if (action == "asset" && parts.Length == 3)
                {
                    var item = _plazaArrangement[index];
                    if (string.IsNullOrEmpty(parts[2])
                        || _assetCatalog.TryGetNamed(
                            ArrangementCategory(item.Kind), parts[2], out _))
                    {
                        item.AssetName = parts[2];
                        _plazaArrangement[index] = item;
                    }
                }
            }
            PublishPlazaArrangement();
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _plazaPlan != null && !HasBuiltDecorations)
                ReplanPlazaArrangement();
        }

        private void PublishPlazaArrangement()
        {
            if (_ui == null) return;
            var json = new StringBuilder("[");
            for (var i = 0; i < _plazaArrangement.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"kind\":").Append((int)_plazaArrangement[i].Kind)
                    .Append(",\"name\":\"");
                var name = _plazaArrangement[i].AssetName ?? string.Empty;
                foreach (var character in name)
                {
                    if (character == '\\' || character == '"') json.Append('\\');
                    if (character >= ' ') json.Append(character);
                }
                json.Append("\"}");
            }
            _ui.SetPlazaArrangement(json.Append(']').ToString());
        }

        private static ParkAssetCategory ArrangementCategory(PlazaFurnitureKind kind)
        {
            switch (kind)
            {
                case PlazaFurnitureKind.Lamp: return ParkAssetCategory.Lamp;
                case PlazaFurnitureKind.TrashBin: return ParkAssetCategory.TrashBin;
                case PlazaFurnitureKind.Tree: return ParkAssetCategory.Tree;
                case PlazaFurnitureKind.Bush: return ParkAssetCategory.PlazaPlanter;
                default: return ParkAssetCategory.Bench;
            }
        }

        private List<PlazaArrangementItem> ResolvedPlazaArrangement()
        {
            var result = new List<PlazaArrangementItem>(_plazaArrangement.Count);
            for (var i = 0; i < _plazaArrangement.Count; i++)
            {
                var item = _plazaArrangement[i];
                var category = ArrangementCategory(item.Kind);
                Unity.Entities.Entity prefab;
                var name = string.Empty;
                if (!string.IsNullOrEmpty(item.AssetName))
                {
                    if (!_assetCatalog.TryGetNamed(category, item.AssetName,
                        out prefab)) return null;
                }
                else if (!_assetCatalog.TryGetSelected(category, out prefab,
                    out name)) return null;
                if (string.IsNullOrEmpty(item.AssetName)) item.AssetName = name;
                var fallback = item.Kind == PlazaFurnitureKind.Tree ? 3.25f
                    : item.Kind == PlazaFurnitureKind.Bush ? 1.65f
                    : item.Kind == PlazaFurnitureKind.Bench ? 1.35f
                    : item.Kind == PlazaFurnitureKind.Lamp ? 0.55f : 0.5f;
                item.FootprintRadius = _assetCatalog.TryGetPlanarRadius(prefab,
                    out var radius) ? math.clamp(radius, 0.35f, 5f) : fallback;
                item.Size = item.FootprintRadius * 2f;
                result.Add(item);
            }
            return result;
        }

        private void ReplanPlazaArrangement()
        {
            var arrangement = ResolvedPlazaArrangement()
                ?? new List<PlazaArrangementItem>();
            var gates = new List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++) gates.Add(_entrances[i].xz);
            var centerPrefab = Unity.Entities.Entity.Null;
            var centerName = "ohne Mittelobjekt";
            var useCenter = !_plazaNoCenter
                && _assetCatalog.TryGetFittingPlazaCenter(_points,
                    out centerPrefab, out centerName);
            var radius = useCenter
                && _assetCatalog.TryGetPlanarRadius(centerPrefab,
                    out var measuredRadius)
                ? math.max(measuredRadius, 2f) : 0f;
            var next = PlazaPlanner.Generate(_points, gates, radius,
                _plazaCenterPlacement, _plazaArrangementPlacement,
                _plazaCenterpieceSpacing, _plazaArrangementSpacing,
                _furnitureDensity, _plazaSeed, useCenter, arrangement);
            _plazaPlan = next;
            GenerateDecorationPlan(_plazaSeed);
            PublishPlazaPlacementSettings();
            if (next.Furniture.Count == 0)
                PublishState("Das Arrangement passt nicht auf diese Plaza-Fläche.");
            else if (!useCenter && !_plazaNoCenter)
                PublishState("Das Mittelobjekt passt nicht; Anordnung um das Flächenzentrum geplant.");
            else
                PublishState($"Plaza-Regeln angewendet: {next.Centerpieces.Count} Mittelobjekte, "
                    + $"{next.Furniture.Count} Ausstattungselemente.");
        }

        private void PublishPlazaPlacementSettings()
        {
            _ui?.SetPlazaPlacementSettings((int)_plazaCenterPlacement,
                (int)_plazaArrangementPlacement,
                (int)math.round(_plazaCenterpieceSpacing),
                (int)math.round(_plazaArrangementSpacing), _plazaFenceEnabled);
        }

        private bool CanChangePlazaSettings()
            => !HasBuiltPaths && !PathBuildBusy && !DecorationBuildBusy
                && !HasBuiltDecorations;

        private void ApplyPlazaSettings()
        {
            PublishPlazaPlacementSettings();
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _plazaPlan != null && _pathPlan != null)
                ReplanPlazaArrangement();
        }

        internal void SetPlazaCenterPlacement(int value)
        {
            if (!CanChangePlazaSettings()) return;
            var next = value >= (int)PlazaCenterPlacementMode.Centered
                && value <= (int)PlazaCenterPlacementMode.MainAxis
                ? (PlazaCenterPlacementMode)value
                : PlazaCenterPlacementMode.Centered;
            if (_plazaCenterPlacement == next) return;
            _plazaCenterPlacement = next;
            ApplyPlazaSettings();
        }

        internal void SetPlazaArrangementPlacement(int value)
        {
            if (!CanChangePlazaSettings()) return;
            var next = value == (int)PlazaArrangementPlacementMode.AlongBoundary
                ? PlazaArrangementPlacementMode.AlongBoundary
                : PlazaArrangementPlacementMode.AroundCenter;
            if (_plazaArrangementPlacement == next) return;
            _plazaArrangementPlacement = next;
            ApplyPlazaSettings();
        }

        internal void SetPlazaCenterpieceSpacing(int value)
        {
            if (!CanChangePlazaSettings()) return;
            var next = math.clamp(value, 5, 60);
            if (math.abs(_plazaCenterpieceSpacing - next) < 0.01f) return;
            _plazaCenterpieceSpacing = next;
            ApplyPlazaSettings();
        }

        internal void SetPlazaArrangementSpacing(int value)
        {
            if (!CanChangePlazaSettings()) return;
            var next = math.clamp(value, 0, 20);
            if (math.abs(_plazaArrangementSpacing - next) < 0.01f) return;
            _plazaArrangementSpacing = next;
            ApplyPlazaSettings();
        }

        internal void SetPlazaFenceEnabled(bool enabled)
        {
            if (!CanChangePlazaSettings() || _plazaFenceEnabled == enabled) return;
            _plazaFenceEnabled = enabled;
            ApplyPlazaSettings();
        }

        internal void RefreshPlazaCenterChoices(bool force = false)
        {
            if (_ui == null || _assetCatalog == null) return;
            ulong geometryHash = _closed ? 1469598103934665603UL : 0UL;
            for (var i = 0; i < _points.Count; i++)
            {
                geometryHash = (geometryHash ^ math.asuint(_points[i].x))
                    * 1099511628211UL;
                geometryHash = (geometryHash ^ math.asuint(_points[i].y))
                    * 1099511628211UL;
            }
            var polygon = _closed && IsValidPolygon() ? _points : null;
            if (force || _plazaCenterOptionsCache == null
                || geometryHash != _plazaCenterGeometryHash)
            {
                _plazaCenterGeometryHash = geometryHash;
                _plazaCenterOptionsCache = _assetCatalog
                    .GetPlazaCenterOptionsJson(polygon);
            }
            _ui.SetPlazaCenterOptions(
                _plazaCenterOptionsCache,
                _plazaNoCenter ? "__none__"
                    : _assetCatalog.GetSelectedPlazaCenterName(polygon));
            PublishPlazaArrangement();
        }

        internal void SelectPlazaCenter(string name)
        {
            if (!CanChangePlazaSettings()) return;
            _plazaNoCenter = name == "__none__";
            if (!_plazaNoCenter)
                _assetCatalog.Select("PlazaCenter\nsingle\n" + (name ?? string.Empty));
            RefreshPlazaCenterChoices();
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _plazaPlan != null) ReplanPlazaArrangement();
        }

        /// <summary>
        /// Rolls every plaza setting of the surface step plus the furnishing,
        /// then publishes the values so the controls show the variant.
        /// </summary>
        private void RollPlazaVariant(int seed)
        {
            _plazaSeed = seed;
            var layout = PlazaVariantRoller.RollLayout(seed,
                _assetCatalog.GetUiChoiceNames(ParkAssetCategory.PlazaCenter,
                    _points),
                _assetCatalog.GetUiChoiceNames(ParkAssetCategory.Surface),
                _assetCatalog.GetUiChoiceNames(ParkAssetCategory.Fence));
            _plazaNoCenter = string.IsNullOrEmpty(layout.CenterAsset);
            _plazaCenterPlacement = layout.CenterPlacement;
            _plazaCenterpieceSpacing = layout.CenterpieceSpacing;
            _plazaArrangementPlacement = layout.ArrangementPlacement;
            _plazaArrangementSpacing = layout.ArrangementSpacing;
            _plazaFenceEnabled = !string.IsNullOrEmpty(layout.FenceAsset);
            RollPlazaFurnishing(seed);
            // Publishes the asset selection and, through
            // RefreshPlazaCenterChoices, the centerpiece and arrangement.
            _assetCatalog.SelectPlazaVariantAssets(layout.SurfaceAsset,
                layout.FenceAsset, layout.CenterAsset);
            PublishPlazaPlacementSettings();
        }

        /// <summary>Rolls the arrangement slots and the furnishing density.</summary>
        private void RollPlazaFurnishing(int seed)
        {
            _plazaSeed = seed;
            var assetsByKind = new List<IReadOnlyList<string>>();
            for (var kind = PlazaFurnitureKind.Bench;
                kind <= PlazaFurnitureKind.Bush; kind++)
                assetsByKind.Add(_assetCatalog.GetUiChoiceNames(
                    ArrangementCategory(kind)));
            var furnishing = PlazaVariantRoller.RollFurnishing(seed, assetsByKind);
            _plazaArrangement.Clear();
            _plazaArrangement.AddRange(furnishing.Arrangement);
            _furnitureDensity = furnishing.Density;
            PublishPlazaArrangement();
        }

        private static int NewPlazaVariantSeed()
        {
            var seed = Guid.NewGuid().GetHashCode() & int.MaxValue;
            return seed == 0 ? 1 : seed;
        }

        private void GeneratePlazaPlan()
        {
            _plazaPlan = null;
            _pathPlan = null;
            _decorationPlan = null;
            _pathPreview.Clear();
            var useCenter = !_plazaNoCenter;
            var centerPrefab = Unity.Entities.Entity.Null;
            var centerName = "ohne Mittelobjekt";
            if (useCenter && !_assetCatalog.TryGetFittingPlazaCenter(_points,
                out centerPrefab, out centerName))
            {
                useCenter = false;
                centerName = "ohne Mittelobjekt (zu wenig Platz)";
            }
            var centerRadius = useCenter
                && _assetCatalog.TryGetPlanarRadius(centerPrefab,
                out var measuredRadius)
                ? math.max(measuredRadius, 2f) : 4f;
            if (!useCenter) centerRadius = 0f;
            else Mod.Log.Info($"ParkManager plaza centerpiece {centerName}: "
                + $"measured footprint radius {centerRadius:F2} m.");
            var gates = new List<float2>(_entrances.Count);
            for (var i = 0; i < _entrances.Count; i++)
                gates.Add(_entrances[i].xz);
            var arrangement = ResolvedPlazaArrangement()
                ?? new List<PlazaArrangementItem>();
            var plan = PlazaPlanner.Generate(_points, gates, centerRadius,
                _plazaCenterPlacement, _plazaArrangementPlacement,
                _plazaCenterpieceSpacing, _plazaArrangementSpacing,
                _furnitureDensity, _plazaSeed, useCenter, arrangement);
            _plazaPlan = plan;
            _pathPlan = ParkPathPlan.Empty(_plazaSeed);
            GenerateDecorationPlan(_plazaSeed);
            PublishPlazaPlacementSettings();
            PublishState($"Plaza-Entwurf (Seed {_plazaSeed}): {centerName}, "
                + $"{_plazaCenterPlacement}, {_plazaArrangementPlacement}, "
                + $"{plan.Centerpieces.Count} Mittelobjekte, "
                + $"{plan.Furniture.Count} Ausstattungselemente.");
            PublishPlannerState();
        }

        private ParkDecorationPlan GeneratePlazaDecorations(int seed)
        {
            var placements = new List<ParkDecorationPlacement>();
            if (_plazaPlan == null) return new ParkDecorationPlan(seed, false,
                placements);
            for (var centerIndex = 0; centerIndex < _plazaPlan.Centerpieces.Count;
                centerIndex++)
            {
                var centerpiece = _plazaPlan.Centerpieces[centerIndex];
                placements.Add(new ParkDecorationPlacement
                {
                    Kind = ParkDecorationKind.PlazaCenter,
                    Position = centerpiece.Position,
                    Size = centerpiece.Radius,
                });
            }
            for (var i = 0; i + 1 < _plazaPlan.Furniture.Count; i += 2)
            {
                var first = _plazaPlan.Furniture[i];
                var second = _plazaPlan.Furniture[i + 1];
                var kind = ToDecorationKind(first.Kind);
                placements.Add(ToPlacement(first, kind, (uint)i));
                placements.Add(ToPlacement(second, kind, (uint)(i + 1)));
            }
            if (_plazaFenceEnabled)
            {
                var gates = new List<float2>(_entrances.Count);
                for (var i = 0; i < _entrances.Count; i++)
                    gates.Add(_entrances[i].xz);
                var fencePlan = ParkDecorationPlanner.Generate(_points,
                    ParkPathPlan.Empty(seed), gates, seed, 0f, true, 25, 25,
                    1 << ((int)ParkDecorationKind.Fence - 1));
                for (var i = 0; i < fencePlan.Placements.Count; i++)
                    if (fencePlan.Placements[i].Kind == ParkDecorationKind.Fence)
                        placements.Add(fencePlan.Placements[i]);
            }
            return new ParkDecorationPlan(seed, _plazaFenceEnabled, placements);
        }

        private static ParkDecorationKind ToDecorationKind(PlazaFurnitureKind kind)
        {
            switch (kind)
            {
                case PlazaFurnitureKind.Lamp: return ParkDecorationKind.Lamp;
                case PlazaFurnitureKind.TrashBin: return ParkDecorationKind.TrashBin;
                case PlazaFurnitureKind.Tree: return ParkDecorationKind.Tree;
                case PlazaFurnitureKind.Bush: return ParkDecorationKind.Bush;
                default: return ParkDecorationKind.Bench;
            }
        }

        private static ParkDecorationPlacement ToPlacement(
            PlazaFurniturePlacement source, ParkDecorationKind kind,
            uint variant)
            => new ParkDecorationPlacement
            {
                Kind = kind,
                Position = source.Position,
                Rotation = source.Rotation,
                Size = source.Size,
                Variant = variant,
                AgeStage = kind == ParkDecorationKind.Tree ? (byte)3 : (byte)0,
                ExplicitAssetName = source.AssetName,
            };
    }
}
