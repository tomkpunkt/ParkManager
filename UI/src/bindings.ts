import { bindValue, trigger } from "cs2/api";

const MOD = "ParkManager";

export const panelOpen$ = bindValue<boolean>(MOD, "PanelOpen", false);
export const toolActive$ = bindValue<boolean>(MOD, "ToolActive", false);
export const pointCount$ = bindValue<number>(MOD, "PointCount", 0);
export const polygonArea$ = bindValue<number>(MOD, "PolygonArea", 0);
export const polygonClosed$ = bindValue<boolean>(MOD, "PolygonClosed", false);
export const polygonValid$ = bindValue<boolean>(MOD, "PolygonValid", false);
export const status$ = bindValue<string>(MOD, "Status", "");
export const plannerMode$ = bindValue<boolean>(MOD, "PlannerMode", false);
export const entranceCount$ = bindValue<number>(MOD, "EntranceCount", 0);
export const pathPlanReady$ = bindValue<boolean>(MOD, "PathPlanReady", false);
export const pathBuildBusy$ = bindValue<boolean>(MOD, "PathBuildBusy", false);
export const pathBuildPresent$ = bindValue<boolean>(MOD, "PathBuildPresent", false);
export const pathBuildSummary$ = bindValue<string>(MOD, "PathBuildSummary", "");
export type PathBuildStatus = "ok" | "warning" | "error";
export const pathBuildStatus$ = bindValue<PathBuildStatus>(MOD, "PathBuildStatus", "ok");
export const pathType$ = bindValue<number>(MOD, "PathType", 1);
export const siteType$ = bindValue<number>(MOD, "SiteType", 0);
export const plazaCenterPlacement$ = bindValue<number>(MOD, "PlazaCenterPlacement", 0);
export const plazaArrangementPlacement$ = bindValue<number>(MOD, "PlazaArrangementPlacement", 0);
export const plazaCenterpieceSpacing$ = bindValue<number>(MOD, "PlazaCenterpieceSpacing", 20);
export const plazaArrangementSpacing$ = bindValue<number>(MOD, "PlazaArrangementSpacing", 4);
export const plazaFenceEnabled$ = bindValue<boolean>(MOD, "PlazaFenceEnabled", false);
export const plazaCenterOptionsJson$ = bindValue<string>(MOD, "PlazaCenterOptionsJson", "[]");
export const plazaCenterSelected$ = bindValue<string>(MOD, "PlazaCenterSelected", "");
export const plazaArrangementJson$ = bindValue<string>(MOD, "PlazaArrangementJson", "[]");
export const snapMask$ = bindValue<number>(MOD, "SnapMask", 0);
export const vegetationDensity$ = bindValue<number>(MOD, "VegetationDensity", 100);
export const furnitureDensity$ = bindValue<number>(MOD, "FurnitureDensity", 100);
export const decorationEnabledMask$ = bindValue<number>(MOD, "DecorationEnabledMask", 0x2f);
export const decorationPlanReady$ = bindValue<boolean>(MOD, "DecorationPlanReady", false);
export const decorationBuildBusy$ = bindValue<boolean>(MOD, "DecorationBuildBusy", false);
export const decorationBuildPresent$ = bindValue<boolean>(MOD, "DecorationBuildPresent", false);
export const decorationSummary$ = bindValue<string>(MOD, "DecorationSummary", "");
export const locale$ = bindValue<string>(MOD, "Locale", "en");
export const assetOptionsJson$ = bindValue<string>(MOD, "AssetOptionsJson", "{}");
export const parkPaletteOptionsJson$ = bindValue<string>(MOD, "ParkPaletteOptionsJson", "[]");
export const selectedParkPalette$ = bindValue<string>(MOD, "SelectedParkPalette", "");
export const selectedSnapMask$ = bindValue<number>("tool", "selectedSnapMask", 0);
export const togglePanel = () => trigger(MOD, "TogglePanel");
export const toggleTool = () => trigger(MOD, "ToggleTool");
export const clearPolygon = () => trigger(MOD, "ClearPolygon");
export const setPlannerMode = (enabled: boolean) => trigger(MOD, "SetPlannerMode", enabled);
export const generatePaths = () => trigger(MOD, "GeneratePaths");
export const buildPaths = () => trigger(MOD, "BuildPaths");
export const setPathType = (type: number) => trigger(MOD, "SetPathType", type);
export const setSiteType = (type: number) => trigger(MOD, "SetSiteType", type);
export const setPlazaCenterPlacement = (value: number) =>
  trigger(MOD, "SetPlazaCenterPlacement", value);
export const setPlazaArrangementPlacement = (value: number) =>
  trigger(MOD, "SetPlazaArrangementPlacement", value);
export const setPlazaCenterpieceSpacing = (value: number) =>
  trigger(MOD, "SetPlazaCenterpieceSpacing", value);
export const setPlazaArrangementSpacing = (value: number) =>
  trigger(MOD, "SetPlazaArrangementSpacing", value);
export const setPlazaFenceEnabled = (value: boolean) =>
  trigger(MOD, "SetPlazaFenceEnabled", value);
export const selectPlazaCenter = (name: string) =>
  trigger(MOD, "SelectPlazaCenter", name);
export const editPlazaArrangement = (command: string) =>
  trigger(MOD, "EditPlazaArrangement", command);
export const removeBuiltPaths = () => trigger(MOD, "RemoveBuiltPaths");
export const generateDecorations = () => trigger(MOD, "GenerateDecorations");
export const setVegetationDensity = (density: number) =>
  trigger(MOD, "SetVegetationDensity", density);
export const setFurnitureDensity = (density: number) =>
  trigger(MOD, "SetFurnitureDensity", density);
export const toggleDecorationCategory = (kind: number) =>
  trigger(MOD, "ToggleDecorationCategory", kind);
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
