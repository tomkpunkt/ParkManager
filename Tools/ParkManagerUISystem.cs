using Colossal.UI.Binding;
using Game.SceneFlow;
using Game.UI;
using UnityEngine.Scripting;
using ParkManager.Assets;

namespace ParkManager.Tools
{
    /// <summary>
    /// Bridge between the Cohtml interface and ParkManager simulation systems.
    /// It owns UI bindings only and forwards user actions to the world tool and
    /// asset catalog; procedural and ECS state remain outside the UI layer.
    /// </summary>
    public sealed partial class ParkManagerUISystem : UISystemBase
    {
        private const string Group = "ParkManager";

        private ValueBinding<bool> _panelOpen;
        private ValueBinding<bool> _toolActive;
        private ValueBinding<int> _pointCount;
        private ValueBinding<bool> _polygonClosed;
        private ValueBinding<bool> _polygonValid;
        private ValueBinding<string> _status;
        private ValueBinding<bool> _assetCatalogReady;
        private ValueBinding<string> _assetCatalogSummary;
        private ValueBinding<string> _assetOptionsJson;
        private ValueBinding<string> _parkPaletteOptionsJson;
        private ValueBinding<string> _selectedParkPalette;
        private ValueBinding<bool> _plannerMode;
        private ValueBinding<int> _entranceCount;
        private ValueBinding<bool> _pathPlanReady;
        private ValueBinding<bool> _pathBuildBusy;
        private ValueBinding<bool> _pathBuildPresent;
        private ValueBinding<string> _pathBuildSummary;
        private ValueBinding<int> _snapMask;
        private ValueBinding<bool> _fenceEnabled;
        private ValueBinding<int> _vegetationDensity;
        private ValueBinding<bool> _decorationPlanReady;
        private ValueBinding<bool> _decorationBuildBusy;
        private ValueBinding<bool> _decorationBuildPresent;
        private ValueBinding<string> _decorationSummary;
        private ValueBinding<int> _parkCount;
        private ValueBinding<string> _locale;
        private string _lastLocale = "en";

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(_panelOpen = new ValueBinding<bool>(
                Group, "PanelOpen", false));
            AddBinding(_toolActive = new ValueBinding<bool>(
                Group, "ToolActive", false));
            AddBinding(_pointCount = new ValueBinding<int>(
                Group, "PointCount", 0));
            AddBinding(_polygonClosed = new ValueBinding<bool>(
                Group, "PolygonClosed", false));
            AddBinding(_polygonValid = new ValueBinding<bool>(
                Group, "PolygonValid", false));
            AddBinding(_status = new ValueBinding<string>(
                Group, "Status", "Werkzeug starten, um ein Polygon zu zeichnen."));
            AddBinding(_assetCatalogReady = new ValueBinding<bool>(
                Group, "AssetCatalogReady", false));
            AddBinding(_assetCatalogSummary = new ValueBinding<string>(
                Group, "AssetCatalogSummary", "Assetkatalog wird aufgebaut …"));
            AddBinding(_assetOptionsJson = new ValueBinding<string>(
                Group, "AssetOptionsJson", "{}"));
            AddBinding(_parkPaletteOptionsJson = new ValueBinding<string>(
                Group, "ParkPaletteOptionsJson", "[]"));
            AddBinding(_selectedParkPalette = new ValueBinding<string>(
                Group, "SelectedParkPalette", string.Empty));
            AddBinding(_plannerMode = new ValueBinding<bool>(
                Group, "PlannerMode", false));
            AddBinding(_entranceCount = new ValueBinding<int>(
                Group, "EntranceCount", 0));
            AddBinding(_pathPlanReady = new ValueBinding<bool>(
                Group, "PathPlanReady", false));
            AddBinding(_pathBuildBusy = new ValueBinding<bool>(
                Group, "PathBuildBusy", false));
            AddBinding(_pathBuildPresent = new ValueBinding<bool>(
                Group, "PathBuildPresent", false));
            AddBinding(_pathBuildSummary = new ValueBinding<string>(
                Group, "PathBuildSummary", "Noch keine Testwege gebaut."));
            AddBinding(_snapMask = new ValueBinding<int>(
                Group, "SnapMask", (int)ParkToolSystem.SupportedSnapKinds));
            AddBinding(_fenceEnabled = new ValueBinding<bool>(
                Group, "FenceEnabled", false));
            AddBinding(_vegetationDensity = new ValueBinding<int>(
                Group, "VegetationDensity", 100));
            AddBinding(_decorationPlanReady = new ValueBinding<bool>(
                Group, "DecorationPlanReady", false));
            AddBinding(_decorationBuildBusy = new ValueBinding<bool>(
                Group, "DecorationBuildBusy", false));
            AddBinding(_decorationBuildPresent = new ValueBinding<bool>(
                Group, "DecorationBuildPresent", false));
            AddBinding(_decorationSummary = new ValueBinding<string>(
                Group, "DecorationSummary", "Noch keine Ausstattung geplant."));
            AddBinding(_parkCount = new ValueBinding<int>(
                Group, "ParkCount", 0));
            _lastLocale = GetSupportedLocale();
            AddBinding(_locale = new ValueBinding<string>(
                Group, "Locale", _lastLocale));
            AddBinding(new TriggerBinding(Group, "TogglePanel",
                () => _panelOpen.Update(!_panelOpen.value)));
            AddBinding(new TriggerBinding<bool>(Group, "SetPanelOpen",
                open => _panelOpen.Update(open)));
            AddBinding(new TriggerBinding(Group, "ToggleTool", ToggleTool));
            AddBinding(new TriggerBinding(Group, "ClearPolygon", ClearPolygon));
            AddBinding(new TriggerBinding(Group, "RefreshAssetCatalog",
                () => World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>()
                    .RequestRefresh()));
            AddBinding(new TriggerBinding(Group, "TogglePlannerMode",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .TogglePlannerMode()));
            AddBinding(new TriggerBinding(Group, "GeneratePaths",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .GeneratePaths()));
            AddBinding(new TriggerBinding(Group, "BuildPaths",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .BuildPaths()));
            AddBinding(new TriggerBinding(Group, "RemoveBuiltPaths",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .RemoveBuiltPaths()));
            AddBinding(new TriggerBinding<string>(Group, "SelectAsset",
                value => World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>()
                    .Select(value)));
            AddBinding(new TriggerBinding<string>(Group, "SelectParkPalette",
                value => World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>()
                    .SelectParkPalette(value)));
            AddBinding(new TriggerBinding(Group, "GenerateDecorations",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .GenerateDecorations()));
            AddBinding(new TriggerBinding(Group, "ToggleFence",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .ToggleFence()));
            AddBinding(new TriggerBinding<int>(Group, "SetVegetationDensity",
                value => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .SetVegetationDensity(value)));
            AddBinding(new TriggerBinding(Group, "BuildDecorations",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .BuildDecorations()));
            AddBinding(new TriggerBinding(Group, "RemoveBuiltDecorations",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .RemoveBuiltDecorations()));
            AddBinding(new TriggerBinding(Group, "FinishPark",
                () => World.GetOrCreateSystemManaged<ParkToolSystem>()
                    .FinishPark()));
        }

