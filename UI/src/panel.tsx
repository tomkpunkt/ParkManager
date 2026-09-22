import { useEffect, useState } from "react";
import { useValue } from "cs2/api";
import {
  assetOptionsJson$, buildDecorations, buildPaths, clearPolygon,
  decorationBuildBusy$, decorationBuildPresent$, decorationEnabledMask$,
  decorationPlanReady$,
  entranceCount$, finishPark, generateDecorations, generatePaths,
  furnitureDensity$, locale$, panelOpen$, pathBuildBusy$,
  pathBuildPresent$, pathPlanReady$, pathType$, plannerMode$, pointCount$,
  polygonArea$, polygonClosed$, polygonValid$, removeBuiltDecorations, removeBuiltPaths,
  selectAsset, setPathType, setSiteType, siteType$, togglePlannerMode, toggleTool,
  vegetationDensity$,
} from "./bindings";
import { parseAssetChoices } from "./assetChoices";
import { AssetCatalog } from "./components/AssetCatalog";
import { SnapControls, StatePill } from "./components/WorkflowParts";
import { getTexts } from "./i18n";
import arrowLeftIcon from "./assets/arrow-left.svg";
import arrowRightIcon from "./assets/arrow-right.svg";
import refreshIcon from "./assets/refresh.svg";
import styles from "./panel.module.less";

const stop = (event: any) => event.stopPropagation();

/**
 * Four-step workflow shell. Detailed snapping and asset-selection concerns
 * live in dedicated components so this file only coordinates workflow state.
 */
