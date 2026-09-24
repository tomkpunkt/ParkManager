import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { ParkManagerPanel } from '../src/panel';
import { get, scenario, set } from './mockApi';
import { useValue } from 'cs2/api';
import { decorationPlanReady$, pathPlanReady$, plannerMode$, pointCount$,
  polygonClosed$, polygonValid$ } from '../src/bindings';

type BatchCase = { index: number; seed: number; width: number; height: number;
  mode: string; errors: string[]; plan: { centers: { x: number; y: number; Radius: number }[];
    furniture: { kind: string; x: number; y: number; FootprintRadius: number }[];
    routes: { ax: number; ay: number; bx: number; by: number }[] } };
type BatchReport = { count: number; failed: number; cases: BatchCase[] };
type LivePlan = { seed: number; error?: string;
  paths: { ax: number; ay: number; bx: number; by: number; hidden: boolean }[];
  centers: { x: number; y: number; radius: number }[];
  furniture: { x: number; y: number; radius: number; kind: string; asset: string }[] };

const icon = (letter: string, color: string) => `data:image/svg+xml,${encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" rx="8" fill="${color}"/><text x="32" y="43" text-anchor="middle" fill="white" font-size="33" font-family="Arial">${letter}</text></svg>`)}`;
const choices: Record<string, any> = {};
for (const [key, label, color] of [
  ['surface', 'Gras', '#50965a'], ['tree', 'Baum', '#2c9d4a'],
  ['bush', 'Busch', '#629945'], ['plazaplanter', 'Pflanzkasten', '#77934d'],
  ['bench', 'Bank', '#8f6c45'], ['lamp', 'Laterne', '#d3bb58'],
  ['fence', 'Zaun', '#8a9188'], ['trashbin', 'Mülleimer', '#647b82'],
] as const) {
  choices[key] = { selected: '', selectedMany: [], options: Array.from({ length: key === 'surface' ? 11 : 15 }, (_, n) =>
    ({ name: `${label} ${n + 1}`, icon: icon(label[0], color) })) };
}
set('AssetOptionsJson', JSON.stringify(choices));
set('PlazaCenterOptionsJson', JSON.stringify(Array.from({ length: 23 }, (_, index) => ({
  name: index === 0 ? 'Mock Fountain' : `Mock Center ${index + 1}`,
  icon: icon(index === 0 ? 'F' : 'S', index === 0 ? '#3b9bad' : '#b4a37a'),
}))));
set('PlazaArrangementJson', JSON.stringify([{ kind: 0, name: 'Bank 1' }]));
scenario('empty');

function App() {
  const plannerMode = useValue(plannerMode$);
  const polygonClosed = useValue(polygonClosed$);
  const polygonValid = useValue(polygonValid$);
  const pointCount = useValue(pointCount$);
  const pathPlanReady = useValue(pathPlanReady$);
  const decorationPlanReady = useValue(decorationPlanReady$);
  const [report, setReport] = useState<BatchReport | null>(null);
  const [livePlan, setLivePlan] = useState<LivePlan | null>(null);
  const [planError, setPlanError] = useState('');
  const [selected, setSelected] = useState(0);
  const [points, setPoints] = useState<{ x: number; y: number }[]>([]);
  const [entrances, setEntrances] = useState<{ x: number; y: number }[]>([]);
  const previewPoints = points.length >= 3 ? points : pointCount >= 3 ? [
    { x: 200, y: 180 }, { x: 700, y: 180 },
    { x: 700, y: 500 }, { x: 200, y: 500 } ] : points;
  useEffect(() => { if (pointCount === 0) { setPoints([]); setEntrances([]); } }, [pointCount]);
  useEffect(() => {
    const recalculate = async (event: Event) => {
      const seed = (event as CustomEvent<{ seed: number }>).detail.seed;
      const polygon = points.length >= 3 ? points : [
        { x: 200, y: 180 }, { x: 700, y: 180 },
        { x: 700, y: 500 }, { x: 200, y: 500 } ];
      const gates = entrances.length ? entrances : [{ x: 450, y: 180 }];
      try {
        const response = await fetch('/api/plan', { method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ polygon: polygon.map((p) => ({ x: p.x / 8, y: p.y / 8 })),
            entrances: gates.map((p) => ({ x: p.x / 8, y: p.y / 8 })), seed,
            siteType: get('SiteType'), pathType: get('PathType'),
            plazaLayout: get('PlazaLayout'),
            includeCenterpiece: get('PlazaCenterSelected') !== '__none__',
            centerRadius: 2.5,
            arrangement: JSON.parse(get<string>('PlazaArrangementJson') || '[]'),
            vegetationDensity: get('VegetationDensity'),
            furnitureDensity: get('FurnitureDensity'),
            enabledMask: get('DecorationEnabledMask') }) });
        const result = await response.json() as LivePlan;
        if (!response.ok || result.error) throw new Error(result.error || `HTTP ${response.status}`);
        setLivePlan(result); setPlanError('');
      } catch (error) {
        setLivePlan(null);
        setPlanError(`Live-Planung nicht erreichbar: ${error}`);
      }
    };
    window.addEventListener('mock:recalculate', recalculate);
    return () => window.removeEventListener('mock:recalculate', recalculate);
  }, [points, entrances]);
  const current = report?.cases[selected];
  const scaleX = current ? 650 / current.width : 1;
  const scaleY = current ? 390 / current.height : 1;
  const mapX = (x: number) => 80 + x * scaleX;
  const mapY = (y: number) => 140 + y * scaleY;
  const onMapClick = (event: React.MouseEvent<SVGSVGElement>) => {
    if (report) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const point = { x: (event.clientX - rect.left) / rect.width * 900,
      y: (event.clientY - rect.top) / rect.height * 600 };
    if (plannerMode && polygonValid) {
      const next = [...entrances, point]; setEntrances(next);
      setLivePlan(null); set('PathPlanReady', false);
      set('EntranceCount', next.length); return;
    }
    if (polygonClosed) return;
    if (points.length >= 3 && Math.hypot(point.x - points[0].x,
        point.y - points[0].y) < 25) {
      set('PolygonClosed', true); set('PolygonValid', true); return;
    }
    const next = [...points, point]; setPoints(next); setLivePlan(null);
    set('PointCount', next.length);
    if (next.length >= 3) {
      const area = Math.abs(next.reduce((sum, p, i) => {
        const q = next[(i + 1) % next.length]; return sum + p.x * q.y - q.x * p.y;
      }, 0)) / 2;
      set('PolygonArea', Math.round(area * 0.1));
    }
  };
  const loadReport = async (file?: File) => {
    if (!file) return;
    try { const parsed = JSON.parse(await file.text()) as BatchReport;
      if (!Array.isArray(parsed.cases)) throw new Error('Keine Fälle vorhanden');
      setReport(parsed); setSelected(0);
    } catch (error) { alert(`Ungültiger Bericht: ${error}`); }
  };
  return <>
    <div className="mockbar"><strong>ParkManager Mock · ohne Spiel</strong>
      {(['empty', 'outline', 'paths', 'decorated', 'plaza'] as const).map((item) =>
        <button key={item} onClick={() => { scenario(item); setPoints([]); setEntrances([]); setLivePlan(null); }}>
          {item}</button>)}
      <button onClick={() => { scenario('empty'); setPoints([]); setEntrances([]); setReport(null); setLivePlan(null); }}>
        Neu zeichnen</button>
      <label>Batch-Bericht öffnen <input type="file" accept=".json" hidden
        onChange={(event) => loadReport(event.target.files?.[0])} /></label>
    </div>
    <div className="mocknote">{planError || (report ? 'Batch-Bericht: Geometrie des echten C#-Plaza-Planers.'
      : plannerMode ? 'Klicke auf die Karte, um Eingänge zu markieren.'
      : polygonClosed ? 'Umriss geschlossen. Weiter zu Untergrund.'
      : 'Klicke auf die Karte, um Punkte zu setzen. Zum Schließen den ersten Punkt erneut anklicken.')}</div>
    <main className="map">
      <svg viewBox="0 0 900 600" preserveAspectRatio="none" onClick={onMapClick}>
        {current ? <>
          <polygon points={`${mapX(0)},${mapY(0)} ${mapX(current.width)},${mapY(0)} ${mapX(current.width)},${mapY(current.height)} ${mapX(0)},${mapY(current.height)}`} />
          {current.plan.routes.map((route, i) => <line key={i} x1={mapX(route.ax)} y1={mapY(route.ay)} x2={mapX(route.bx)} y2={mapY(route.by)} stroke="#bde5ed" strokeWidth="2" strokeDasharray="5 4" />)}
          {current.plan.centers.map((center, i) => <circle key={i} cx={mapX(center.x)} cy={mapY(center.y)} r={center.Radius * Math.min(scaleX, scaleY)} fill="#479ec0" stroke="white" strokeWidth="2" />)}
          {current.plan.furniture.map((item, i) => <circle key={i} cx={mapX(item.x)} cy={mapY(item.y)} r={Math.max(4, item.FootprintRadius * Math.min(scaleX, scaleY))} fill={item.kind === 'Bench' ? '#ad7c4b' : item.kind === 'Tree' ? '#43a761' : '#e7c452'}><title>{item.kind}</title></circle>)}
        </> : previewPoints.length > 0 ? <>
          <polygon points={previewPoints.map((p) => `${p.x},${p.y}`).join(' ')} />
          {livePlan && pathPlanReady ? livePlan.paths.map((path, i) =>
            <line key={`path-${i}`} x1={path.ax * 8} y1={path.ay * 8}
              x2={path.bx * 8} y2={path.by * 8} stroke={path.hidden ? '#9ac7cf' : '#edce66'}
              strokeWidth={path.hidden ? '2' : '4'}
              strokeDasharray={path.hidden ? '5 4' : undefined} />) : null}
          {livePlan && pathPlanReady ? livePlan.centers.map((center, i) =>
            <circle key={`center-${i}`} cx={center.x * 8} cy={center.y * 8}
              r={center.radius * 8} fill="#58bed4" stroke="white" strokeWidth="1" />) : null}
          {livePlan && decorationPlanReady ? livePlan.furniture.map((item, i) =>
            <circle key={`furniture-${i}`} cx={item.x * 8} cy={item.y * 8}
              r={Math.max(2, item.radius * 8)} fill={item.kind === 'Bench' ? '#bd8758'
                : item.kind === 'Tree' || item.kind === 'Bush' ? '#68cb74' : '#efd56c'}>
              <title>{item.kind} {item.asset}</title></circle>) : null}
          {points.map((p, i) => <circle key={i} cx={p.x} cy={p.y} r={i === 0 ? '9' : '5'}
            fill={i === 0 ? '#80e69a' : '#f4cb62'} />)}
          {entrances.map((p, i) => <circle key={`gate-${i}`} cx={p.x} cy={p.y}
            r="8" fill="#5cc9fa" stroke="white" strokeWidth="2" />)}
        </> : null}
      </svg>
      {livePlan && !report ? <div className="report"><strong>Live-Layout · Seed {livePlan.seed}</strong>
        <div>{livePlan.paths.length} Routen · {livePlan.centers.length} Zentren · {livePlan.furniture.length} Assets</div>
        <small>Berechnet mit den Produktions-Planern. Asset-Radien im Mock sind Beispielwerte.</small>
      </div> : null}
      {report ? <div className="report"><strong>{report.count} Varianten · {report.failed} mit Befund</strong><br />
        <button onClick={() => setSelected(Math.max(0, selected - 1))}>◀</button>
        <input type="number" min="0" max={report.cases.length - 1} value={selected}
          onChange={(event) => setSelected(Math.min(report.cases.length - 1, Math.max(0, Number(event.target.value))))} />
        <button onClick={() => setSelected(Math.min(report.cases.length - 1, selected + 1))}>▶</button>
        <div>Seed {current?.seed} · {current?.mode} · {current?.width} × {current?.height} m</div>
        <div>{current?.errors.length ? current.errors.join(', ') : 'Keine geometrischen Befunde'}</div>
      </div> : null}
    </main>
    <ParkManagerPanel />
  </>;
}
createRoot(document.getElementById('root')!).render(<App />);
