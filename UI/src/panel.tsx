import { useEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import {
  assetOptionsJson$, buildDecorations, buildPaths, clearPolygon,
  decorationBuildBusy$, decorationBuildPresent$, decorationEnabledMask$,
  decorationPlanReady$, decorationSummary$,
  entranceCount$, finishPark, generateDecorations, generatePaths,
  furnitureDensity$, locale$, panelOpen$, pathBuildBusy$,
  pathBuildPresent$, pathBuildStatus$, pathBuildSummary$, pathPlanReady$,
  pathType$, plannerMode$, pointCount$,
  plazaArrangementPlacement$, plazaArrangementSpacing$,
  plazaCenterOptionsJson$, plazaCenterSelected$, plazaCenterPlacement$,
  plazaCenterpieceSpacing$, plazaFenceEnabled$, selectPlazaCenter,
  setPlazaArrangementPlacement, setPlazaArrangementSpacing,
  setPlazaCenterPlacement, setPlazaCenterpieceSpacing, setPlazaFenceEnabled,
  polygonArea$, polygonClosed$, polygonValid$, removeBuiltDecorations, removeBuiltPaths,
  selectAsset, setPathType, setSiteType, setPlannerMode, siteType$, toggleTool,
  vegetationDensity$,
} from "./bindings";
import { parseAssetChoices } from "./assetChoices";
import { AssetCatalog } from "./components/AssetCatalog";
import { PathSettings } from "./components/PathSettings";
import { PlazaFenceSelector } from "./components/PlazaFenceSelector";
import { PlazaArrangementEditor } from "./components/PlazaArrangementEditor";
import { SnapControls, StatePill } from "./components/WorkflowParts";
import { getTexts } from "./i18n";
import arrowRightIcon from "./assets/arrow-right.svg";
import brandLogo from "./assets/park-manager.svg";
import styles from "./panel.module.less";
import { deriveWorkflowModel } from "./workflow";

const stop = (event: any) => event.stopPropagation();

type PlazaCenterOption = { name: string; icon: string };

const parsePlazaCenterOptions = (json: string): PlazaCenterOption[] => {
  try {
    const parsed = JSON.parse(json);
    const options = Array.isArray(parsed) ? parsed
      : Array.isArray(parsed?.options) ? parsed.options : [];
    return options.map((option: unknown) => typeof option === "string"
      ? { name: option, icon: "" }
      : {
          name: typeof (option as any)?.name === "string"
            ? (option as any).name : "",
          icon: typeof (option as any)?.icon === "string"
            ? (option as any).icon : "",
        }).filter((option: PlazaCenterOption) => option.name.trim().length > 0);
  } catch {
    return [];
  }
};

/**
 * Four-step workflow shell. Detailed snapping and asset-selection concerns
 * live in dedicated components so this file only coordinates workflow state.
 */
export const ParkManagerPanel = () => {
  // Hooks stay unconditional: conditional hooks caused React #310 in Cohtml.
  const [activeStage, setActiveStage] = useState(0);
  const [failedSurfaceIcons, setFailedSurfaceIcons] = useState<Record<string, boolean>>({});
  const [failedCenterIcons, setFailedCenterIcons] = useState<Record<string, boolean>>({});
  const [assetTooltip, setAssetTooltip] = useState<{
    name: string; x: number; y: number;
  } | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);
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
  const pathBuildSummary = useValue(pathBuildSummary$);
  const pathBuildStatus = useValue(pathBuildStatus$);
  const pathType = useValue(pathType$);
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
  const decorationSummary = useValue(decorationSummary$);
  const decorationBuildBusy = useValue(decorationBuildBusy$);
  const decorationBuildPresent = useValue(decorationBuildPresent$);
  const assetChoices = parseAssetChoices(useValue(assetOptionsJson$));
  const surfaceChoice = assetChoices.surface;
  const visibleSurfaces = surfaceChoice?.options ?? [];
  const fenceChoice = assetChoices.fence;

  const busy = pathBuildBusy || decorationBuildBusy;
  const workflow = deriveWorkflowModel({ plannerMode, polygonValid: valid,
    pathsBuilt: pathBuildPresent, decorationsBuilt: decorationBuildPresent });
  const outlineState = pointCount === 0 ? t.outlineEmpty
    : !closed ? t.outlineOpen(pointCount)
      : !valid ? t.outlineInvalid(pointCount) : t.outlineReady(pointCount);
  const pathBuildNotice = pathBuildStatus !== "ok";
  const isPlaza = siteType === 1;
  const workflowSteps = isPlaza ? t.plazaSteps : t.steps;
  const canPlanPlaza = plazaCenterSelected === "__none__"
    || plazaCenterOptions.some((option) => option.name === plazaCenterSelected);

  const selectPlazaFence = (name: string | null) => {
    if (name === null) {
      setPlazaFenceEnabled(false);
      return;
    }
    selectAsset("Fence", name);
    setPlazaFenceEnabled(true);
  };

  const showAssetTooltip = (event: any, name: string) => {
    const rect = panelRef.current?.getBoundingClientRect();
    if (!rect) return;
    setAssetTooltip({ name,
      x: Math.max(8, Math.min(event.clientX - rect.left + 10,
        rect.width - 220)),
      y: Math.max(62, event.clientY - rect.top - 39) });
  };

  const renderSurfaceSelector = (groupTestId: string, choicesTestId: string) => (
    <section className={`${styles.plazaAssetGroup} ${styles.plazaSurfaceGroup}`}
      data-testid={groupTestId}>
      <div className={styles.plazaAssetGroupHeader}
        data-testid={`${groupTestId}-header`}>
        <strong>{t.background}</strong>
      </div>
      <div className={styles.surfaceChoices} data-testid={choicesTestId}>
        {visibleSurfaces.map((option) => (
          <button key={option.name} type="button" aria-label={option.name}
            aria-pressed={surfaceChoice?.selected === option.name}
            onMouseEnter={(event) => showAssetTooltip(event, option.name)}
            onMouseLeave={() => setAssetTooltip(null)}
            className={surfaceChoice?.selected === option.name
              ? styles.surfaceChoiceActive : ""}
            disabled={busy || pathBuildPresent}
            onClick={() => selectAsset("Surface", option.name)}>
            {option.icon && !failedSurfaceIcons[option.icon]
              ? <img src={option.icon} alt=""
                  onError={() => setFailedSurfaceIcons((current) =>
                    current[option.icon] ? current
                      : { ...current, [option.icon]: true })} />
              : <span className={styles.plazaCenterFallback} aria-hidden="true">
                  {option.name.charAt(0).toUpperCase()}
                </span>}
          </button>
        ))}
      </div>
    </section>
  );

  const renderPlazaAssetSelectors = () => (
    <div className={styles.plazaAssetSelectors} data-testid="plaza-asset-selectors">
      <section className={`${styles.plazaAssetGroup} ${styles.plazaCenterGroup}`}
        data-testid="plaza-center-group">
        <div className={styles.plazaAssetGroupHeader}
          data-testid="plaza-center-header">
          <strong>{t.plazaCenter}</strong>
        </div>
        <div className={styles.plazaCenterChoices} data-testid="plaza-center-choices">
          <button type="button" title={t.plazaNoCenter}
            className={`${styles.plazaCenterChoice} ${plazaCenterSelected === "__none__"
              ? styles.plazaCenterChoiceActive : ""}`}
            disabled={busy || pathBuildPresent}
            aria-pressed={plazaCenterSelected === "__none__"}
            aria-label={t.plazaNoCenter}
            onClick={() => selectPlazaCenter("__none__")}>—</button>
          {plazaCenterOptions.map((option) => {
            const selected = option.name === plazaCenterSelected;
            return <button key={option.name} type="button"
              className={`${styles.plazaCenterChoice} ${selected
                ? styles.plazaCenterChoiceActive : ""}`}
              disabled={busy || pathBuildPresent}
              aria-pressed={selected} aria-label={option.name}
              onMouseEnter={(event) => showAssetTooltip(event, option.name)}
              onMouseLeave={() => setAssetTooltip(null)}
              onClick={() => selectPlazaCenter(option.name)}>
              {option.icon && !failedCenterIcons[option.icon]
                ? <img src={option.icon} alt="" onError={() =>
                  setFailedCenterIcons((current) => ({
                    ...current, [option.icon]: true }))} />
                : <span className={styles.plazaCenterFallback} aria-hidden="true">
                  {option.name.charAt(0).toUpperCase()}
                </span>}
            </button>;
          })}
        </div>
      </section>
      <PlazaFenceSelector t={t} options={fenceChoice?.options ?? []}
        selected={fenceChoice?.selected ?? ""} enabled={plazaFenceEnabled}
        disabled={busy || pathBuildPresent} onSelect={selectPlazaFence}
        onTooltip={showAssetTooltip}
        onTooltipClose={() => setAssetTooltip(null)} />
    </div>
  );

  useEffect(() => {
    if (open) setActiveStage(workflow.progressStage);
  }, [open, workflow.progressStage]);

  useEffect(() => setAssetTooltip(null), [open, activeStage]);

  if (!open) return null;

  const openStage = (index: number) => {
    if (busy || !workflow.stageAvailability[index as 0 | 1 | 2 | 3]) return;
    setActiveStage(index);
    setPlannerMode(index > 0);
  };

  const renderOutline = () => (
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
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
        <div className={styles.stageCopy} data-testid="path-stage-copy">
          <div className={styles.pathStageIntro} data-testid="path-stage-intro">
            <p>{pathPlanReady ? (isPlaza ? t.plazaPathsPreview : t.pathsPreview)
              : isPlaza ? (entranceCount > 0 ? t.plazaPathsNeedPlan : t.plazaPathsNoGate)
                : entranceCount > 0 ? t.pathsGates(entranceCount) : t.pathsNoGate}</p>
            {pathBuildNotice ? <p className={styles.buildWarning} role="alert"
              data-status={pathBuildStatus}>
              {pathBuildSummary}
            </p> : null}
            <div className={styles.stateRow} data-testid="path-stage-state">
              <StatePill success={entranceCount > 0}>{isPlaza
                ? t.plazaAccesses(entranceCount) : t.pathsGates(entranceCount)}</StatePill>
              {pathPlanReady ? <StatePill success>{workflowSteps[1]}</StatePill> : null}
            </div>
          </div>
          {isPlaza ? renderPlazaAssetSelectors() : null}
        </div>
        <PathSettings t={t} isPlaza={isPlaza} busy={busy}
          pathBuildPresent={pathBuildPresent} siteType={siteType} pathType={pathType}
          centerSelected={plazaCenterSelected}
          centerPlacement={plazaCenterPlacement}
          arrangementPlacement={plazaArrangementPlacement}
          centerpieceSpacing={plazaCenterpieceSpacing}
          arrangementSpacing={plazaArrangementSpacing}
          surfaceSelector={renderSurfaceSelector(
            isPlaza ? "plaza-surface-group" : "park-surface-group",
            isPlaza ? "plaza-surface-choices" : "park-surface-choices")}
          onSiteType={setSiteType} onPathType={setPathType}
          onCenterPlacement={setPlazaCenterPlacement}
          onArrangementPlacement={setPlazaArrangementPlacement}
          onCenterpieceSpacing={setPlazaCenterpieceSpacing}
          onArrangementSpacing={setPlazaArrangementSpacing}
        />
      </div>
  );

  const renderAssets = () => (
    <div className={styles.assetStage}>
      {isPlaza ? <PlazaArrangementEditor t={t} choices={assetChoices}
        busy={busy || decorationBuildPresent}
        density={furnitureDensity} decorationPlanReady={decorationPlanReady}
        summary={decorationSummary} />
        : <AssetCatalog t={t} choices={assetChoices}
        isPlaza={false}
        busy={busy || decorationBuildPresent}
        vegetationDensity={vegetationDensity}
        furnitureDensity={furnitureDensity}
        enabledMask={decorationEnabledMask}
        decorationBuildPresent={decorationBuildPresent}
        decorationPlanReady={decorationPlanReady} />}
    </div>
  );

  const renderComplete = () => (
      <div className={styles.stageColumn}>
        <div className={styles.stageCopy}>
          <p>{isPlaza ? t.plazaCompleteText : t.completeText}</p>
          <div className={styles.stateRow}>
            <StatePill success>{isPlaza ? t.plazaSurfaceBuilt : t.pathsBuilt}</StatePill>
            <StatePill success>{t.decorationsBuilt}</StatePill>
          </div>
        </div>
      </div>
  );

  const renderFooter = () => (
    <div className={styles.panelFooter} data-testid="panel-footer">
      <div className={styles.footerLeft}>
        {activeStage === 1 ? <button className={styles.backButton}
          disabled={busy || pathBuildPresent}
          onClick={() => openStage(0)}>
          <img className={`${styles.buttonIcon} ${styles.backButtonIcon}`}
            src={arrowRightIcon} alt="" />{t.editOutline}</button> : null}
        {activeStage === 2 ? <button className={styles.backButton}
          disabled={busy} onClick={removeBuiltPaths}>
          <img className={`${styles.buttonIcon} ${styles.backButtonIcon}`}
            src={arrowRightIcon} alt="" />{t.removeSurface}</button> : null}
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
            disabled={busy || pathBuildPresent || entranceCount === 0 || !pathPlanReady
              || (isPlaza && !canPlanPlaza)}
            onClick={generatePaths}>
            {isPlaza ? t.plazaRecalculate : t.recalculatePaths}</button>
          <button className={styles.primaryButton}
            disabled={busy || pathBuildPresent || entranceCount === 0
              || (isPlaza && !canPlanPlaza)}
            onClick={pathPlanReady ? buildPaths : generatePaths}>
            {pathBuildBusy ? t.busy : pathPlanReady
              ? (isPlaza ? t.plazaBuild : t.buildPaths)
              : (isPlaza ? t.plazaGenerate : t.generatePaths)}
            <img className={styles.buttonIcon} src={arrowRightIcon} alt="" />
          </button>
        </> : null}
        {activeStage === 2 ? <>
          <button className={styles.secondaryButton}
            disabled={busy || decorationBuildPresent || !decorationPlanReady}
            onClick={generateDecorations}>
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
            onClick={removeBuiltPaths}>{isPlaza ? t.removePlaza : t.removePark}</button>
          <button className={styles.successButton} disabled={busy}
            onClick={finishPark}>{isPlaza ? t.finishPlaza : t.finishPark}</button>
        </> : null}
      </div>
    </div>
  );

  return (
    <div ref={panelRef} className={styles.panel} data-testid="park-panel"
      onMouseDown={stop} onMouseUp={stop}
      onClick={stop} onContextMenu={stop}>
      <div className={styles.panelHeader} data-testid="panel-header">
        <div className={styles.brand}>
          <span className={styles.brandMark}>
            <img src={brandLogo} alt="" data-testid="brand-logo" />
          </span><span>ParkManager</span>
        </div>
        <div className={styles.progress} data-testid="workflow-progress" role="list">
          {workflowSteps.map((label, index) => (
            <div key={label} role="listitem" data-testid="workflow-step"
              className={`${styles.progressStep} ${
                index === activeStage ? styles.progressStepActive : ""} ${
                index < workflow.progressStage ? styles.progressStepDone : ""}`}
              aria-current={index === activeStage ? "step" : undefined}>
              <span className={styles.progressNumber}>
                {index < workflow.progressStage ? "✓" : index + 1}
              </span>
              <span>{label}</span>
            </div>
          ))}
        </div>
        <div className={styles.toolSlot}>
          {activeStage === 0 ? <SnapControls t={t} /> : null}
        </div>
        <button className={styles.closeButton} title={t.close}
          onClick={toggleTool}>×</button>
      </div>

      <div data-testid="panel-body" data-stage={activeStage}
        className={`${styles.panelBody} ${
        activeStage <= 1 || activeStage === 3 ? styles.compactBody : ""} ${
        activeStage === 1 && isPlaza ? styles.plazaBody : ""} ${
        activeStage === 2 ? styles.assetBody : ""}`}>
        {activeStage === 0 ? renderOutline()
          : activeStage === 1 ? renderPaths()
            : activeStage === 2 ? renderAssets() : renderComplete()}
      </div>
      {renderFooter()}
      {assetTooltip ? <div className={styles.assetTooltip}
        style={{ left: `${assetTooltip.x}px`, top: `${assetTooltip.y}px` }}>
        {assetTooltip.name}
      </div> : null}
    </div>
  );
};