export const ParkManagerPanel = () => {
  // Hooks stay unconditional: conditional hooks caused React #310 in Cohtml.
  const [activeStage, setActiveStage] = useState(0);
  const [failedSurfaceIcons, setFailedSurfaceIcons] = useState<Record<string, boolean>>({});
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
  const pathType = useValue(pathType$);
  const siteType = useValue(siteType$);
  const vegetationDensity = useValue(vegetationDensity$);
  const furnitureDensity = useValue(furnitureDensity$);
  const decorationEnabledMask = useValue(decorationEnabledMask$);
  const decorationPlanReady = useValue(decorationPlanReady$);
  const decorationBuildBusy = useValue(decorationBuildBusy$);
  const decorationBuildPresent = useValue(decorationBuildPresent$);
  const assetChoices = parseAssetChoices(useValue(assetOptionsJson$));
  const surfaceChoice = assetChoices.surface;
  const visibleSurfaces = (surfaceChoice?.options ?? []).filter((option) =>
    !failedSurfaceIcons[option.icon]);

  const busy = pathBuildBusy || decorationBuildBusy;
  const workflowStage = decorationBuildPresent ? 3
    : pathBuildPresent ? 2 : plannerMode ? 1 : 0;
  const outlineState = pointCount === 0 ? t.outlineEmpty
    : !closed ? t.outlineOpen(pointCount)
      : !valid ? t.outlineInvalid(pointCount) : t.outlineReady(pointCount);

  useEffect(() => {
    if (open) setActiveStage(workflowStage);
  }, [open, workflowStage]);

  if (!open) return null;

  const canOpenStage = (index: number) => (index === 0 && !pathBuildPresent)
    || (index === 1 && valid)
    || (index === 2 && pathBuildPresent)
    || (index === 3 && decorationBuildPresent);

  const openStage = (index: number) => {
    if (busy || !canOpenStage(index)) return;
    setActiveStage(index);
    if (index === 0 && plannerMode) togglePlannerMode();
    else if (index > 0 && !plannerMode) togglePlannerMode();
  };

  const renderOutline = () => (
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>1 / 4 · {t.steps[0]}</span>
          <h2>{t.outlineTitle}</h2>
          <p>{t.outlineText}</p>
          <div className={styles.stateRow}>
            <StatePill success={valid}>{outlineState}</StatePill>
            {pointCount >= 3
              ? <StatePill>{t.outlineArea(Math.round(polygonArea).toLocaleString())}</StatePill>
              : null}
          </div>
        </div>
      </div>
  );

  const renderPaths = () => (
      <div className={`${styles.stageColumn} ${styles.pathStageColumn}`}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>2 / 4 · {t.steps[1]}</span>
          <h2>{t.pathsTitle}</h2>
          <p>{pathPlanReady ? t.pathsPreview
            : entranceCount > 0 ? t.pathsGates(entranceCount) : t.pathsNoGate}</p>
          <div className={styles.stateRow}>
            <StatePill success={entranceCount > 0}>{t.pathsGates(entranceCount)}</StatePill>
            {pathPlanReady ? <StatePill success>{t.steps[1]}</StatePill> : null}
          </div>
        </div>
        <div className={styles.pathSettings}>
          <div className={styles.compactSetting}>
            <span>{t.siteType}</span>
            <div className={styles.segmentedControl}>
              <button className={siteType === 0 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setSiteType(0)}>{t.sitePark}</button>
              <button className={siteType === 1 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setSiteType(1)}>{t.sitePlaza}</button>
            </div>
          </div>
          <div className={styles.compactSetting}>
            <span>{t.pathType}</span>
            <div className={styles.segmentedControl}>
              <button className={pathType === 0 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setPathType(0)}>{t.pathNarrow}</button>
              <button className={pathType === 1 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setPathType(1)}>{t.pathWide}</button>
            </div>
          </div>
          <div className={styles.compactSetting}>
            <span>{t.background}</span>
            <div className={styles.surfaceChoices}>
              {visibleSurfaces.map((option) => (
                <button key={option.name} title={option.name}
                  className={surfaceChoice?.selected === option.name
                    ? styles.surfaceChoiceActive : ""}
                  disabled={busy || pathBuildPresent}
                  onClick={() => selectAsset("Surface", option.name)}>
                  <img src={option.icon} alt=""
                    onError={() => setFailedSurfaceIcons((current) =>
                      current[option.icon] ? current
                        : { ...current, [option.icon]: true })} />
                </button>
              ))}
            </div>
          </div>
        </div>
      </div>
  );

  const renderAssets = () => (
    <div className={styles.assetStage}>
      <AssetCatalog t={t} choices={assetChoices}
        busy={busy || decorationBuildPresent}
        vegetationDensity={vegetationDensity}
        furnitureDensity={furnitureDensity}
        enabledMask={decorationEnabledMask}
        decorationBuildPresent={decorationBuildPresent}
        decorationPlanReady={decorationPlanReady} />
    </div>
  );

  const renderComplete = () => (
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>4 / 4 · {t.steps[3]}</span>
          <h2>{t.completeTitle}</h2>
          <p>{t.completeText}</p>
          <div className={styles.stateRow}>
            <StatePill success>{t.pathsBuilt}</StatePill>
            <StatePill success>{t.decorationsBuilt}</StatePill>
          </div>
        </div>
      </div>
  );

  const renderFooter = () => (
    <div className={styles.panelFooter}>
      <div className={styles.footerLeft}>
        {activeStage === 1 ? <button className={styles.backButton}
          disabled={busy || pathBuildPresent}
          onClick={() => openStage(0)}>
          <img className={styles.buttonIcon} src={arrowLeftIcon} alt="" />
          {t.editOutline}</button> : null}
        {activeStage === 2 ? <button className={styles.dangerButton}
          disabled={busy} onClick={removeBuiltPaths}>{t.removePark}</button> : null}
        {activeStage === 3 ? <button className={styles.backButton}
          disabled={busy} onClick={removeBuiltDecorations}>
          {t.removeDecorations}</button> : null}
      </div>
      <div className={styles.footerRight}>
        {activeStage === 0 ? <>
          <button className={styles.secondaryButton} disabled={pointCount === 0}
            onClick={clearPolygon}>{t.reset}</button>
          <button className={styles.primaryButton} disabled={!valid}
            onClick={() => openStage(1)}>
            {t.continuePaths}<img className={styles.buttonIcon} src={arrowRightIcon} alt="" />
          </button>
        </> : null}
        {activeStage === 1 ? <>
          <button className={styles.secondaryButton}
            disabled={busy || pathBuildPresent || entranceCount === 0 || !pathPlanReady}
            onClick={generatePaths}>
            <img className={styles.buttonIcon} src={refreshIcon} alt="" />
            {t.recalculatePaths}</button>
          <button className={styles.primaryButton}
            disabled={busy || pathBuildPresent || entranceCount === 0}
            onClick={pathPlanReady ? buildPaths : generatePaths}>
            {pathBuildBusy ? t.busy : pathPlanReady ? t.buildPaths : t.generatePaths}
            <img className={styles.buttonIcon} src={arrowRightIcon} alt="" />
          </button>
        </> : null}
        {activeStage === 2 ? <>
          <button className={styles.secondaryButton}
            disabled={busy || decorationBuildPresent || !decorationPlanReady}
            onClick={generateDecorations}>
            <img className={styles.buttonIcon} src={refreshIcon} alt="" />
            {t.replanDecorations}</button>
          <button className={styles.primaryButton}
            disabled={busy || decorationBuildPresent}
            onClick={decorationPlanReady ? buildDecorations : generateDecorations}>
            {decorationBuildBusy ? t.busy
              : decorationPlanReady ? t.buildDecorations : t.generateDecorations}
            <img className={styles.buttonIcon} src={arrowRightIcon} alt="" />
          </button>
        </> : null}
        {activeStage === 3 ? <>
          <button className={styles.dangerButton} disabled={busy}
            onClick={removeBuiltPaths}>{t.removePark}</button>
          <button className={styles.successButton} disabled={busy}
            onClick={finishPark}>{t.finishPark}</button>
        </> : null}
      </div>
    </div>
  );

  return (
    <div className={styles.panel} onMouseDown={stop} onMouseUp={stop}
      onClick={stop} onContextMenu={stop}>
      <div className={styles.panelHeader}>
        <div className={styles.brand}>
          <span className={styles.brandMark}>P</span><span>ParkManager</span>
        </div>
        <div className={styles.progress}>
          {t.steps.map((label, index) => (
            <button key={label} type="button"
              className={`${styles.progressStep} ${
                index === activeStage ? styles.progressStepActive : ""} ${
                index < workflowStage ? styles.progressStepDone : ""}`}
              disabled={busy || !canOpenStage(index)}
              aria-current={index === activeStage ? "step" : undefined}
              onClick={() => openStage(index)}>
              <span className={styles.progressNumber}>
                {index < workflowStage ? "✓" : index + 1}
              </span>
              <span>{label}</span>
            </button>
          ))}
        </div>
        <div className={styles.toolSlot}>
          {activeStage === 0 ? <SnapControls t={t} /> : null}
        </div>
        <button className={styles.closeButton} title={t.close}
          onClick={toggleTool}>×</button>
      </div>

      <div className={`${styles.panelBody} ${
        activeStage <= 1 || activeStage === 3 ? styles.compactBody : ""} ${
        activeStage === 2 ? styles.assetBody : ""}`}>
        {activeStage === 0 ? renderOutline()
          : activeStage === 1 ? renderPaths()
            : activeStage === 2 ? renderAssets() : renderComplete()}
      </div>
      {renderFooter()}
    </div>
  );
};
