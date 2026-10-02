using Colossal.UI.Binding;
using Game.SceneFlow;
using Game.UI;
using UnityEngine.Scripting;
using ParkManager.Assets;

namespace ParkManager.Tools
{
    internal enum PathBuildStatus
    {
        Ok,
        Warning,
        Error,
    }

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
        private ValueBinding<float> _polygonArea;
        private ValueBinding<bool> _polygonClosed;
        private ValueBinding<bool> _polygonValid;
        private ValueBinding<string> _status;
        private ValueBinding<string> _assetOptionsJson;
        private ValueBinding<bool> _plannerMode;
        private ValueBinding<int> _entranceCount;
        private ValueBinding<bool> _pathPlanReady;
        private ValueBinding<bool> _pathBuildBusy;
        private ValueBinding<bool> _pathBuildPresent;
        private ValueBinding<string> _pathBuildSummary;
        private ValueBinding<string> _pathBuildStatus;
        private ValueBinding<int> _pathType;
        private ValueBinding<bool> _lakeEnabled;
        private ValueBinding<int> _siteType;
        private ValueBinding<int> _plazaCenterPlacement;
        private ValueBinding<int> _plazaArrangementPlacement;
        private ValueBinding<int> _plazaCenterpieceSpacing;
        private ValueBinding<int> _plazaArrangementSpacing;
        private ValueBinding<bool> _plazaFenceEnabled;
        private ValueBinding<string> _plazaCenterOptionsJson;
        private ValueBinding<string> _plazaCenterSelected;
        private ValueBinding<string> _plazaArrangementJson;
        private ValueBinding<int> _vegetationDensity;
        private ValueBinding<int> _furnitureDensity;
        private ValueBinding<int> _decorationEnabledMask;
        private ValueBinding<bool> _decorationPlanReady;
        private ValueBinding<bool> _decorationBuildBusy;
        private ValueBinding<bool> _decorationBuildPresent;
        private ValueBinding<string> _decorationSummary;
        private ValueBinding<bool> _removeMode;
        private ValueBinding<int> _removeSelectionCount;
        private ValueBinding<string> _locale;
        private string _lastLocale = "en";

