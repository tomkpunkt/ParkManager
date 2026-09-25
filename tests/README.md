# ParkManager mock and batch checks

The browser mock renders the **real** `UI/src/panel.tsx` and its existing
components. `UI/mock/mockApi.ts` supplies simulated Cities: Skylines II bindings
and actions. It does not create game entities or validate the game's prefab,
height, collision, or rendering systems.

No game installation, modding toolchain, `CSII_TOOLPATH`, or game DLLs are needed
for the mock and batch runner. Install the .NET 8 SDK and Node.js 18 or newer.
NuGet and npm access are needed for the initial dependency restore; subsequent
runs can use their local caches. The test project restores
`UnityMathematics.NoDeps` from NuGet because the production geometry sources
use `float2` and `math` helpers.

For an interactive mock, from the repository root in PowerShell:

```powershell
cd UI
npm ci
npm run mock:start
```

To use a different port, run `powershell -NoProfile -ExecutionPolicy Bypass
-File mock/run-mock.ps1 -Port 8766` from `UI`. The default is 8765. Press Ctrl+C
to stop the server. The production mod is not built by this command.

To generate a batch report separately, from the repository root:

```powershell
dotnet run --project tests/PlazaBatch/PlazaBatch.csproj -- 100 tests/PlazaBatch/report.json
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
For plaza boundary layouts, the batch also checks actual edge clearance,
inward-facing furniture, mirrored groups on symmetric outlines, and independent
edge groups on asymmetric outlines.
For circular plaza layouts, the existing 25–200% slider targets evenly spaced
arrangement angles from 180° down to 15°. If a complete ring does not fit,
the planner retries with fewer pairs and recalculates all angles; the batch
checks the spacing, all eight slider levels, and the furniture cap.

This is a geometry and UI regression tool, not a replacement for an in-game
build test. Asset dimensions and placement-system behavior are not available
outside the game yet; those require an in-game diagnostic capture.

UI regression tests use Playwright with Chromium:

```powershell
cd UI
npm ci
npx playwright install chromium
npm run test:ui
```

`test:ui` builds the geometry host and mock UI, starts a private server on a
free local port, runs Playwright, then stops only that server. An already-open
interactive mock on port 8765 is unaffected. The test project builds without
`ParkManager.csproj` or the official modding-toolchain imports.

The tests cover drawing and stage progression, live planning/seed changes,
common panel insets, flex-column spacing, compact-width containment, toolbar
separation, and a compiled-CSS guard against `display: grid`. Chromium cannot
prove that CS2's Gameface renderer accepts every CSS rule, so an in-game visual
pass remains necessary before deployment.

Gameface's Yoga layout does not support CSS Grid. Production panel columns
therefore use sized Flexbox items; Playwright checks their geometry in Chromium
and the CSS guard prevents a browser-only Grid regression.
