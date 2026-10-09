import { useValue } from "cs2/api";
import { selectedSnapMask$, setSelectedSnapMask, snapMask$ } from "../bindings";
import { Texts } from "../i18n";
import { Hint } from "./Hint";
import styles from "../panel.module.less";

const snapOptions = [
  { bit: 1, name: "ExistingGeometry" },
  { bit: 4, name: "StraightDirection" },
  { bit: 8, name: "NetSide" },
  { bit: 0x40, name: "ObjectSide" },
  { bit: 0x400, name: "GuideLines" },
  { bit: 0x800, name: "ZoneGrid" },
] as const;

/** The game's own snapping switches, shown while the outline is edited. */
export const SnapControls = ({ t }: { t: Texts }) => {
  const available = useValue(snapMask$);
  const selected = useValue(selectedSnapMask$);
  const shown = snapOptions.filter((option) => (available & option.bit) !== 0);
  const allBits = shown.reduce((value, option) => value | option.bit, 0);
  const allSelected = shown.length > 0 && (selected & allBits) === allBits;
  const button = (key: string, label: string, active: boolean, icon: string,
    onClick: () => void) => <Hint key={key} text={label}>
      <button type="button" aria-label={label} aria-pressed={active}
        className={`${styles.iconButton} ${active ? styles.iconButtonActive : ""}`}
        onClick={onClick}>
        <img alt="" src={`Media/Tools/Snap Options/${icon}.svg`} />
      </button>
    </Hint>;

  return <div className={styles.snapRow} data-testid="snap-controls">
    {button("all", allSelected ? t.snapAllOff : t.snapAllOn, allSelected, "All",
      () => setSelectedSnapMask(allSelected ? selected & ~allBits : selected | allBits))}
    {shown.map((option) => {
      const active = (selected & option.bit) !== 0;
      return button(option.name, t.snapNames[option.name] || option.name, active,
        option.name, () => setSelectedSnapMask(
          active ? selected & ~option.bit : selected | option.bit));
    })}
  </div>;
};
