import { useState } from "react";
import { AssetChoiceOption } from "../assetChoices";
import { Texts } from "../i18n";
import styles from "../panel.module.less";

type Props = {
  t: Texts;
  options: AssetChoiceOption[];
  selected: string;
  enabled: boolean;
  disabled: boolean;
  onSelect: (name: string | null) => void;
  onTooltip: (event: React.MouseEvent<HTMLButtonElement>, name: string) => void;
  onTooltipClose: () => void;
};

export const PlazaFenceSelector = ({ t, options, selected, enabled, disabled,
  onSelect, onTooltip, onTooltipClose }: Props) => {
  const [failedIcons, setFailedIcons] = useState<Record<string, boolean>>({});

  return <section className={styles.plazaAssetGroup}
    data-testid="plaza-fence-group">
    <div className={styles.plazaAssetGroupHeader}
      data-testid="plaza-fence-group-header">
      <strong>{t.plazaFence}</strong>
    </div>
    <div className={styles.plazaCenterChoices} data-testid="plaza-fence-choices">
      <button type="button" title={t.plazaNoFence}
        className={`${styles.plazaCenterChoice} ${!enabled
          ? styles.plazaCenterChoiceActive : ""}`}
        disabled={disabled} aria-pressed={!enabled}
        aria-label={t.plazaNoFence}
        onClick={() => onSelect(null)}>—</button>
      {options.map((option) => {
        const active = enabled && selected === option.name;
        const showImage = option.icon && !failedIcons[option.icon];
        return <button key={option.name} type="button" aria-label={option.name}
          aria-pressed={active} title={option.name}
          onMouseEnter={(event) => onTooltip(event, option.name)}
          onMouseLeave={onTooltipClose}
          className={`${styles.plazaCenterChoice} ${active
            ? styles.plazaCenterChoiceActive : ""}`}
          disabled={disabled}
          onClick={() => onSelect(option.name)}>
          {showImage ? <img className={styles.plazaCenterChoiceIcon}
            src={option.icon} alt="" onError={() => setFailedIcons((current) => ({
              ...current, [option.icon]: true }))} />
            : <span className={styles.plazaCenterFallback} aria-hidden="true">
              {option.name.charAt(0).toUpperCase()}
            </span>}
        </button>;
      })}
    </div>
  </section>;
};
