import { Texts } from "../i18n";
import type { ReactNode } from "react";
import { PlazaGeometrySettings } from "./PlazaGeometrySettings";
import { SiteTypeSelector } from "./SiteTypeSelector";
import styles from "../panel.module.less";

type Props = {
  t: Texts;
  isPlaza: boolean;
  busy: boolean;
  pathBuildPresent: boolean;
  siteType: number;
  pathType: number;
  centerSelected: string;
  centerPlacement: number;
  arrangementPlacement: number;
  centerpieceSpacing: number;
  arrangementSpacing: number;
  surfaceSelector?: ReactNode;
  onSiteType: (value: number) => void;
  onPathType: (value: number) => void;
  onCenterPlacement: (value: number) => void;
  onArrangementPlacement: (value: number) => void;
  onCenterpieceSpacing: (value: number) => void;
  onArrangementSpacing: (value: number) => void;
};

/** Right-side controls for path and plaza structure settings. */
export const PathSettings = ({ t, isPlaza, busy, pathBuildPresent, siteType,
  pathType, centerSelected, centerPlacement, arrangementPlacement,
  centerpieceSpacing, arrangementSpacing, surfaceSelector,
  onSiteType, onPathType, onCenterPlacement,
  onArrangementPlacement, onCenterpieceSpacing, onArrangementSpacing,
}: Props) => {
  return <div className={`${styles.pathSettings} ${isPlaza
    ? styles.plazaPathSettings : ""}`} data-testid="path-settings">
    <SiteTypeSelector t={t} siteType={siteType} disabled={busy || pathBuildPresent}
      onChange={onSiteType} />
    {isPlaza ? <>
      <PlazaGeometrySettings t={t} busy={busy} pathBuildPresent={pathBuildPresent}
        centerpieceSelected={centerSelected} centerPlacement={centerPlacement}
        arrangementPlacement={arrangementPlacement}
        centerpieceSpacing={centerpieceSpacing}
        arrangementSpacing={arrangementSpacing}
        onCenterPlacement={onCenterPlacement}
        onArrangementPlacement={onArrangementPlacement}
        onCenterpieceSpacing={onCenterpieceSpacing}
        onArrangementSpacing={onArrangementSpacing} />
      {surfaceSelector}
    </> : <>
      <div className={styles.compactSetting} data-testid="path-width-selector">
        <span>{t.pathType}</span>
        <div className={styles.segmentedControl}>
          <button className={pathType === 0 ? styles.segmentActive : ""}
            disabled={busy || pathBuildPresent} aria-pressed={pathType === 0}
            onClick={() => onPathType(0)}>{t.pathNarrow}</button>
          <button className={pathType === 1 ? styles.segmentActive : ""}
            disabled={busy || pathBuildPresent} aria-pressed={pathType === 1}
            onClick={() => onPathType(1)}>{t.pathWide}</button>
        </div>
      </div>
      <div className={styles.settingsDivider} data-testid="path-surface-divider"
        role="separator" aria-orientation="horizontal" />
      {surfaceSelector}
    </>}
  </div>;
};
