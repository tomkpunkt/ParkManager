import { useEffect, useRef, useState } from "react";
import { useValue } from "cs2/api";
import { AssetChoiceMap } from "../assetChoices";
import { editPlazaArrangement, plazaArrangementJson$,
  setFurnitureDensity } from "../bindings";
import { RangeSlider } from "./RangeSlider";
import { Texts } from "../i18n";
import styles from "../panel.module.less";

type Item = { kind: number; name: string };
const kinds = [
  { kind: 0, key: "bench" },
  { kind: 1, key: "lamp" },
  { kind: 2, key: "trashbin" },
  { kind: 3, key: "tree" },
  { kind: 4, key: "plazaplanter" },
] as const;

const parseArrangement = (json: string): Item[] => {
  try {
    const value = JSON.parse(json);
    return Array.isArray(value) ? value.slice(0, 5).filter((item) =>
      Number.isInteger(item?.kind) && item.kind >= 0 && item.kind <= 4
      && typeof item?.name === "string") : [];
  } catch { return []; }
};

type Props = {
  t: Texts;
  choices: AssetChoiceMap;
  busy: boolean;
  density: number;
  decorationPlanReady: boolean;
  summary: string;
};

/** Five ordered slots; every asset choice applies to exactly one slot. */
export const PlazaArrangementEditor = ({ t, choices, busy, density,
  decorationPlanReady, summary }: Props) => {
  const arrangement = parseArrangement(useValue(plazaArrangementJson$));
  const [selected, setSelected] = useState(0);
  const [failedIcons, setFailedIcons] = useState<Record<string, boolean>>({});
  const gridRef = useRef<HTMLDivElement | null>(null);
  const current = arrangement[Math.min(selected, arrangement.length - 1)];
  const selectedIndex = Math.min(selected, arrangement.length - 1);
  const category = kinds.find((kind) => kind.kind === current?.kind) || kinds[0];
  const options = (choices[category.key]?.options || []).filter((option) =>
    !failedIcons[option.icon]);
  const selectedOption = options.find((option) => option.name === current?.name);

  useEffect(() => { if (gridRef.current) gridRef.current.scrollTop = 0; },
    [selectedIndex, current?.kind]);
  const send = (action: string, index: number, value?: string | number) =>
    editPlazaArrangement(`${action}\n${index}${value === undefined ? "" : `\n${value}`}`);
  const scroll = (direction: number) => {
    const grid = gridRef.current;
    if (!grid) return;
    const tile = grid.querySelector("button");
    grid.scrollTop += direction * (tile
      ? tile.getBoundingClientRect().height : 78) * 2;
  };
  return <div className={styles.plazaArrangementStage}>
    <div className={styles.assetStageHeader} data-testid="asset-header">
      <div>
        <p>{decorationPlanReady ? t.plazaDecorationPreview
          : summary.startsWith("Das Arrangement") ? summary
            : t.plazaArrangementHelp}</p>
      </div>
      <div className={`${styles.densityControl} ${styles.plazaArrangementDensity}`}
        data-testid="plaza-density-settings">
        <span>{t.plazaArrangementDensity}</span>
        <RangeSlider label={t.plazaArrangementDensity} value={density}
          minimum={25} maximum={200} step={25} disabled={busy}
          formatValue={(value) => `${value}%`}
          onChange={setFurnitureDensity} />
        <strong>{density}%</strong>
      </div>
    </div>
    <div className={styles.plazaArrangementWorkspace}
      data-testid="plaza-arrangement-workspace">
      <div className={styles.plazaArrangementSlots} data-testid="plaza-arrangement-slots">
        <div className={styles.assetChooserHeader}>
          <strong>{t.plazaArrangement}</strong>
          <span>{arrangement.length} / 5</span>
        </div>
        <div className={styles.plazaArrangementSlotRow}>
          {arrangement.map((item, index) => {
            const kind = kinds.find((candidate) => candidate.kind === item.kind)
              || kinds[0];
            const asset = choices[kind.key]?.options.find((option) =>
              option.name === item.name && !failedIcons[option.icon]);
            return <button key={index} type="button"
              className={`${styles.plazaArrangementSlot} ${index === selectedIndex
                ? styles.plazaArrangementSlotActive : ""}`}
              aria-pressed={index === selectedIndex}
              onClick={() => setSelected(index)}>
              <span>{index + 1}</span>
              {asset ? <img src={asset.icon} alt=""
                onError={() => setFailedIcons((state) => ({ ...state,
                  [asset.icon]: true }))} />
                : <b>{t.categories[kind.key].charAt(0)}</b>}
              <small title={item.name || t.automatic}>
                {item.name || t.categories[kind.key]}</small>
            </button>;
          })}
          {arrangement.length < 5 ? <button type="button"
            className={styles.plazaArrangementAdd} disabled={busy}
            title={t.plazaArrangementAdd}
            onClick={() => {
              editPlazaArrangement("add");
              setSelected(arrangement.length);
            }}>+</button> : null}
        </div>
        <div className={styles.plazaArrangementActions}>
          <button disabled={busy || selectedIndex <= 0}
            onClick={() => { send("move", selectedIndex, selectedIndex - 1);
              setSelected(selectedIndex - 1); }}>←</button>
          <button disabled={busy || selectedIndex >= arrangement.length - 1}
            onClick={() => { send("move", selectedIndex, selectedIndex + 1);
              setSelected(selectedIndex + 1); }}>→</button>
          <button disabled={busy || arrangement.length <= 1}
            onClick={() => { send("remove", selectedIndex);
              setSelected(Math.max(0, selectedIndex - 1)); }}>
            {t.plazaArrangementRemove}</button>
        </div>
      </div>
      <div className={styles.plazaArrangementPicker} data-testid="plaza-arrangement-picker">
        <div className={styles.plazaArrangementKinds}>
          {kinds.map((kind) => <button key={kind.kind} type="button"
            className={kind.kind === current?.kind ? styles.segmentActive : ""}
            disabled={busy || !current}
            onClick={() => send("kind", selectedIndex, kind.kind)}>
            {t.categories[kind.key]}</button>)}
        </div>
        <div className={styles.assetChoiceBody}>
          <div className={styles.assetChoiceGrid} ref={gridRef}>
            {options.map((option) => <button key={option.name}
              className={`${styles.assetChoiceTile} ${option.name === current?.name
                ? styles.assetChoiceTileActive : ""}`}
              disabled={busy} title={option.name} aria-label={option.name}
              aria-pressed={option.name === current?.name}
              onClick={() => send("asset", selectedIndex, option.name)}>
              <img className={styles.assetChoiceIcon} src={option.icon} alt=""
                onError={() => setFailedIcons((state) => ({ ...state,
                  [option.icon]: true }))} />
              <span className={styles.assetChoiceName}>{option.name}</span>
              {option.name === current?.name
                ? <span className={styles.assetChoiceCheck}>✓</span> : null}
            </button>)}
          </div>
          <div className={styles.assetScrollControls}>
            <button title={t.scrollAssetsUp} onClick={() => scroll(-1)}>▲</button>
            <button title={t.scrollAssetsDown} onClick={() => scroll(1)}>▼</button>
          </div>
        </div>
        <div className={styles.plazaArrangementSelection}>
          {selectedOption?.name || t.plazaArrangementChooseAsset}
        </div>
      </div>
    </div>
  </div>;
};
