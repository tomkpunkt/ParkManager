import { useEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import {
  assetOptionsJson$, buildDecorations, buildPaths, clearPolygon,
  decorationBuildBusy$, decorationBuildPresent$, decorationEnabledMask$,
  decorationPlanReady$, decorationSummary$,
  entranceCount$, finishPark, generateDecorations, generatePaths,
  furnitureDensity$, locale$, panelOpen$, pathBuildBusy$,
  pathBuildPresent$, pathBuildSummary$, pathPlanReady$, pathType$, plannerMode$, pointCount$,
  plazaCenterOptionsJson$, plazaCenterSelected$, plazaLayout$, selectPlazaCenter,
  setPlazaLayout,
  polygonArea$, polygonClosed$, polygonValid$, removeBuiltDecorations, removeBuiltPaths,
  selectAsset, setPathType, setSiteType, siteType$, togglePlannerMode, toggleTool,
  vegetationDensity$,
} from "./bindings";
import { parseAssetChoices } from "./assetChoices";
import { AssetCatalog } from "./components/AssetCatalog";
import { PlazaArrangementEditor } from "./components/PlazaArrangementEditor";
import { SnapControls, StatePill } from "./components/WorkflowParts";
import { getTexts } from "./i18n";
import arrowLeftIcon from "./assets/arrow-left.svg";
import arrowRightIcon from "./assets/arrow-right.svg";
import refreshIcon from "./assets/refresh.svg";
import styles from "./panel.module.less";

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
        }).filter((option: PlazaCenterOption) => option.name.trim().length > 0
          && option.icon.trim().length > 0);
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
  const pathType = useValue(pathType$);
  const siteType = useValue(siteType$);
  const plazaLayout = useValue(plazaLayout$);
  const plazaCenterOptions = parsePlazaCenterOptions(
    useValue(plazaCenterOptionsJson$)).filter((option) =>
      !failedCenterIcons[option.icon]);
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
  const visibleSurfaces = (surfaceChoice?.options ?? []).filter((option) =>
    !failedSurfaceIcons[option.icon]);

  const busy = pathBuildBusy || decorationBuildBusy;
  const workflowStage = decorationBuildPresent ? 3
    : pathBuildPresent ? 2 : plannerMode ? 1 : 0;
  const outlineState = pointCount === 0 ? t.outlineEmpty
    : !closed ? t.outlineOpen(pointCount)
      : !valid ? t.outlineInvalid(pointCount) : t.outlineReady(pointCount);
  const pathBuildError = pathBuildSummary.startsWith("Bauprüfung:")
    || pathBuildSummary.startsWith("Hinweis:")
    || pathBuildSummary.startsWith("Fehler:");
  const isPlaza = siteType === 1;
  const workflowSteps = isPlaza ? t.plazaSteps : t.steps;
  const canPlanPlaza = plazaLayout === 3 || plazaCenterSelected === "__none__"
    || plazaCenterOptions.some((option) =>
      option.name === plazaCenterSelected);

  const showAssetTooltip = (event: any, name: string) => {
    const rect = panelRef.current?.getBoundingClientRect();
    if (!rect) return;
    setAssetTooltip({ name,
      x: Math.max(8, Math.min(event.clientX - rect.left + 10,
        rect.width - 220)),
      y: Math.max(62, event.clientY - rect.top - 39) });
  };

  useEffect(() => {
    if (open) setActiveStage(workflowStage);
  }, [open, workflowStage]);

  useEffect(() => setAssetTooltip(null), [open, activeStage]);

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
          <span className={styles.eyebrow}>1 / 4 · {workflowSteps[0]}</span>
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
          <span className={styles.eyebrow}>2 / 4 · {workflowSteps[1]}</span>
          <h2>{isPlaza ? t.plazaPathsTitle : t.pathsTitle}</h2>
          <p>{pathPlanReady ? (isPlaza ? t.plazaPathsPreview : t.pathsPreview)
            : isPlaza ? (entranceCount > 0 ? t.plazaPathsNeedPlan : t.plazaPathsNoGate)
              : entranceCount > 0 ? t.pathsGates(entranceCount) : t.pathsNoGate}</p>
          {pathBuildError ? <p className={styles.buildWarning} role="alert">
            {pathBuildSummary}
          </p> : null}
          <div className={styles.stateRow}>
            <StatePill success={entranceCount > 0}>{isPlaza
              ? t.plazaAccesses(entranceCount) : t.pathsGates(entranceCount)}</StatePill>
            {pathPlanReady ? <StatePill success>{workflowSteps[1]}</StatePill> : null}
          </div>
        </div>
        <div className={styles.pathSettings} data-testid="path-settings">
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
          {isPlaza ? <>
            <div className={styles.compactSetting}>
              <span>{t.plazaLayout}</span>
              <div className={styles.segmentedControl}>
                <button className={plazaLayout === 0 ? styles.segmentActive : ""}
                  disabled={busy || pathBuildPresent}
                  aria-pressed={plazaLayout === 0}
                  onClick={() => setPlazaLayout(0)}>{t.plazaAxial}</button>
                <button className={plazaLayout === 1 ? styles.segmentActive : ""}
                  disabled={busy || pathBuildPresent}
                  aria-pressed={plazaLayout === 1}
                  onClick={() => setPlazaLayout(1)}>{t.plazaRadial}</button>
                <button className={plazaLayout === 2 ? styles.segmentActive : ""}
                  disabled={busy || pathBuildPresent}
                  aria-pressed={plazaLayout === 2}
                  onClick={() => setPlazaLayout(2)}>{t.plazaBoundary}</button>
                <button className={plazaLayout === 3 ? styles.segmentActive : ""}
                  disabled={busy || pathBuildPresent}
                  aria-pressed={plazaLayout === 3}
                  onClick={() => setPlazaLayout(3)}>{t.plazaOpen}</button>
              </div>
            </div>
            <div className={`${styles.compactSetting} ${styles.plazaCenterSetting}`}
              title={t.plazaCenterHint}>
              <span>{t.plazaCenter}</span>
              <div className={styles.plazaCenterChoices} data-testid="plaza-center-choices">
                <button type="button" title={t.plazaNoCenter}
                  className={`${styles.plazaCenterChoice} ${plazaCenterSelected === "__none__"
                    || plazaLayout === 3 ? styles.plazaCenterChoiceActive : ""}`}
                  disabled={busy || pathBuildPresent || plazaLayout === 3}
                  aria-pressed={plazaCenterSelected === "__none__" || plazaLayout === 3}
                  aria-label={t.plazaNoCenter}
                  onClick={() => selectPlazaCenter("__none__")}>—</button>
                {plazaCenterOptions.map((option) => {
                  const selected = option.name === plazaCenterSelected;
                  return <button key={option.name} type="button"
                    className={`${styles.plazaCenterChoice} ${selected
                      ? styles.plazaCenterChoiceActive : ""}`}
                    disabled={busy || pathBuildPresent || plazaLayout === 3}
                    aria-pressed={selected} aria-label={option.name}
                    onMouseEnter={(event) => showAssetTooltip(event, option.name)}
                    onMouseLeave={() => setAssetTooltip(null)}
                    onClick={() => selectPlazaCenter(option.name)}>
                    <img src={option.icon} alt="" onError={() =>
                      setFailedCenterIcons((current) => ({
                        ...current, [option.icon]: true }))} />
                  </button>;
                })}
              </div>
            </div>
          </> : <div className={styles.compactSetting}>
            <span>{t.pathType}</span>
            <div className={styles.segmentedControl}>
              <button className={pathType === 0 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setPathType(0)}>{t.pathNarrow}</button>
              <button className={pathType === 1 ? styles.segmentActive : ""}
                disabled={busy || pathBuildPresent}
                onClick={() => setPathType(1)}>{t.pathWide}</button>
            </div>
          </div>}
          <div className={styles.compactSetting}>
            <span>{t.background}</span>
            <div className={styles.surfaceChoices}>
              {visibleSurfaces.map((option) => (
                <button key={option.name} aria-label={option.name}
                  onMouseEnter={(event) => showAssetTooltip(event, option.name)}
                  onMouseLeave={() => setAssetTooltip(null)}
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
          <span className={styles.eyebrow}>4 / 4 · {workflowSteps[3]}</span>
          <h2>{isPlaza ? t.plazaCompleteTitle : t.completeTitle}</h2>
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
          <img className={styles.buttonIcon} src={arrowLeftIcon} alt="" />
          {t.editOutline}</button> : null}
        {activeStage === 2 ? <button className={styles.dangerButton}
          disabled={busy} onClick={removeBuiltPaths}>{isPlaza ? t.removePlaza : t.removePark}</button> : null}
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
            <img className={styles.buttonIcon} src={refreshIcon} alt="" />
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
          <span className={styles.brandMark}>P</span><span>ParkManager</span>
        </div>
        <div className={styles.progress}>
          {workflowSteps.map((label, index) => (
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
