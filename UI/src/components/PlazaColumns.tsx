import { useValue } from "cs2/api";
import { AssetChoiceMap, AssetChoiceOption, NO_PLAZA_CENTER } from "../assetChoices";
import { generateDecorations, generatePaths, plazaArrangementJson$,
  selectAsset, selectPlazaCenter, setFurnitureDensity,
  setPlazaArrangementPlacement, setPlazaArrangementSpacing,
  setPlazaCenterPlacement, setPlazaCenterpieceSpacing, setPlazaDesignStyle,
  setPlazaFenceEnabled, setPlazaSquareSurface, SAME_SQUARE_SURFACE } from "../bindings";
import { Texts } from "../i18n";
import { AssetIcon, useIconFailures } from "./AssetIcon";
import { AssetTileGroup } from "./AssetTileGroup";
import { Column, DropdownCaret, HeadAction, Segmented, SliderRow } from "./Bar";
import { Hint } from "./Hint";
import { arrangementKinds, parseArrangement } from "./PlazaArrangementEditor";
import styles from "../panel.module.less";

type PlacementIcon = "center" | "mirrored" | "axis" | "around" | "boundary";

type StyleIcon = "auto" | "classic" | "modern" | "natural" | "promenade" | "sequence";

/** Plan symbols for the composer styles; Cohtml lacks most glyphs. */
const StyleIcon = ({ kind }: { kind: StyleIcon }) => (
  <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    {kind === "auto" ? <>
      <path d="M4 20 15 9" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
      <path d="M17 3l1 2.5L20.5 6.5 18 7.5 17 10l-1-2.5L13.5 6.5 16 5.5z" fill="currentColor" />
    </> : null}
    {kind === "classic" ? <>
      <path d="M7 6h10v12H7z" fill="none" stroke="currentColor" strokeWidth="1.5" />
      {[7, 12, 17].map((y) => <g key={y}>
        <circle cx="3.5" cy={y} r="1.6" fill="currentColor" />
        <circle cx="20.5" cy={y} r="1.6" fill="currentColor" />
      </g>)}
    </> : null}
    {kind === "modern" ? <>
      <path d="M8 5h12l-4 14H4z" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path d="M10 9h6l-1.2 4H8.8z" fill="currentColor" />
    </> : null}
    {kind === "natural" ? <>
      <path d="M6 12c0-5 4-8 8-7s5 4 4 8-5 6-9 5-3-3-3-6z" fill="none"
        stroke="currentColor" strokeWidth="1.5" />
      <circle cx="19" cy="19" r="2.5" fill="currentColor" />
      <circle cx="4.5" cy="5" r="2" fill="currentColor" />
    </> : null}
    {kind === "promenade" ? <>
      <path d="M2 9h20v6H2z" fill="none" stroke="currentColor" strokeWidth="1.5" />
      {[5, 12, 19].map((x) => <g key={x}>
        <circle cx={x} cy="5" r="1.6" fill="currentColor" />
        <circle cx={x} cy="19" r="1.6" fill="currentColor" />
      </g>)}
    </> : null}
    {kind === "sequence" ? <>
      <path d="M2 5h9v9H2z" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <circle cx="18" cy="17" r="4.5" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path d="M11 11l4 3" stroke="currentColor" strokeWidth="1.5" strokeDasharray="1.5 1.5" />
    </> : null}
  </svg>
);

const PlacementIcon = ({ kind }: { kind: PlacementIcon }) => (
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
  choices: AssetChoiceMap;
  centerOptions: AssetChoiceOption[];
  centerSelected: string;
  centerPlacement: number;
  arrangementPlacement: number;
  centerpieceSpacing: number;
  arrangementSpacing: number;
  fenceEnabled: boolean;
  /** -1 for the classic arrangement, otherwise the composer style. */
  designStyle: number;
  /** Paving of the composed squares, or SAME_SQUARE_SURFACE. */
  squareSurface: string;
  density: number;
  pathPlanReady: boolean;
  decorationPlanReady: boolean;
  structureLocked: boolean;
  furnishingLocked: boolean;
  canPlan: boolean;
  /** Localized "arrangement does not fit" notice, if any. */
  notice: string | null;
  arrangementOpen: boolean;
  onEditArrangement: () => void;
};