        // Resolved lazily: the tool system itself creates this UI system in
        // its OnCreate, so resolving it here during OnCreate would recurse.
        private ParkToolSystem Tool => World.GetOrCreateSystemManaged<ParkToolSystem>();
        private ParkAssetCatalogSystem Catalog
            => World.GetOrCreateSystemManaged<ParkAssetCatalogSystem>();

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
            AddBinding(_polygonArea = new ValueBinding<float>(
                Group, "PolygonArea", 0f));
            AddBinding(_polygonClosed = new ValueBinding<bool>(
                Group, "PolygonClosed", false));
            AddBinding(_polygonValid = new ValueBinding<bool>(
                Group, "PolygonValid", false));
            AddBinding(_status = new ValueBinding<string>(
                Group, "Status", UiText.Of("status.startTool")));
            AddBinding(_assetOptionsJson = new ValueBinding<string>(
                Group, "AssetOptionsJson", "{}"));
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
                Group, "PathBuildSummary", UiText.Of("path.noneBuilt")));
            AddBinding(_pathBuildStatus = new ValueBinding<string>(
                Group, "PathBuildStatus", "ok"));
            AddBinding(_pathType = new ValueBinding<int>(
                Group, "PathType", (int)ParkPathType.Wide));
            AddBinding(_lakeEnabled = new ValueBinding<bool>(
                Group, "LakeEnabled", true));
            AddBinding(_siteType = new ValueBinding<int>(
                Group, "SiteType", (int)ProceduralSiteKind.Park));
            AddBinding(_plazaCenterPlacement = new ValueBinding<int>(
                Group, "PlazaCenterPlacement", 0));
            AddBinding(_plazaArrangementPlacement = new ValueBinding<int>(
                Group, "PlazaArrangementPlacement", 0));
            AddBinding(_plazaCenterpieceSpacing = new ValueBinding<int>(
                Group, "PlazaCenterpieceSpacing", 20));
            AddBinding(_plazaArrangementSpacing = new ValueBinding<int>(
                Group, "PlazaArrangementSpacing", 4));
            AddBinding(_plazaFenceEnabled = new ValueBinding<bool>(
                Group, "PlazaFenceEnabled", false));
            AddBinding(_plazaCenterOptionsJson = new ValueBinding<string>(
                Group, "PlazaCenterOptionsJson", "[]"));
            AddBinding(_plazaCenterSelected = new ValueBinding<string>(
                Group, "PlazaCenterSelected", string.Empty));
            AddBinding(_plazaArrangementJson = new ValueBinding<string>(
                Group, "PlazaArrangementJson", "[]"));
            // Snap options offered in the header; constant for this tool.
            AddBinding(new ValueBinding<int>(Group, "SnapMask",
                (int)ParkToolSystem.SupportedSnapKinds));
            AddBinding(_vegetationDensity = new ValueBinding<int>(
                Group, "VegetationDensity", 100));
            AddBinding(_furnitureDensity = new ValueBinding<int>(
                Group, "FurnitureDensity", 100));
            AddBinding(_decorationEnabledMask = new ValueBinding<int>(
                Group, "DecorationEnabledMask", 0x2f));
            AddBinding(_decorationPlanReady = new ValueBinding<bool>(
                Group, "DecorationPlanReady", false));
            AddBinding(_decorationBuildBusy = new ValueBinding<bool>(
                Group, "DecorationBuildBusy", false));
            AddBinding(_decorationBuildPresent = new ValueBinding<bool>(
                Group, "DecorationBuildPresent", false));
            AddBinding(_decorationSummary = new ValueBinding<string>(
                Group, "DecorationSummary", UiText.Of("decoration.none")));
            _lastLocale = GetSupportedLocale();
            AddBinding(_locale = new ValueBinding<string>(
                Group, "Locale", _lastLocale));

            AddBinding(_removeMode = new ValueBinding<bool>(
                Group, "RemoveMode", false));
            AddBinding(_removeSelectionCount = new ValueBinding<int>(
                Group, "RemoveSelectionCount", 0));
            AddBinding(new TriggerBinding<bool>(Group, "SetRemoveMode",
                enabled => Tool.SetRemoveMode(enabled)));
            AddBinding(new TriggerBinding(Group, "RemoveSelectedPark",
                () => Tool.RemoveSelectedPark()));

            AddBinding(new TriggerBinding(Group, "ToggleTool", ToggleTool));
            AddBinding(new TriggerBinding(Group, "ClearPolygon",
                () => Tool.ClearPolygon()));
            AddBinding(new TriggerBinding<bool>(Group, "SetPlannerMode",
                enabled => Tool.SetPlannerMode(enabled)));
            AddBinding(new TriggerBinding(Group, "GeneratePaths",
                () => Tool.GeneratePaths()));
            AddBinding(new TriggerBinding(Group, "BuildPark",
                () => Tool.BuildPark()));
            AddBinding(new TriggerBinding<int>(Group, "SetPathType",
                value => Tool.SetPathType(value)));
            AddBinding(new TriggerBinding<bool>(Group, "SetLakeEnabled",
                value => Tool.SetLakeEnabled(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetSiteType",
                value => Tool.SetSiteKind(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetPlazaCenterPlacement",
                value => Tool.SetPlazaCenterPlacement(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetPlazaArrangementPlacement",
                value => Tool.SetPlazaArrangementPlacement(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetPlazaCenterpieceSpacing",
                value => Tool.SetPlazaCenterpieceSpacing(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetPlazaArrangementSpacing",
                value => Tool.SetPlazaArrangementSpacing(value)));
            AddBinding(new TriggerBinding<bool>(Group, "SetPlazaFenceEnabled",
                value => Tool.SetPlazaFenceEnabled(value)));
            AddBinding(new TriggerBinding<string>(Group, "SelectPlazaCenter",
                value => Tool.SelectPlazaCenter(value)));
            AddBinding(new TriggerBinding<string>(Group, "EditPlazaArrangement",
                value => Tool.EditPlazaArrangement(value)));
            AddBinding(new TriggerBinding(Group, "RemoveBuiltPaths",
                () => Tool.RemoveBuiltPaths()));
            AddBinding(new TriggerBinding<string>(Group, "SelectAsset",
                value => Catalog.Select(value)));
            AddBinding(new TriggerBinding(Group, "GenerateDecorations",
                () => Tool.GenerateDecorations()));
            AddBinding(new TriggerBinding<int>(Group, "SetVegetationDensity",
                value => Tool.SetVegetationDensity(value)));
            AddBinding(new TriggerBinding<int>(Group, "SetFurnitureDensity",
                value => Tool.SetFurnitureDensity(value)));
            AddBinding(new TriggerBinding<int>(Group, "ToggleDecorationCategory",
                value => Tool.ToggleDecorationCategory(value)));
            AddBinding(new TriggerBinding(Group, "FinishPark",
                () => Tool.FinishPark()));
        }

        private void ToggleTool()
        {
            var tools = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            var parkTool = Tool;
            var activating = tools.activeTool != parkTool;
            tools.activeTool = activating
                ? parkTool
                : World.GetOrCreateSystemManaged<Game.Tools.DefaultToolSystem>();
            // Asset Icon Library may populate UIObject icons after our first
            // prefab scan. Refresh on opening the tool, when all UI mods have
            // normally finished initializing, so the compact tiles gain their
            // real thumbnails without a manual reload button.
            if (activating) Catalog.RequestRefresh();
        }

        internal void SetToolActive(bool active)
        {
            _toolActive?.Update(active);
            _panelOpen?.Update(active);
        }

        internal void SetPolygonState(int pointCount, float polygonArea,
            bool closed, bool valid, string status)
        {
            _pointCount?.Update(pointCount);
            _polygonArea?.Update(polygonArea);
            _polygonClosed?.Update(closed);
            _polygonValid?.Update(valid);
            _status?.Update(status);
        }

        internal void SetAssetOptions(string optionsJson)
            => _assetOptionsJson?.Update(optionsJson ?? "{}");

        /// <summary>Remove mode and the element count of the selected park (0 = none).</summary>
        internal void SetRemoveState(bool removeMode, int selectionCount)
        {
            _removeMode?.Update(removeMode);
            _removeSelectionCount?.Update(selectionCount);
        }

        internal void SetPlannerState(bool plannerMode, int entranceCount,
            bool pathPlanReady)
        {
            _plannerMode?.Update(plannerMode);
            _entranceCount?.Update(entranceCount);
            _pathPlanReady?.Update(pathPlanReady);
        }

        internal void SetPathBuildState(bool busy, bool present, string summary,
            PathBuildStatus status)
        {
            _pathBuildBusy?.Update(busy);
            _pathBuildPresent?.Update(present);
            _pathBuildSummary?.Update(summary);
            _pathBuildStatus?.Update(status.ToString().ToLowerInvariant());
        }

        internal void SetPathType(int type) => _pathType?.Update(type);

        internal void SetSiteType(int type) => _siteType?.Update(type);

        internal void SetLakeEnabled(bool enabled) => _lakeEnabled?.Update(enabled);

        internal void SetPlazaPlacementSettings(int centerPlacement,
            int arrangementPlacement, int centerpieceSpacing,
            int arrangementSpacing, bool fenceEnabled)
        {
            _plazaCenterPlacement?.Update(centerPlacement);
            _plazaArrangementPlacement?.Update(arrangementPlacement);
            _plazaCenterpieceSpacing?.Update(centerpieceSpacing);
            _plazaArrangementSpacing?.Update(arrangementSpacing);
            _plazaFenceEnabled?.Update(fenceEnabled);
        }

        internal void SetPlazaArrangement(string json)
            => _plazaArrangementJson?.Update(json ?? "[]");

        internal void SetPlazaCenterOptions(string optionsJson,
            string selectedName)
        {
            _plazaCenterOptionsJson?.Update(optionsJson ?? "[]");
            _plazaCenterSelected?.Update(selectedName ?? string.Empty);
        }

        internal void SetDecorationState(int vegetationDensity,
            int furnitureDensity, int enabledMask, bool planReady,
            bool busy, bool present, string summary)
        {
            _vegetationDensity?.Update(vegetationDensity);
            _furnitureDensity?.Update(furnitureDensity);
            _decorationEnabledMask?.Update(enabledMask);
            _decorationPlanReady?.Update(planReady);
            _decorationBuildBusy?.Update(busy);
            _decorationBuildPresent?.Update(present);
            _decorationSummary?.Update(summary);
        }

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
