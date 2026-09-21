import { bindValue, trigger } from "cs2/api";

const MOD = "ParkManager";

export const panelOpen$ = bindValue<boolean>(MOD, "PanelOpen", false);
export const toolActive$ = bindValue<boolean>(MOD, "ToolActive", false);
export const pointCount$ = bindValue<number>(MOD, "PointCount", 0);
export const polygonClosed$ = bindValue<boolean>(MOD, "PolygonClosed", false);
export const polygonValid$ = bindValue<boolean>(MOD, "PolygonValid", false);
export const status$ = bindValue<string>(MOD, "Status", "");
export const plannerMode$ = bindValue<boolean>(MOD, "PlannerMode", false);
export const entranceCount$ = bindValue<number>(MOD, "EntranceCount", 0);
export const pathPlanReady$ = bindValue<boolean>(MOD, "PathPlanReady", false);
export const pathBuildBusy$ = bindValue<boolean>(MOD, "PathBuildBusy", false);
export const pathBuildPresent$ = bindValue<boolean>(MOD, "PathBuildPresent", false);
export const pathBuildSummary$ = bindValue<string>(MOD, "PathBuildSummary", "");
export const snapMask$ = bindValue<number>(MOD, "SnapMask", 0);
export const fenceEnabled$ = bindValue<boolean>(MOD, "FenceEnabled", false);
export const vegetationDensity$ = bindValue<number>(MOD, "VegetationDensity", 100);
export const decorationPlanReady$ = bindValue<boolean>(MOD, "DecorationPlanReady", false);
export const decorationBuildBusy$ = bindValue<boolean>(MOD, "DecorationBuildBusy", false);
export const decorationBuildPresent$ = bindValue<boolean>(MOD, "DecorationBuildPresent", false);
export const decorationSummary$ = bindValue<string>(MOD, "DecorationSummary", "");
export const parkCount$ = bindValue<number>(MOD, "ParkCount", 0);
export const locale$ = bindValue<string>(MOD, "Locale", "en");
export const assetOptionsJson$ = bindValue<string>(MOD, "AssetOptionsJson", "{}");
export const parkPaletteOptionsJson$ = bindValue<string>(MOD, "ParkPaletteOptionsJson", "[]");
export const selectedParkPalette$ = bindValue<string>(MOD, "SelectedParkPalette", "");
export const selectedSnapMask$ = bindValue<number>("tool", "selectedSnapMask", 0);
export const togglePanel = () => trigger(MOD, "TogglePanel");
export const toggleTool = () => trigger(MOD, "ToggleTool");
export const clearPolygon = () => trigger(MOD, "ClearPolygon");
export const togglePlannerMode = () => trigger(MOD, "TogglePlannerMode");
export const generatePaths = () => trigger(MOD, "GeneratePaths");
export const buildPaths = () => trigger(MOD, "BuildPaths");
export const removeBuiltPaths = () => trigger(MOD, "RemoveBuiltPaths");
export const generateDecorations = () => trigger(MOD, "GenerateDecorations");
export const toggleFence = () => trigger(MOD, "ToggleFence");
export const setVegetationDensity = (density: number) =>
  trigger(MOD, "SetVegetationDensity", density);
export const buildDecorations = () => trigger(MOD, "BuildDecorations");
export const removeBuiltDecorations = () => trigger(MOD, "RemoveBuiltDecorations");
export const finishPark = () => trigger(MOD, "FinishPark");
export const selectAsset = (category: string, name: string, multi = false) =>
  trigger(MOD, "SelectAsset", `${category}\n${multi ? "multi" : "single"}\n${name}`);
export const selectParkPalette = (name: string) =>
  trigger(MOD, "SelectParkPalette", name);
export const setSelectedSnapMask = (mask: number) =>
  trigger("tool", "setSelectedSnapMask", mask);
export const setPanelOpen = (open: boolean) =>
  trigger(MOD, "SetPanelOpen", open);
