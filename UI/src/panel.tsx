import { useEffect, useState } from "react";
import { useValue } from "cs2/api";
import {
  assetOptionsJson$, buildDecorations, buildPaths, clearPolygon,
  decorationBuildBusy$, decorationBuildPresent$, decorationPlanReady$,
  entranceCount$, fenceEnabled$, finishPark, generateDecorations, generatePaths,
  locale$, panelOpen$, parkCount$, pathBuildBusy$,
  pathBuildPresent$, pathPlanReady$, pathType$, plannerMode$, pointCount$,
  polygonClosed$, polygonValid$, removeBuiltDecorations, removeBuiltPaths,
  setPathType, togglePlannerMode, toggleTool, vegetationDensity$,
} from "./bindings";
import { parseAssetChoices } from "./assetChoices";
import { AssetCatalog } from "./components/AssetCatalog";
import { SnapControls, StatePill } from "./components/WorkflowParts";
import { getTexts } from "./i18n";
import styles from "./panel.module.less";

const stop = (event: any) => event.stopPropagation();

/**
 * Four-step workflow shell. Detailed snapping and asset-selection concerns
 * live in dedicated components so this file only coordinates workflow state.
 */
export const ParkManagerPanel = () => {
  // Hooks stay unconditional: conditional hooks caused React #310 in Cohtml.
  const [activeStage, setActiveStage] = useState(0);
  const open = useValue(panelOpen$);
  const t = getTexts(useValue(locale$));
  const pointCount = useValue(pointCount$);
  const closed = useValue(polygonClosed$);
  const valid = useValue(polygonValid$);
  const plannerMode = useValue(plannerMode$);
  const entranceCount = useValue(entranceCount$);
  const pathPlanReady = useValue(pathPlanReady$);
  const pathBuildBusy = useValue(pathBuildBusy$);
  const pathBuildPresent = useValue(pathBuildPresent$);
  const pathType = useValue(pathType$);
  const fenceEnabled = useValue(fenceEnabled$);
  const vegetationDensity = useValue(vegetationDensity$);
  const decorationPlanReady = useValue(decorationPlanReady$);
  const decorationBuildBusy = useValue(decorationBuildBusy$);
  const decorationBuildPresent = useValue(decorationBuildPresent$);
  const parkCount = useValue(parkCount$);
  const assetChoices = parseAssetChoices(useValue(assetOptionsJson$));

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
    <div className={styles.simpleWorkspace}>
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>1 / 4 · {t.steps[0]}</span>
          <h2>{t.outlineTitle}</h2>
          <p>{t.outlineText}</p>
          <div className={styles.stateRow}>
            <StatePill success={valid}>{outlineState}</StatePill>
            {parkCount > 0 ? <StatePill success>{t.builtParks(parkCount)}</StatePill> : null}
          </div>
        </div>
      </div>
      <div className={styles.workflowFooter}>
        <div className={styles.footerLeft} />
        <div className={styles.footerRight}>
          <button className={styles.secondaryButton} disabled={pointCount === 0}
            onClick={clearPolygon}>{t.reset}</button>
          <button className={styles.primaryButton} disabled={!valid}
            onClick={() => openStage(1)}>
            {t.continuePaths}<span className={styles.buttonArrow}>›</span>
          </button>
        </div>
      </div>
    </div>
  );

  const renderPaths = () => (
    <div className={styles.simpleWorkspace}>
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>2 / 4 · {t.steps[1]}</span>
          <h2>{t.pathsTitle}</h2>
          <p>{pathPlanReady ? t.pathsPreview
            : entranceCount > 0 ? t.pathsGates(entranceCount) : t.pathsNoGate}</p>
          <div className={styles.stateRow}>
            <StatePill success={entranceCount > 0}>{t.pathsGates(entranceCount)}</StatePill>
            {pathPlanReady ? <StatePill success>{t.steps[1]}</StatePill> : null}
          </div>
          <div className={styles.pathTypeControl}>
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
        </div>
      </div>
      <div className={styles.workflowFooter}>
        <div className={styles.footerLeft}>
          <button className={styles.backButton}
            disabled={busy || pathBuildPresent}
            onClick={() => openStage(0)}>‹ {t.editOutline}</button>
        </div>
        <div className={styles.footerRight}>
          <button className={styles.secondaryButton}
            disabled={busy || pathBuildPresent || entranceCount === 0 || !pathPlanReady}
            onClick={generatePaths}>{t.recalculatePaths}</button>
          <button className={styles.primaryButton}
            disabled={busy || pathBuildPresent || entranceCount === 0}
            onClick={pathPlanReady ? buildPaths : generatePaths}>
            {pathBuildBusy ? t.busy : pathPlanReady ? t.buildPaths : t.generatePaths}
            <span className={styles.buttonArrow}>›</span>
          </button>
        </div>
      </div>
    </div>
  );

  const renderAssets = () => (
    <div className={styles.assetStage}>
      <AssetCatalog t={t} choices={assetChoices}
        busy={busy || decorationBuildPresent}
        fenceEnabled={fenceEnabled} vegetationDensity={vegetationDensity}
        decorationBuildPresent={decorationBuildPresent}
        decorationPlanReady={decorationPlanReady} />
      <div className={styles.assetFooter}>
        <button className={styles.dangerButton} disabled={busy}
          onClick={removeBuiltPaths}>{t.removePark}</button>
        <div className={styles.footerActions}>
          <button className={styles.secondaryButton}
            disabled={busy || decorationBuildPresent || !decorationPlanReady}
            onClick={generateDecorations}>{t.replanDecorations}</button>
          <button className={styles.primaryButton}
            disabled={busy || decorationBuildPresent}
            onClick={decorationPlanReady ? buildDecorations : generateDecorations}>
            {decorationBuildBusy ? t.busy
              : decorationPlanReady ? t.buildDecorations : t.generateDecorations}
            <span className={styles.buttonArrow}>›</span>
          </button>
        </div>
      </div>
    </div>
  );

  const renderComplete = () => (
    <div className={styles.simpleWorkspace}>
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <span className={styles.eyebrow}>4 / 4 · {t.steps[3]}</span>
          <h2>{t.completeTitle}</h2>
          <p>{t.completeText}</p>
          <div className={styles.stateRow}>
            <StatePill success>{t.pathsBuilt}</StatePill>
            <StatePill success>{t.decorationsBuilt}</StatePill>
            <StatePill success>{t.builtParks(parkCount)}</StatePill>
          </div>
        </div>
      </div>
      <div className={styles.workflowFooter}>
        <div className={styles.footerLeft}>
          <button className={styles.backButton} disabled={busy}
            onClick={removeBuiltDecorations}>{t.removeDecorations}</button>
        </div>
        <div className={styles.footerRight}>
          <button className={styles.dangerButton} disabled={busy}
            onClick={removeBuiltPaths}>{t.removePark}</button>
          <button className={styles.successButton} disabled={busy}
            onClick={finishPark}>{t.finishPark}</button>
        </div>
      </div>
    </div>
  );

  return (
    <div className={styles.panel} onMouseDown={stop} onMouseUp={stop}
      onClick={stop} onContextMenu={stop}>
      <div className={styles.rail}>
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

      <div className={styles.content}>
        {activeStage === 0 ? renderOutline()
          : activeStage === 1 ? renderPaths()
            : activeStage === 2 ? renderAssets() : renderComplete()}
      </div>
    </div>
  );
};
