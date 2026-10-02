import { useEffect, useState } from "react";
import { useValue } from "cs2/api";
import {
  assetOptionsJson$, buildPark, clearPolygon,
  decorationBuildBusy$, decorationBuildPresent$, decorationEnabledMask$,
  decorationPlanReady$, decorationSummary$,
  entranceCount$, finishPark, generateDecorations, generatePaths,
  furnitureDensity$, lakeEnabled$, locale$, panelOpen$, pathBuildBusy$,
  pathBuildPresent$, pathBuildStatus$, pathBuildSummary$, pathPlanReady$,
  pathType$, plannerMode$, pointCount$,
  plazaArrangementPlacement$, plazaArrangementSpacing$,
  plazaCenterOptionsJson$, plazaCenterSelected$, plazaCenterPlacement$,
  plazaCenterpieceSpacing$, plazaFenceEnabled$,
  polygonArea$, polygonClosed$, polygonValid$, removeBuiltPaths, removeMode$,
  removeSelectedPark, removeSelectionCount$, setPlannerMode, setRemoveMode,
  setSiteType, siteType$, toggleTool, vegetationDensity$,
} from "./bindings";
import { assetCategories, NO_PLAZA_CENTER, parseAssetChoices,
  parsePlazaCenterOptions } from "./assetChoices";
import { AssetChooser } from "./components/AssetCatalog";
import { Segmented } from "./components/Bar";
import { Hint } from "./components/Hint";
import { ParkColumns } from "./components/ParkColumns";
import { PlazaArrangementEditor } from "./components/PlazaArrangementEditor";
import { PlazaColumns } from "./components/PlazaColumns";
import { SnapControls } from "./components/SnapControls";
import { getTexts } from "./i18n";
import brandLogo from "./assets/park-manager.svg";
import styles from "./panel.module.less";
import { deriveNextAction, isHintAction, NextAction } from "./workflow";
import { formatUiText, parseUiText } from "./uiText";

const stop = (event: any) => event.stopPropagation();
const ARRANGEMENT_WINDOW = "arrangement";

/**
 * Flat bar at the top of the screen: every setting stays reachable, one main
 * button always offers the next step, and large asset choices open in a
 * window below the bar instead of growing it.
 */
