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
    /// The routing graph is built as an invisible pedestrian network; the
    /// surface and individually editable objects remain visible.
    /// </summary>
    public sealed partial class ParkToolSystem
    {
        private PlazaLayoutMode _plazaLayout = PlazaLayoutMode.Axial;
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
            var radius = _plazaPlan.HasCenterpiece
                ? _plazaPlan.Centerpieces[0].Radius : 0f;
            var next = PlazaPlanner.Generate(_points, gates, radius,
                _plazaLayout, _plazaPlan.Seed, _plazaPlan.HasCenterpiece,
                arrangement);
            if (next.RoutingSegments.Count == 0)
            {
                PublishState("Die Plaza-Geometrie konnte nicht neu berechnet werden.");
                return;
            }
            _plazaPlan = next;
            var decorationSeed = _decorationPlan?.Seed
                ?? (unchecked(next.Seed * 1103515245 + 12345) & int.MaxValue);
            GenerateDecorationPlan(decorationSeed);
            if (next.Furniture.Count == 0)
                PublishState("Das Arrangement passt nicht auf diese Plaza-Fläche.");
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

        internal void SetPlazaLayout(int value)
        {
            if (HasBuiltPaths || PathBuildBusy || DecorationBuildBusy) return;
            var next = value >= (int)PlazaLayoutMode.Axial
                && value <= (int)PlazaLayoutMode.Open
                ? (PlazaLayoutMode)value : PlazaLayoutMode.Axial;
            if (_plazaLayout == next) return;
            _plazaLayout = next;
            _ui?.SetPlazaLayout((int)next);
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _pathPlan != null) GeneratePaths();
        }

        internal void SelectPlazaCenter(string name)
        {
            if (HasBuiltPaths || PathBuildBusy || DecorationBuildBusy) return;
            _plazaNoCenter = name == "__none__";
            if (!_plazaNoCenter)
                _assetCatalog.Select("PlazaCenter\nsingle\n" + (name ?? string.Empty));
            RefreshPlazaCenterChoices();
            if (_selectedSiteKind == ProceduralSiteKind.Plaza
                && _pathPlan != null) GeneratePaths();
        }

        private void GeneratePlazaPlan()
        {
            _plazaPlan = null;
            _pathPlan = null;
            _decorationPlan = null;
            _pathPreview.Clear();
            var useCenter = !_plazaNoCenter
                && _plazaLayout != PlazaLayoutMode.Open;
            var centerPrefab = Unity.Entities.Entity.Null;
            var centerName = "ohne Mittelobjekt";
            if (useCenter && !_assetCatalog.TryGetFittingPlazaCenter(_points,
                out centerPrefab, out centerName))
            {
                PublishState("Kein verfügbares Mittelobjekt passt in die Plaza-Fläche.");
                PublishPlannerState();
                return;
            }
            if (!ResolvePlacementPrefabs())
            {
                PublishState("Das unsichtbare Fußweg-Prefab ist nicht verfügbar.");
                PublishPlannerState();
                return;
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
            var seed = Guid.NewGuid().GetHashCode() & int.MaxValue;
            if (seed == 0) seed = 1;
            var arrangement = ResolvedPlazaArrangement()
                ?? new List<PlazaArrangementItem>();
            var plan = PlazaPlanner.Generate(_points, gates, centerRadius,
                _plazaLayout, seed, useCenter, arrangement);
            if (plan.RoutingSegments.Count == 0)
            {
                PublishState("Das Plaza-Layout passt nicht auf diese Fläche.");
                PublishPlannerState();
                return;
            }
            for (var gateIndex = 0; gateIndex < gates.Count; gateIndex++)
            {
                var connected = false;
                for (var routeIndex = 0; routeIndex < plan.RoutingSegments.Count;
                    routeIndex++)
                {
                    var route = plan.RoutingSegments[routeIndex];
                    if (math.distancesq(gates[gateIndex], route.A) < 0.25f
                        || math.distancesq(gates[gateIndex], route.B) < 0.25f)
                    {
                        connected = true;
                        break;
                    }
                }
                if (connected) continue;
                PublishState("Ein Plaza-Zugang kann nicht mit dem Zentrum verbunden werden.");
                PublishPlannerState();
                return;
            }
            var segments = new List<float2>(plan.RoutingSegments.Count * 2);
            for (var i = 0; i < plan.RoutingSegments.Count; i++)
            {
                segments.Add(plan.RoutingSegments[i].A);
                segments.Add(plan.RoutingSegments[i].B);
            }
            var routing = ParkPathPlan.FromSegments(seed, segments, gates);
            if (routing.Edges.Count == 0)
            {
                PublishState("Für diese Eingänge konnte kein Plaza-Routing erzeugt werden.");
                PublishPlannerState();
                return;
            }
            _plazaPlan = plan;
            _pathPlan = routing;
            GenerateDecorationPlan(unchecked(seed * 1103515245 + 12345)
                & int.MaxValue);
            PublishState($"Plaza-Entwurf: {centerName}, "
                + $"{_plazaLayout}, {plan.Centerpieces.Count} Mittelobjekte, "
                + $"Seed {seed}, {plan.Furniture.Count} Ausstattungselemente.");
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
            // Keep each mirrored arrangement intact when thinning density.
            for (var i = 0; i + 1 < _plazaPlan.Furniture.Count; i += 2)
            {
                var first = _plazaPlan.Furniture[i];
                var second = _plazaPlan.Furniture[i + 1];
                var kind = ToDecorationKind(first.Kind);
                var draw = (int)((unchecked((uint)seed)
                    ^ (uint)first.ArrangementId * 16777619u) % 100u);
                if (first.ArrangementId != _plazaPlan.Furniture[0].ArrangementId
                    && draw >= math.clamp(_furnitureDensity, 0, 200)) continue;
                placements.Add(ToPlacement(first, kind, (uint)i));
                placements.Add(ToPlacement(second, kind, (uint)(i + 1)));
            }
            return new ParkDecorationPlan(seed, false, placements);
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
