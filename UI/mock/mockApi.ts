import { useSyncExternalStore } from 'react';

type Binding<T> = { key: string; initial: T };
const values: Record<string, any> = {};
const listeners = new Set<() => void>();
let seed = 1;
const recalculate = () => window.dispatchEvent(new CustomEvent('mock:recalculate',
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
    'ParkManager.DecorationPlanReady': kind === 'decorated',
    'ParkManager.SiteType': plaza ? 1 : 0,
    'ParkManager.PathBuildSummary': '', 'ParkManager.DecorationSummary': '',
    'ParkManager.PlazaCenterSelected': plaza ? 'Mock Fountain' : '',
    'ParkManager.Locale': 'de',
  }); emit();
};
export const trigger = (scope: string, action: string, payload?: any) => {
  if (scope === 'tool') { values[`tool.selectedSnapMask`] = payload; emit(); return; }
  switch (action) {
    case 'TogglePanel': case 'SetPanelOpen': set('PanelOpen', payload ?? !get('PanelOpen')); break;
    case 'ToggleTool': set('PanelOpen', !get('PanelOpen')); break;
    case 'ClearPolygon': scenario('empty'); break;
    case 'TogglePlannerMode': set('PlannerMode', !get('PlannerMode')); break;
    case 'SetPathType': set('PathType', payload); set('PathPlanReady', false); break;
    case 'SetSiteType': set('SiteType', payload); set('PathPlanReady', false); break;
    case 'SetPlazaLayout': set('PlazaLayout', payload); set('PathPlanReady', false); break;
    case 'SelectPlazaCenter': set('PlazaCenterSelected', payload); set('PathPlanReady', false); break;
    case 'GeneratePaths': seed++; set('PathPlanReady', true); set('PathBuildSummary', `Mock-Seed ${seed}`); recalculate(); break;
    case 'BuildPaths': set('PathBuildPresent', true); break;
    case 'RemoveBuiltPaths': set('PathBuildPresent', false); set('DecorationBuildPresent', false); set('DecorationPlanReady', false); break;
    case 'GenerateDecorations': seed++; set('DecorationPlanReady', true); set('DecorationSummary', `Mock-Seed ${seed}`); recalculate(); break;
    case 'BuildDecorations': set('DecorationBuildPresent', true); break;
    case 'RemoveBuiltDecorations': set('DecorationBuildPresent', false); break;
    case 'FinishPark': scenario('empty'); break;
    case 'SetVegetationDensity': set('VegetationDensity', payload); set('DecorationPlanReady', false); break;
    case 'SetFurnitureDensity': set('FurnitureDensity', payload); set('DecorationPlanReady', false); break;
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
  set('PlazaArrangementJson', JSON.stringify(items)); set('DecorationPlanReady', false);
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
  set('DecorationPlanReady', false);
}
