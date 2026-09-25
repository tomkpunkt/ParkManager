import { Texts } from "../i18n";
import { RangeSlider } from "./RangeSlider";
import styles from "../panel.module.less";

type PlacementIcon = "center" | "mirrored" | "axis" | "around" | "boundary";

const PlazaPlacementIcon = ({ kind }: { kind: PlacementIcon }) => (
  <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    {kind === "center" ? <>
      <circle cx="12" cy="12" r="8" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path d="M12 2v4M12 18v4M2 12h4m12 0h4" stroke="currentColor" strokeWidth="1.5" />
      <circle cx="12" cy="12" r="2.5" fill="currentColor" />
    </> : null}
    {kind === "mirrored" ? <>
      <path d="M12 4v16" stroke="currentColor" strokeWidth="1.5" strokeDasharray="2 2" />
      <circle cx="6" cy="12" r="3" fill="currentColor" />
      <circle cx="18" cy="12" r="3" fill="currentColor" />
    </> : null}
    {kind === "axis" ? <>
      <path d="M4 12h16" stroke="currentColor" strokeWidth="1.5" />
      <circle cx="5" cy="12" r="2.5" fill="currentColor" />
      <circle cx="12" cy="12" r="2.5" fill="currentColor" />
      <circle cx="19" cy="12" r="2.5" fill="currentColor" />
    </> : null}
    {kind === "around" ? <>
      <circle cx="12" cy="12" r="7" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <circle cx="12" cy="3.5" r="2" fill="currentColor" />
      <circle cx="20.5" cy="12" r="2" fill="currentColor" />
      <circle cx="12" cy="20.5" r="2" fill="currentColor" />
      <circle cx="3.5" cy="12" r="2" fill="currentColor" />
    </> : null}
    {kind === "boundary" ? <>
      <path d="M5 5h14v14H5z" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <circle cx="5" cy="5" r="2" fill="currentColor" />
      <circle cx="19" cy="5" r="2" fill="currentColor" />
      <circle cx="19" cy="19" r="2" fill="currentColor" />
      <circle cx="5" cy="19" r="2" fill="currentColor" />
    </> : null}
  </svg>
);

type Props = {
  t: Texts;
  busy: boolean;
  pathBuildPresent: boolean;
  centerpieceSelected: string;
  centerPlacement: number;
  arrangementPlacement: number;
  centerpieceSpacing: number;
  arrangementSpacing: number;
  onCenterPlacement: (value: number) => void;
  onArrangementPlacement: (value: number) => void;
  onCenterpieceSpacing: (value: number) => void;
  onArrangementSpacing: (value: number) => void;
};

export const PlazaGeometrySettings = ({ t, busy, pathBuildPresent,
  centerpieceSelected, centerPlacement, arrangementPlacement,
  centerpieceSpacing, arrangementSpacing, onCenterPlacement,
  onArrangementPlacement, onCenterpieceSpacing, onArrangementSpacing }: Props) => (
  <>
    <div className={styles.plazaSettingsDivider}
      data-testid="plaza-center-placement-divider" aria-hidden="true" />
    <div className={styles.compactSetting} data-testid="plaza-center-placement">
      <span>{t.plazaCenterPlacement}</span>
      <div className={`${styles.segmentedControl} ${styles.plazaRuleSegments}`}>
        <button className={centerPlacement === 0 ? styles.segmentActive : ""}
          disabled={busy || pathBuildPresent} aria-pressed={centerPlacement === 0}
          onClick={() => onCenterPlacement(0)}>
          <PlazaPlacementIcon kind="center" /><span>{t.plazaCenterSingle}</span>
        </button>
        <button className={centerPlacement === 1 ? styles.segmentActive : ""}
          disabled={busy || pathBuildPresent} aria-pressed={centerPlacement === 1}
          onClick={() => onCenterPlacement(1)}>
          <PlazaPlacementIcon kind="mirrored" /><span>{t.plazaCenterMirrored}</span>
        </button>
        <button className={centerPlacement === 2 ? styles.segmentActive : ""}
          disabled={busy || pathBuildPresent} aria-pressed={centerPlacement === 2}
          onClick={() => onCenterPlacement(2)}>
          <PlazaPlacementIcon kind="axis" /><span>{t.plazaCenterAxis}</span>
        </button>
      </div>
    </div>
    <div className={styles.densityControl} data-testid="plaza-centerpiece-spacing">
      <span>{t.plazaCenterpieceSpacing}</span>
      <RangeSlider label={t.plazaCenterpieceSpacing} value={centerpieceSpacing}
        minimum={5} maximum={60}
        disabled={busy || pathBuildPresent || centerpieceSelected === "__none__"
          || centerPlacement === 0}
        formatValue={(value) => `${value} m`}
        onChange={onCenterpieceSpacing} />
      <strong>{centerpieceSpacing} m</strong>
    </div>
    <div className={styles.plazaSettingsDivider}
      data-testid="plaza-arrangement-placement-divider" aria-hidden="true" />
    <div className={styles.compactSetting} data-testid="plaza-arrangement-placement">
      <span>{t.plazaArrangementPlacement}</span>
      <div className={`${styles.segmentedControl} ${styles.plazaRuleSegments}`}>
        <button className={arrangementPlacement === 0 ? styles.segmentActive : ""}
          disabled={busy || pathBuildPresent} aria-pressed={arrangementPlacement === 0}
          onClick={() => onArrangementPlacement(0)}>
          <PlazaPlacementIcon kind="around" /><span>{t.plazaAroundCenter}</span>
        </button>
        <button className={arrangementPlacement === 1 ? styles.segmentActive : ""}
          disabled={busy || pathBuildPresent} aria-pressed={arrangementPlacement === 1}
          onClick={() => onArrangementPlacement(1)}>
          <PlazaPlacementIcon kind="boundary" /><span>{t.plazaAlongBoundary}</span>
        </button>
      </div>
    </div>
    <div className={styles.densityControl} data-testid="plaza-arrangement-spacing">
      <span>{arrangementPlacement === 0
        ? t.plazaArrangementCenterSpacing : t.plazaArrangementEdgeSpacing}</span>
      <RangeSlider label={arrangementPlacement === 0
        ? t.plazaArrangementCenterSpacing : t.plazaArrangementEdgeSpacing}
        value={arrangementSpacing} minimum={0} maximum={20}
        disabled={busy || pathBuildPresent}
        formatValue={(value) => `${value} m`}
        onChange={onArrangementSpacing} />
      <strong>{arrangementSpacing} m</strong>
    </div>
    <div className={styles.plazaSettingsDivider}
      data-testid="plaza-fence-divider" aria-hidden="true" />
  </>
);