        private void ToggleTool()
        {
            var tools = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            var parkTool = World.GetOrCreateSystemManaged<ParkToolSystem>();
            var activating = tools.activeTool != parkTool;
            tools.activeTool = activating
                ? parkTool
                : World.GetOrCreateSystemManaged<Game.Tools.DefaultToolSystem>();
            // Asset Icon Library may populate UIObject icons after our first
            // prefab scan. Refresh on opening the tool, when all UI mods have
            // normally finished initializing, so the compact tiles gain their
            // real thumbnails without a manual reload button.
            if (activating)
                World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>()
                    .RequestRefresh();
        }

        private void ClearPolygon()
        {
            World.GetOrCreateSystemManaged<ParkToolSystem>().ClearPolygon();
        }

        internal void SetToolActive(bool active)
        {
            _toolActive?.Update(active);
            _panelOpen?.Update(active);
        }

        internal void SetPolygonState(int pointCount, bool closed, bool valid,
            string status)
        {
            _pointCount?.Update(pointCount);
            _polygonClosed?.Update(closed);
            _polygonValid?.Update(valid);
            _status?.Update(status);
        }

        internal void SetAssetCatalogState(bool ready, string summary,
            string optionsJson, string parkPaletteOptionsJson,
            string selectedParkPalette)
        {
            _assetCatalogReady?.Update(ready);
            _assetCatalogSummary?.Update(summary);
            _assetOptionsJson?.Update(optionsJson ?? "{}");
            _parkPaletteOptionsJson?.Update(parkPaletteOptionsJson ?? "[]");
            _selectedParkPalette?.Update(selectedParkPalette ?? string.Empty);
        }

        internal void SetPlannerState(bool plannerMode, int entranceCount,
            bool pathPlanReady)
        {
            _plannerMode?.Update(plannerMode);
            _entranceCount?.Update(entranceCount);
            _pathPlanReady?.Update(pathPlanReady);
        }

        internal void SetPathBuildState(bool busy, bool present, string summary)
        {
            _pathBuildBusy?.Update(busy);
            _pathBuildPresent?.Update(present);
            _pathBuildSummary?.Update(summary);
        }

        internal void SetSnapMask(int mask) => _snapMask?.Update(mask);

        internal void SetDecorationState(bool fenceEnabled, int vegetationDensity,
            bool planReady,
            bool busy, bool present, string summary)
        {
            _fenceEnabled?.Update(fenceEnabled);
            _vegetationDensity?.Update(vegetationDensity);
            _decorationPlanReady?.Update(planReady);
            _decorationBuildBusy?.Update(busy);
            _decorationBuildPresent?.Update(present);
            _decorationSummary?.Update(summary);
        }

        internal void SetWorkspaceState(int parkCount)
            => _parkCount?.Update(parkCount);

        protected override void OnUpdate()
        {
            var locale = GetSupportedLocale();
            if (locale != _lastLocale)
            {
                _lastLocale = locale;
                _locale?.Update(locale);
            }
        }

        /// <summary>
        /// Reduces the game's locale to the languages currently shipped by
        /// ParkManager. Unknown languages deliberately fall back to English.
        /// The binding is watched by the React UI, so changing the game
        /// language does not require a second UI-specific setting.
        /// </summary>
        private static string GetSupportedLocale()
        {
            try
            {
                var locale = GameManager.instance?.localizationManager
                    ?.activeLocaleId;
                return locale != null && locale.StartsWith("de") ? "de" : "en";
            }
            catch
            {
                return "en";
            }
        }
    }
}
