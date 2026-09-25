import { useSyncExternalStore } from 'react';

type Binding<T> = { key: string; initial: T };
const values: Record<string, any> = {};
const listeners = new Set<() => void>();
let seed = 1;
export const recalculate = () => window.dispatchEvent(new CustomEvent('mock:recalculate',
  { detail: { seed } }));
const emit = () => listeners.forEach((listener) => listener());
export const bindValue = <T,>(scope: string, name: string, initial: T): Binding<T> => {
  const key = `${scope}.${name}`;
  if (!(key in values)) values[key] = initial;
  return { key, initial };
};
export const useValue = <T,>(binding: Binding<T>): T => useSyncExternalStore(
  (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
  () => values[binding.key] as T);
export const get = <T,>(name: string): T => values[`ParkManager.${name}`];
export const set = (name: string, value: any) => {
  values[`ParkManager.${name}`] = value; emit();
};
export const scenario = (kind: 'empty' | 'outline' | 'paths' | 'decorated' | 'plaza') => {
  const plaza = kind === 'plaza';
  Object.assign(values, {
    'ParkManager.PanelOpen': true, 'ParkManager.ToolActive': true,
    'ParkManager.PointCount': kind === 'empty' ? 0 : 4,
    'ParkManager.PolygonArea': kind === 'empty' ? 0 : 2400,
    'ParkManager.PolygonClosed': kind !== 'empty',
    'ParkManager.PolygonValid': kind !== 'empty',
    'ParkManager.PlannerMode': kind !== 'empty' && kind !== 'outline',
    'ParkManager.EntranceCount': kind === 'empty' || kind === 'outline' ? 0 : 2,
    'ParkManager.PathPlanReady': kind === 'paths' || kind === 'decorated' || plaza,
    'ParkManager.PathBuildPresent': kind === 'decorated',
    'ParkManager.DecorationBuildPresent': false,
    'ParkManager.DecorationPlanReady': kind === 'decorated' || plaza,
    'ParkManager.SiteType': plaza ? 1 : 0,
    'ParkManager.PlazaCenterPlacement': 0,
    'ParkManager.PlazaArrangementPlacement': 0,
    'ParkManager.PlazaCenterpieceSpacing': 20,
    'ParkManager.PlazaArrangementSpacing': 4,
    'ParkManager.PlazaFenceEnabled': false,
    'ParkManager.PathBuildSummary': '', 'ParkManager.PathBuildStatus': 'ok',
    'ParkManager.DecorationSummary': '',
    'ParkManager.PlazaCenterSelected': plaza ? 'Mock Fountain' : '',
    'ParkManager.Locale': 'de',
  }); emit();
  if (plaza) recalculate();
};
export const trigger = (scope: string, action: string, payload?: any) => {
  if (scope === 'tool') { values[`tool.selectedSnapMask`] = payload; emit(); return; }
  switch (action) {
    case 'TogglePanel': case 'SetPanelOpen': set('PanelOpen', payload ?? !get('PanelOpen')); break;
    case 'ToggleTool': set('PanelOpen', !get('PanelOpen')); break;
    case 'ClearPolygon': scenario('empty'); break;
    case 'SetPlannerMode': set('PlannerMode', payload); break;
    case 'SetPathType': set('PathType', payload); set('PathPlanReady', false); break;
    case 'SetSiteType': set('SiteType', payload); set('PathPlanReady', false); break;
    case 'SetPlazaCenterPlacement': set('PlazaCenterPlacement', payload); replanPlaza(); break;
    case 'SetPlazaArrangementPlacement': set('PlazaArrangementPlacement', payload); replanPlaza(); break;
    case 'SetPlazaCenterpieceSpacing': set('PlazaCenterpieceSpacing', payload); replanPlaza(); break;
    case 'SetPlazaArrangementSpacing': set('PlazaArrangementSpacing', payload); replanPlaza(); break;
    case 'SetPlazaFenceEnabled': set('PlazaFenceEnabled', payload); replanPlaza(); break;
    case 'SelectPlazaCenter': set('PlazaCenterSelected', payload); replanPlaza(); break;
    case 'GeneratePaths': if (get('SiteType') === 1) { generatePlaza(); break; } seed++; set('PathPlanReady', true); set('PathBuildSummary', `Mock-Seed ${seed}`); recalculate(); break;
    case 'BuildPaths': set('PathBuildPresent', true); break;
    case 'RemoveBuiltPaths': set('PathBuildPresent', false); set('DecorationBuildPresent', false); set('DecorationPlanReady', false); break;
    case 'GenerateDecorations': seed++; if (get('SiteType') === 1 && get('DecorationPlanReady')) { void rollPlazaVariant(true); break; } set('DecorationPlanReady', true); set('DecorationSummary', `Mock-Seed ${seed}`); recalculate(); break;
    case 'BuildDecorations': set('DecorationBuildPresent', true); break;
    case 'RemoveBuiltDecorations': set('DecorationBuildPresent', false); break;
    case 'FinishPark': scenario('empty'); break;
    case 'SetVegetationDensity': set('VegetationDensity', payload); set('DecorationPlanReady', false); break;
    case 'SetFurnitureDensity': set('FurnitureDensity', payload); if (get('SiteType') === 1) replanPlaza(); else set('DecorationPlanReady', false); break;
    case 'ToggleDecorationCategory': set('DecorationEnabledMask', get<number>('DecorationEnabledMask') ^ (1 << (payload - 1))); set('DecorationPlanReady', false); break;
    case 'EditPlazaArrangement': editArrangement(payload); break;
    case 'SelectAsset': selectAsset(payload); break;
    default: break;
  }
};
function editArrangement(command: string) {
  const [action, indexText, value] = command.split('\n');
  const index = Number(indexText);
  const items = JSON.parse(get<string>('PlazaArrangementJson') || '[]');
  if (action === 'add' && items.length < 5) items.push({ kind: 0, name: 'Mock Bench' });
  if (action === 'remove' && items.length > 1) items.splice(index, 1);
  if (action === 'kind' && items[index]) { items[index].kind = Number(value); items[index].name = ''; }
  if (action === 'asset' && items[index]) items[index].name = value;
  if (action === 'move' && items[index] && items[Number(value)])
    [items[index], items[Number(value)]] = [items[Number(value)], items[index]];
  set('PlazaArrangementJson', JSON.stringify(items)); replanPlaza();
}
// Same order as PlazaFurnitureKind: Bench, Lamp, TrashBin, Tree, Bush.
const arrangementKeys = ['bench', 'lamp', 'trashbin', 'tree', 'plazaplanter'];
function generatePlaza() {
  const reroll = get<boolean>('PathPlanReady');
  set('PathPlanReady', true); set('DecorationPlanReady', true);
  if (!reroll) { set('PathBuildSummary', 'Plaza-Regeln angewendet'); recalculate(); return; }
  seed++; void rollPlazaVariant(false);
}
/** Rolls plaza settings with the production PlazaVariantRoller. */
async function rollPlazaVariant(furnishingOnly: boolean) {
  const choices = JSON.parse(get<string>('AssetOptionsJson') || '{}');
  const names = (key: string): string[] =>
    (choices[key]?.options ?? []).map((option: { name: string }) => option.name);
  const centers = JSON.parse(get<string>('PlazaCenterOptionsJson') || '[]')
    .map((option: { name: string }) => option.name);
  try {
    const response = await fetch('/api/plaza-variant', { method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ seed, furnishingOnly, centers,
        surfaces: names('surface'), fences: names('fence'),
        assetsByKind: arrangementKeys.map(names) }) });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const variant = await response.json();
    set('FurnitureDensity', variant.density);
    set('PlazaArrangementJson', JSON.stringify(variant.arrangement));
    if (!furnishingOnly) {
      set('PlazaCenterSelected', variant.center || '__none__');
      set('PlazaCenterPlacement', variant.centerPlacement);
      set('PlazaCenterpieceSpacing', variant.centerpieceSpacing);
      set('PlazaArrangementPlacement', variant.arrangementPlacement);
      set('PlazaArrangementSpacing', variant.arrangementSpacing);
      set('PlazaFenceEnabled', !!variant.fence);
      if (variant.surface && choices.surface) choices.surface.selected = variant.surface;
      if (variant.fence && choices.fence) choices.fence.selected = variant.fence;
      set('AssetOptionsJson', JSON.stringify(choices));
      set('PathBuildSummary', `Plaza-Variante · Seed ${seed}`);
    } else set('DecorationSummary', `Plaza-Ausstattung · Seed ${seed}`);
    set('DecorationPlanReady', true);
    recalculate();
  } catch (error) {
    set('PathBuildStatus', 'error');
    set('PathBuildSummary', `Plaza-Variante nicht erreichbar: ${error}`);
  }
}
function replanPlaza() {
  if (get('SiteType') !== 1 || !get('PathPlanReady')) return;
  set('DecorationPlanReady', true);
  recalculate();
}
function selectAsset(payload: string) {
  const [category, multi, name] = payload.split('\n');
  const key = category.toLowerCase();
  const choices = JSON.parse(get<string>('AssetOptionsJson') || '{}');
  const choice = choices[key];
  if (!choice) return;
  if (multi === 'multi') choice.selectedMany = choice.selectedMany.includes(name)
    ? choice.selectedMany.filter((item: string) => item !== name)
    : [...choice.selectedMany, name];
  else choice.selected = name;
  set('AssetOptionsJson', JSON.stringify(choices));
  if (get('SiteType') === 1) replanPlaza();
  else set('DecorationPlanReady', false);
}
