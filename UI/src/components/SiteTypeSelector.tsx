import { Texts } from "../i18n";
import styles from "../panel.module.less";

type Props = {
  t: Texts;
  siteType: number;
  disabled: boolean;
  onChange: (siteType: number) => void;
};

export const SiteTypeSelector = ({ t, siteType, disabled, onChange }: Props) => (
  <div className={styles.compactSetting} data-testid="site-type-selector">
    <span>{t.siteType}</span>
    <div className={styles.segmentedControl}>
      <button className={siteType === 0 ? styles.segmentActive : ""}
        disabled={disabled} aria-pressed={siteType === 0}
        onClick={() => onChange(0)}>{t.sitePark}</button>
      <button className={siteType === 1 ? styles.segmentActive : ""}
        disabled={disabled} aria-pressed={siteType === 1}
        onClick={() => onChange(1)}>{t.sitePlaza}</button>
    </div>
  </div>
);
