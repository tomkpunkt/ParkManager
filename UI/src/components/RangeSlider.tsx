import { useRef } from "react";
import styles from "../panel.module.less";

type Props = {
  label: string;
  value: number;
  minimum: number;
  maximum: number;
  step?: number;
  disabled?: boolean;
  className?: string;
  testId?: string;
  formatValue?: (value: number) => string;
  onChange: (value: number) => void;
};

/** Accessible range control shared by park and plaza density/spacing settings. */
export const RangeSlider = ({ label, value, minimum, maximum, step = 1,
  disabled = false, className = styles.densitySlider, testId,
  formatValue = (next) => String(next), onChange }: Props) => {
  const activePointer = useRef<number | null>(null);
  const clamp = (next: number) => Math.max(minimum,
    Math.min(maximum, minimum + Math.round((next - minimum) / step) * step));
  const updateFromPointer = (event: React.PointerEvent<HTMLButtonElement>) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1,
      (event.clientX - rect.left) / Math.max(1, rect.width)));
    onChange(clamp(minimum + ratio * (maximum - minimum)));
  };
  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    let next: number | null = null;
    switch (event.key) {
      case "ArrowLeft": case "ArrowDown": next = value - step; break;
      case "ArrowRight": case "ArrowUp": next = value + step; break;
      case "PageDown": next = value - step * 10; break;
      case "PageUp": next = value + step * 10; break;
      case "Home": next = minimum; break;
      case "End": next = maximum; break;
      default: return;
    }
    event.preventDefault();
    onChange(clamp(next));
  };

  return <button type="button" className={className} disabled={disabled}
    data-testid={testId} role="slider" tabIndex={disabled ? -1 : 0}
    aria-label={label} aria-valuemin={minimum} aria-valuemax={maximum}
    aria-valuenow={value} aria-valuetext={formatValue(value)}
    onKeyDown={handleKeyDown}
    onPointerDown={(event) => {
      if (disabled || !event.isPrimary) return;
      activePointer.current = event.pointerId;
      event.currentTarget.setPointerCapture(event.pointerId);
      updateFromPointer(event);
    }}
    onPointerMove={(event) => {
      if (activePointer.current === event.pointerId) updateFromPointer(event);
    }}
    onPointerUp={(event) => {
      if (activePointer.current !== event.pointerId) return;
      updateFromPointer(event);
      activePointer.current = null;
    }}
    onPointerCancel={() => { activePointer.current = null; }}
    onLostPointerCapture={() => { activePointer.current = null; }}>
    <span className={styles.densityTrack}>
      <span className={styles.densityFill}
        style={{ width: `${(value - minimum) / (maximum - minimum) * 100}%` }} />
      <span className={styles.densityThumb}
        style={{ left: `${(value - minimum) / (maximum - minimum) * 100}%` }} />
    </span>
  </button>;
};
