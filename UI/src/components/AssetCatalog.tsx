import { useEffect, useRef, useState } from "react";
import { AssetChoiceMap, assetCategories } from "../assetChoices";
import { selectAsset, setVegetationDensity, toggleFence } from "../bindings";
import { Texts } from "../i18n";
import styles from "../panel.module.less";

type Props = {
  t: Texts;
  choices: AssetChoiceMap;
  busy: boolean;
  fenceEnabled: boolean;
  vegetationDensity: number;
  decorationBuildPresent: boolean;
  decorationPlanReady: boolean;
};

/** Stable two-column catalog: category tiles left, scrollable assets right. */
export const AssetCatalog = ({ t, choices, busy, fenceEnabled,
  vegetationDensity, decorationBuildPresent, decorationPlanReady }: Props) => {
  const [openKey, setOpenKey] = useState("tree");
  const [failedIcons, setFailedIcons] = useState<Record<string, boolean>>({});
  const gridRef = useRef<HTMLDivElement | null>(null);
  const category = assetCategories.find((item) => item.key === openKey)
    || assetCategories[0];
  const choice = choices[category.key];

  useEffect(() => {
    if (gridRef.current) gridRef.current.scrollTop = 0;
  }, [openKey]);

  const iconFailed = (icon: string) => setFailedIcons((current) =>
    current[icon] ? current : { ...current, [icon]: true });
  const scroll = (direction: number) => {
    const grid = gridRef.current;
    if (!grid) return;
    const tile = grid.querySelector("button");
    grid.scrollTop += direction * (tile ? tile.getBoundingClientRect().height : 78) * 3;
  };
  const updateDensity = (event: any) => {
    if (busy || decorationBuildPresent) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1,
      (event.clientX - rect.left) / Math.max(1, rect.width)));
    setVegetationDensity(25 + Math.round(ratio * 7) * 25);
  };

  return <>
    <div className={styles.assetStageHeader}>
      <div>
        <span className={styles.eyebrow}>3 / 4 · {t.steps[2]}</span>
        <h2>{t.assetsTitle}</h2>
        <p>{decorationPlanReady ? t.decorationPreview : t.assetsText}</p>
      </div>
      <div className={styles.assetSettings}>
      <div className={styles.densityControl}>
        <span>{t.plantDensity}</span>
        <button type="button" className={styles.densitySlider}
          disabled={busy || decorationBuildPresent} role="slider"
          aria-label={t.plantDensity} aria-valuemin={25} aria-valuemax={200}
          aria-valuenow={vegetationDensity} onMouseDown={updateDensity}
          onMouseMove={(event) => {
            if ((event.buttons & 1) !== 0) updateDensity(event);
          }}>
          <span className={styles.densityTrack}>
            <span className={styles.densityFill}
              style={{ width: `${(vegetationDensity - 25) / 1.75}%` }} />
            <span className={styles.densityThumb}
              style={{ left: `${(vegetationDensity - 25) / 1.75}%` }} />
          </span>
        </button>
        <strong>{vegetationDensity}%</strong>
      </div>
      <button className={`${styles.fenceToggle} ${
        fenceEnabled ? styles.fenceToggleActive : ""}`}
        disabled={busy} aria-pressed={fenceEnabled} onClick={toggleFence}>
        <span className={styles.toggleTrack}><span className={styles.toggleKnob} /></span>
        {fenceEnabled ? t.fenceOn : t.fenceOff}
      </button>
      </div>
    </div>

    <div className={styles.assetWorkspace}>
      <div className={styles.assetGrid}>{assetCategories.map((item) => {
        const itemChoice = choices[item.key];
        const selectedMany = itemChoice?.selectedMany ?? [];
        const selected = itemChoice?.selected || "";
        const option = item.multi
          ? itemChoice?.options.find((candidate) => selectedMany.includes(candidate.name)
              && !failedIcons[candidate.icon])
          : itemChoice?.options.find((candidate) => candidate.name === selected
              && !failedIcons[candidate.icon]);
        const count = item.multi ? selectedMany.length : (selected ? 1 : 0);
        const label = t.categories[item.key];
        return <button key={item.key}
          className={`${styles.assetTile} ${openKey === item.key ? styles.assetTileActive : ""}`}
          disabled={busy || !itemChoice || itemChoice.options.length === 0}
          title={t.openAssets(label)} onClick={() => setOpenKey(item.key)}>
          <span className={styles.assetTileLabel}>{label}</span>
          <span className={styles.assetTileVisual}>
            {option?.icon
              ? <img className={styles.assetIcon} src={option.icon} alt=""
                  onError={() => iconFailed(option.icon)} />
              : <span className={styles.assetFallback}>{label.charAt(0)}</span>}
          </span>
          <span className={styles.assetTileCount}>{count > 0
            ? (item.multi ? t.activeMany(count) : t.activeOne) : t.automatic}</span>
        </button>;
      })}</div>

      <div className={styles.assetChooser}>
        <div className={styles.assetChooserHeader}>
          <strong>{t.categories[category.key]}</strong>
          <span>{choice ? (category.multi ? t.activeMany(choice.selectedMany.length)
            : (choice.selected ? t.activeOne : t.automatic)) : t.chooseCategory}</span>
        </div>
        {choice ? <div className={styles.assetChoiceBody}>
          <div className={styles.assetChoiceGrid} ref={gridRef}>
            {[{ name: "", icon: "" }, ...choice.options.filter(
              (option) => !failedIcons[option.icon])].map((option) => {
              const active = option.name ? (category.multi
                ? choice.selectedMany.includes(option.name)
                : choice.selected === option.name) : (category.multi
                  ? choice.selectedMany.length === 0 : !choice.selected);
              const label = option.name || t.automatic;
              return <button key={option.name || "__automatic"}
                className={`${styles.assetChoiceTile} ${active ? styles.assetChoiceTileActive : ""}`}
                disabled={busy} title={label} aria-label={label} aria-pressed={active}
                onClick={() => selectAsset(category.payload, option.name, category.multi)}>
                {option.icon ? <img className={styles.assetChoiceIcon} src={option.icon}
                    alt="" onError={() => iconFailed(option.icon)} />
                  : <span className={styles.assetChoiceFallback}>✦</span>}
                <span className={styles.assetChoiceName}>{label}</span>
                {active ? <span className={styles.assetChoiceCheck}>✓</span> : null}
              </button>;
            })}
          </div>
          <div className={styles.assetScrollControls}>
            <button title={t.scrollAssetsUp} aria-label={t.scrollAssetsUp}
              onClick={() => scroll(-1)}>▲</button>
            <button title={t.scrollAssetsDown} aria-label={t.scrollAssetsDown}
              onClick={() => scroll(1)}>▼</button>
          </div>
        </div> : <div className={styles.assetChooserEmpty}>{t.chooseCategory}</div>}
      </div>
    </div>
  </>;
};