export const ParkManagerPanel = () => {
  // Hooks stay unconditional: conditional hooks caused React #310 in Cohtml.
  const [windowKey, setWindowKey] = useState<string | null>(null);
  const open = useValue(panelOpen$);
  const t = getTexts(useValue(locale$));
  const pointCount = useValue(pointCount$);
  const polygonArea = useValue(polygonArea$);
  const closed = useValue(polygonClosed$);
  const valid = useValue(polygonValid$);
  const plannerMode = useValue(plannerMode$);
  const entranceCount = useValue(entranceCount$);
  const pathPlanReady = useValue(pathPlanReady$);
  const pathBuildBusy = useValue(pathBuildBusy$);
  const pathBuildPresent = useValue(pathBuildPresent$);
  const rawPathBuildSummary = useValue(pathBuildSummary$);
  const pathBuildStatus = useValue(pathBuildStatus$);
  const pathType = useValue(pathType$);
  const lakeEnabled = useValue(lakeEnabled$);
  const siteType = useValue(siteType$);
  const plazaCenterPlacement = useValue(plazaCenterPlacement$);
  const plazaArrangementPlacement = useValue(plazaArrangementPlacement$);
  const plazaCenterpieceSpacing = useValue(plazaCenterpieceSpacing$);
  const plazaArrangementSpacing = useValue(plazaArrangementSpacing$);
  const plazaFenceEnabled = useValue(plazaFenceEnabled$);
  const plazaCenterOptions = parsePlazaCenterOptions(
    useValue(plazaCenterOptionsJson$));
  const plazaCenterSelected = useValue(plazaCenterSelected$);
  const vegetationDensity = useValue(vegetationDensity$);
  const furnitureDensity = useValue(furnitureDensity$);
  const decorationEnabledMask = useValue(decorationEnabledMask$);
  const decorationPlanReady = useValue(decorationPlanReady$);
  const rawDecorationSummary = useValue(decorationSummary$);
  const decorationBuildBusy = useValue(decorationBuildBusy$);
  const decorationBuildPresent = useValue(decorationBuildPresent$);
  const assetChoices = parseAssetChoices(useValue(assetOptionsJson$));
  const removeMode = useValue(removeMode$);
  const removeSelectionCount = useValue(removeSelectionCount$);

  useEffect(() => { if (!open) setWindowKey(null); }, [open]);
  useEffect(() => setWindowKey(null), [siteType]);

  if (!open) return null;

  const isPlaza = siteType === 1;
  const busy = pathBuildBusy || decorationBuildBusy;
  const structureLocked = busy || pathBuildPresent;
  const furnishingLocked = busy || decorationBuildPresent;
  const surfaceChoice = assetChoices.surface;
  const surfaceSelected = !!surfaceChoice?.selected
    && surfaceChoice.options.some((option) => option.name === surfaceChoice.selected);
  const canPlan = !isPlaza || plazaCenterSelected === NO_PLAZA_CENTER
    || plazaCenterOptions.some((option) => option.name === plazaCenterSelected);

  // The C# systems publish message keys; resolve them for the game language.
  const pathBuildSummary = formatUiText(rawPathBuildSummary, t.messages);
  const decorationSummary = formatUiText(rawDecorationSummary, t.messages);
  const parsedDecorationSummary = parseUiText(rawDecorationSummary);
  const arrangementDoesNotFit = typeof parsedDecorationSummary !== "string"
    && parsedDecorationSummary.k === "plaza.arrangementDoesNotFit";
  const pathNotice = pathBuildStatus !== "ok";

  const action = deriveNextAction({ polygonValid: valid, plannerMode,
    entranceCount, pathsPlanned: pathPlanReady, surfaceSelected,
    decorationsPlanned: decorationPlanReady, pathsBuilt: pathBuildPresent,
    decorationsBuilt: decorationBuildPresent, busy });
  const actionText: Record<NextAction, string> = {
    drawOutline: t.actionDrawOutline,
    placeEntrances: isPlaza ? t.actionPlaceAccess : t.actionPlaceEntrances,
    markEntrance: isPlaza ? t.actionMarkAccess : t.actionMarkEntrance,
    plan: isPlaza ? t.actionPlanPlaza : t.actionPlanPark,
    chooseSurface: t.actionChooseSurface,
    planFurnishings: t.generateDecorations,
    build: isPlaza ? t.buildPlaza : t.buildPark,
    finish: isPlaza ? t.finishPlaza : t.finishPark,
    busy: t.busy,
  };
  const runAction = () => {
    switch (action) {
      case "placeEntrances": setPlannerMode(true); break;
      case "plan": if (canPlan) generatePaths(); break;
      case "planFurnishings": generateDecorations(); break;
      case "build": buildPark(); break;
      case "finish": finishPark(); break;
      default: break;
    }
  };
  const actionDisabled = isHintAction(action) || (action === "plan" && !canPlan);

  const outlineState = pointCount === 0 ? t.outlineEmpty
    : !closed ? t.outlineOpen(pointCount)
      : !valid ? t.outlineInvalid(pointCount) : t.outlineReady(pointCount);
  const toggleWindow = (key: string) =>
    setWindowKey(windowKey === key ? null : key);

  const renderWindow = () => {
    if (!windowKey) return null;
    const arrangement = windowKey === ARRANGEMENT_WINDOW;
    return <div className={styles.window} data-testid="asset-window"
      onMouseDown={stop} onMouseUp={stop} onClick={stop} onContextMenu={stop}>
      <div className={styles.windowHead}>
        {arrangement ? <strong>{t.plazaArrangement}</strong>
          : <div className={styles.windowTabs}>
            {assetCategories.map((item) => <button key={item.key} type="button"
              className={item.key === windowKey ? styles.windowTabActive : ""}
              aria-pressed={item.key === windowKey}
              onClick={() => setWindowKey(item.key)}>
              {t.categories[item.key]}</button>)}
          </div>}
        <Hint text={t.closeWindow}>
          <button type="button" className={styles.closeButton}
            aria-label={t.closeWindow} onClick={() => setWindowKey(null)}>×</button>
        </Hint>
      </div>
      <div className={styles.windowBody}>
        {arrangement
          ? <PlazaArrangementEditor t={t} choices={assetChoices}
              busy={furnishingLocked} />
          : <AssetChooser t={t} choices={assetChoices} categoryKey={windowKey}
              busy={furnishingLocked} />}
      </div>
    </div>;
  };

  return <div className={styles.shell}>
    <div className={styles.bar} data-testid="park-panel"
      onMouseDown={stop} onMouseUp={stop} onClick={stop} onContextMenu={stop}>
      <div className={styles.head} data-testid="panel-header">
        <div className={styles.brand}>
          <img src={brandLogo} alt="" data-testid="brand-logo" />
          <span>ParkManager</span>
        </div>
        <Segmented value={siteType} disabled={structureLocked}
          testId="site-type-selector" onChange={setSiteType}
          options={[{ value: 0, text: t.sitePark, hint: t.siteParkHint },
            { value: 1, text: t.sitePlaza, hint: t.sitePlazaHint }]} />
        <Segmented value={plannerMode} disabled={structureLocked}
          testId="mode-selector" onChange={setPlannerMode}
          options={[{ value: false, text: t.modeOutline, hint: t.modeOutlineHint },
            { value: true, text: isPlaza ? t.modeAccess : t.modeEntrances,
              hint: isPlaza ? t.modeAccessHint : t.modeEntrancesHint }]} />
        {/* Routine status stays out of the bar; only build problems show. */}
        {pathNotice ? <div className={`${styles.headStatus} ${styles.headStatusWarning}`}
          role="alert" data-testid="panel-status" data-status={pathBuildStatus}>
          {pathBuildSummary}
        </div> : <div className={styles.headSpacer} />}
        {!plannerMode && !pathBuildPresent ? <SnapControls t={t} /> : null}
        <Hint text={t.close}>
          <button type="button" className={styles.closeButton} aria-label={t.close}
            onClick={toggleTool}>×</button>
        </Hint>
      </div>

      <div className={styles.columns} data-testid="panel-body"
        data-paths-built={pathBuildPresent} data-decorations-built={decorationBuildPresent}>
        {isPlaza ? <PlazaColumns t={t} choices={assetChoices}
          centerOptions={plazaCenterOptions} centerSelected={plazaCenterSelected}
          centerPlacement={plazaCenterPlacement}
          arrangementPlacement={plazaArrangementPlacement}
          centerpieceSpacing={plazaCenterpieceSpacing}
          arrangementSpacing={plazaArrangementSpacing}
          fenceEnabled={plazaFenceEnabled} density={furnitureDensity}
          pathPlanReady={pathPlanReady} decorationPlanReady={decorationPlanReady}
          structureLocked={structureLocked} furnishingLocked={furnishingLocked}
          canPlan={canPlan}
          notice={arrangementDoesNotFit ? decorationSummary : null}
          arrangementOpen={windowKey === ARRANGEMENT_WINDOW}
          onEditArrangement={() => toggleWindow(ARRANGEMENT_WINDOW)} />
          : <ParkColumns t={t} choices={assetChoices} pathType={pathType}
          lakeEnabled={lakeEnabled} vegetationDensity={vegetationDensity}
          furnitureDensity={furnitureDensity}
          enabledMask={decorationEnabledMask} pathPlanReady={pathPlanReady}
          decorationPlanReady={decorationPlanReady}
          structureLocked={structureLocked} furnishingLocked={furnishingLocked}
          canPlan={canPlan} openKey={windowKey} onOpen={toggleWindow} />}

        <section className={styles.actionColumn} data-testid="action-column">
          <div className={styles.facts}>
            <span data-testid="outline-state">{outlineState}</span>
            <span>{pointCount >= 3
              ? t.outlineArea(Math.round(polygonArea).toLocaleString()) : "—"}
              {" · "}{isPlaza ? t.plazaAccesses(entranceCount)
                : t.pathsGates(entranceCount)}</span>
          </div>
          {/* Remove mode replaces the next-step button: pick, then confirm. */}
          {removeMode
            ? <Hint text={removeSelectionCount > 0
                ? t.removeSelectedHint : t.removePickHint}>
                <button type="button" data-testid="main-action"
                  data-action={removeSelectionCount > 0 ? "removePark" : "pickPark"}
                  className={`${styles.mainAction} ${removeSelectionCount > 0
                    ? styles.mainActionDanger : ""}`}
                  disabled={removeSelectionCount === 0} onClick={removeSelectedPark}>
                  {removeSelectionCount > 0
                    ? t.removeSelected(removeSelectionCount) : t.removePick}</button>
              </Hint>
            : <Hint text={t.actionHints[action]}>
                <button type="button" data-testid="main-action" data-action={action}
                  className={`${styles.mainAction} ${action === "build"
                    ? styles.mainActionBuild : ""}`}
                  disabled={actionDisabled} onClick={runAction}>
                  {actionText[action]}</button>
              </Hint>}
          <div className={styles.actionRow}>
            {removeMode
              ? <button type="button" className={styles.quietButton}
                  data-testid="remove-mode-cancel"
                  onClick={() => setRemoveMode(false)}>{t.removeCancel}</button>
              : pathBuildPresent
                ? <button type="button" className={styles.dangerButton} disabled={busy}
                    data-testid="remove-built" onClick={removeBuiltPaths}>
                    {isPlaza ? t.removePlaza : t.removePark}</button>
                // With an empty workspace there is nothing to reset; the slot
                // offers removing an already finished park instead.
                : pointCount === 0 && !busy
                  ? <Hint text={t.removeBuiltParkHint}>
                      <button type="button" className={styles.quietButton}
                        data-testid="remove-mode"
                        onClick={() => setRemoveMode(true)}>{t.removeBuiltPark}</button>
                    </Hint>
                  : <button type="button" className={styles.quietButton}
                      disabled={busy || pointCount === 0} data-testid="reset-outline"
                      onClick={clearPolygon}>{t.reset}</button>}
          </div>
        </section>
      </div>
    </div>
    {renderWindow()}
  </div>;
};
