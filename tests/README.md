# ParkManager mock and batch checks

The browser mock renders the **real** `UI/src/panel.tsx` and its existing
components. `UI/mock/mockApi.ts` supplies simulated Cities: Skylines II bindings
and actions. It does not create game entities or validate the game's prefab,
height, collision, or rendering systems.

From the repository root in PowerShell:

```powershell
dotnet run --project tests/PlazaBatch/PlazaBatch.csproj -- 100 tests/PlazaBatch/report.json
cd UI
npm run mock:build
cd ..
dotnet run --project tests/PlazaBatch/PlazaBatch.csproj -- --serve 8765
```

Open `http://localhost:8765/` in a browser. The .NET server is required for
live planning; the old static Python server cannot answer `/api/plan`. The
top bar loads representative
workflow states. On a fresh load, click the map to add polygon points and
click the first point again to close it. In the next stage, map clicks add
entrances. `Neu zeichnen` resets the interaction. `Neue Variante` and the
placement preview call the production C# park/plaza planners with the drawn
polygon, gates, seed, and density settings. Use
`Batch-Bericht öffnen` to load `tests/PlazaBatch/report.json`. Then step through
the generated seed/layout cases and inspect the actual C# planner's routes,
centerpieces, and furniture footprints on the map.

The batch runner uses the production `Geometry/PlazaPlanner.cs` and
`Geometry/PlazaPlan.cs` source files. Its default is 100 cases. Pass a different
count as the first argument. It checks deterministic replay, valid transforms,
center/routing presence, bounds, furniture count, and centerpiece collisions.
It exits nonzero on a finding, and the JSON preserves all cases for inspection.

This is a geometry and UI regression tool, not a replacement for an in-game
build test. Asset dimensions and placement-system behavior are not available
outside the game yet; those require an in-game diagnostic capture.

UI regression tests use Playwright with Chromium:

```powershell
cd UI
npm install
npx playwright install chromium
npm run test:ui
```

The tests cover drawing and stage progression, live planning/seed changes,
common panel insets, flex-column spacing, compact-width containment, toolbar
separation, and a compiled-CSS guard against `display: grid`. Chromium cannot
prove that CS2's Gameface renderer accepts every CSS rule, so an in-game visual
pass remains necessary before deployment.

Gameface's Yoga layout does not support CSS Grid. Production panel columns
therefore use sized Flexbox items; Playwright checks their geometry in Chromium
and the CSS guard prevents a browser-only Grid regression.