/** Plaza settings: centerpiece, arrangement, surface and fence. */
export const PlazaColumns = ({ t, choices, centerOptions, centerSelected,
  centerPlacement, arrangementPlacement, centerpieceSpacing, arrangementSpacing,
  fenceEnabled, designStyle, squareSurface, density, pathPlanReady, decorationPlanReady, structureLocked,
  furnishingLocked, canPlan, notice, arrangementOpen, onEditArrangement }: Props) => {
  const icons = useIconFailures();
  const arrangement = parseArrangement(useValue(plazaArrangementJson$));
  const surface = choices.surface;
  const fence = choices.fence;
  const noCenter = centerSelected === NO_PLAZA_CENTER;
  // The composer places objects from the design; the arrangement
  // settings only apply to the classic arrangement.
  const composed = designStyle >= 0;

  const experimental = (text: string) => `${text}. ${t.plazaDesignHelp}`;

  return <>
    <Column title={t.colCenter} tone="toneBlue" testId="column-center"
      action={pathPlanReady ? <HeadAction text={t.variant} hint={t.variantHint}
        disabled={structureLocked || !canPlan} onClick={generatePaths}
        testId="paths-variant" /> : null}>
      <AssetTileGroup testId="plaza-center-choices" options={centerOptions}
        isActive={(name) => name === centerSelected} onSelect={selectPlazaCenter}
        none={{ label: t.plazaNoCenter, active: noCenter,
          onSelect: () => selectPlazaCenter(NO_PLAZA_CENTER) }}
        disabled={structureLocked} />
      {/* A composed design places the centerpiece itself; its rows give
          way to the paving of the squares. */}
      {composed ? <AssetTileGroup testId="plaza-square-surface-choices"
        label={t.plazaSquareSurface} options={surface?.options ?? []}
        isActive={(name) => squareSurface === name}
        onSelect={setPlazaSquareSurface}
        none={{ label: t.plazaSquareSame, active: squareSurface === SAME_SQUARE_SURFACE,
          onSelect: () => setPlazaSquareSurface(SAME_SQUARE_SURFACE) }}
        disabled={structureLocked} /> : <>
        <Segmented label={t.plazaCenterPlacementShort} value={centerPlacement}
          disabled={structureLocked} testId="plaza-center-placement"
          onChange={setPlazaCenterPlacement} options={[
            { value: 0, icon: <PlacementIcon kind="center" />, hint: t.plazaCenterSingle },
            { value: 1, icon: <PlacementIcon kind="mirrored" />, hint: t.plazaCenterMirrored },
            { value: 2, icon: <PlacementIcon kind="axis" />, hint: t.plazaCenterAxis },
          ]} />
        <SliderRow label={t.spacing} value={centerpieceSpacing} minimum={5}
          maximum={60} unit=" m" testId="plaza-centerpiece-spacing"
          disabled={structureLocked || noCenter || centerPlacement === 0}
          onChange={setPlazaCenterpieceSpacing} />
      </>}
    </Column>
    <Column title={t.colArrangement} tone="toneGreen" testId="column-arrangement"
      action={decorationPlanReady ? <HeadAction text={t.rearrange}
        hint={t.rearrangeHint} disabled={furnishingLocked || !pathPlanReady}
        onClick={generateDecorations} testId="furnishing-variant" /> : null}>
      {/* One choice of how the plaza is arranged: the two classic
          arrangements, or a composed design in one of the styles. */}
      <Segmented label={t.plazaArrangementPlacementShort}
        value={composed ? `s${designStyle}` : `p${arrangementPlacement}`}
        disabled={structureLocked} testId="plaza-arrangement-placement"
        onChange={(value: string) => {
          if (value.startsWith("s")) {
            setPlazaDesignStyle(Number(value.slice(1)));
            return;
          }
          setPlazaDesignStyle(-1);
          setPlazaArrangementPlacement(Number(value.slice(1)));
        }} options={[
          { value: "p0", icon: <PlacementIcon kind="around" />, hint: t.plazaAroundCenter },
          { value: "p1", icon: <PlacementIcon kind="boundary" />, hint: t.plazaAlongBoundary },
          { value: "s0", icon: <StyleIcon kind="auto" />, hint: experimental(t.plazaDesignAuto) },
          { value: "s1", icon: <StyleIcon kind="classic" />, hint: experimental(t.plazaDesignClassic) },
          { value: "s2", icon: <StyleIcon kind="modern" />, hint: experimental(t.plazaDesignModern) },
          { value: "s5", icon: <StyleIcon kind="natural" />, hint: experimental(t.plazaDesignNatural) },
          { value: "s3", icon: <StyleIcon kind="promenade" />, hint: experimental(t.plazaDesignPromenade) },
          { value: "s4", icon: <StyleIcon kind="sequence" />, hint: experimental(t.plazaDesignSequence) },
        ]} />
      <SliderRow label={arrangementPlacement === 0 ? t.toCenter : t.toEdge}
        value={arrangementSpacing} minimum={0} maximum={20} unit=" m"
        disabled={structureLocked || composed} testId="plaza-arrangement-spacing"
        onChange={setPlazaArrangementSpacing} />
      <SliderRow label={t.density} value={density} minimum={25} maximum={200}
        step={25} unit="%" disabled={furnishingLocked}
        testId="plaza-density" onChange={setFurnitureDensity} />
      <div className={styles.settingRow}>
        <span className={styles.settingLabel}>{t.plazaArrangement}</span>
        <Hint text={t.plazaArrangementHelp}>
          <button type="button" data-testid="plaza-arrangement-edit"
            aria-expanded={arrangementOpen}
            className={`${styles.arrangementPreview} ${
              arrangementOpen ? styles.chipOpen : ""}`}
            onClick={onEditArrangement}>
            {arrangement.map((item, index) => {
              const kind = arrangementKinds.find((candidate) =>
                candidate.kind === item.kind) || arrangementKinds[0];
              const asset = choices[kind.key]?.options.find((option) =>
                option.name === item.name);
              return <AssetIcon key={index} icon={asset?.icon} icons={icons}
                className={styles.chipIcon}
                fallback={<b className={styles.chipIcon}>
                  {t.categories[kind.key].charAt(0)}</b>} />;
            })}
            <span className={styles.chipLabel}>{t.editArrangement}</span>
            <DropdownCaret open={arrangementOpen} />
          </button>
        </Hint>
      </div>
      {notice ? <div className={styles.columnNotice} role="alert">{notice}</div> : null}
    </Column>
    <Column title={t.colSurfaceFence} tone="toneAmber" testId="column-surface">
      <AssetTileGroup testId="plaza-surface-choices" label={t.background}
        options={surface?.options ?? []}
        isActive={(name) => surface?.selected === name}
        onSelect={(name) => selectAsset("Surface", name)}
        disabled={structureLocked} />
      <AssetTileGroup testId="plaza-fence-choices" label={t.plazaFence}
        options={fence?.options ?? []}
        isActive={(name) => fenceEnabled && fence?.selected === name}
        onSelect={(name) => { selectAsset("Fence", name); setPlazaFenceEnabled(true); }}
        none={{ label: t.plazaNoFence, active: !fenceEnabled,
          onSelect: () => setPlazaFenceEnabled(false) }}
        disabled={structureLocked} />
    </Column>
  </>;
};
