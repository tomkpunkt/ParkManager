import { ReactNode } from "react";
import { useValue } from "cs2/api";
import { selectedSnapMask$, setSelectedSnapMask, snapMask$ } from "../bindings";
import { Texts } from "../i18n";
import styles from "../panel.module.less";

const stop = (event: any) => event.stopPropagation();

const snapOptions = [
  { bit: 1, name: "ExistingGeometry" },
  { bit: 4, name: "StraightDirection" },
  { bit: 8, name: "NetSide" },
  { bit: 0x40, name: "ObjectSide" },
  { bit: 0x400, name: "GuideLines" },
  { bit: 0x800, name: "ZoneGrid" },
];

export const SnapControls = ({ t }: { t: Texts }) => {
  const available = useValue(snapMask$);
  const selected = useValue(selectedSnapMask$);
  const shown = snapOptions.filter((option) => (available & option.bit) !== 0);
  const allBits = shown.reduce((value, option) => value | option.bit, 0);
  const allSelected = shown.length > 0 && (selected & allBits) === allBits;

  return (
    <div className={styles.snapRow}>
      <span className={styles.snapLabel}>{t.snap}</span>
      <button className={`${styles.iconButton} ${allSelected ? styles.iconButtonActive : ""}`}
        title={allSelected ? t.snapAllOff : t.snapAllOn} aria-pressed={allSelected}
        onMouseDown={stop} onClick={() => setSelectedSnapMask(
          allSelected ? selected & ~allBits : selected | allBits)}>
        <img alt="" src="Media/Tools/Snap Options/All.svg" />
      </button>
      {shown.map((option) => {
        const active = (selected & option.bit) !== 0;
        const label = t.snapNames[option.name] || option.name;
        return (
          <button key={option.name}
            className={`${styles.iconButton} ${active ? styles.iconButtonActive : ""}`}
            title={label} aria-label={label} aria-pressed={active}
            onMouseDown={stop} onClick={() => setSelectedSnapMask(
              active ? selected & ~option.bit : selected | option.bit)}>
            <img alt="" src={`Media/Tools/Snap Options/${option.name}.svg`} />
          </button>
        );
      })}
    </div>
  );
};

export const StatePill = ({ children, success = false }: {
  children: ReactNode;
  success?: boolean;
}) => (
  <span className={`${styles.statePill} ${success ? styles.statePillSuccess : ""}`}>
    {success ? <span className={styles.stateCheck}>✓</span> : null}{children}
  </span>
);
