# ParkManager

Early Cities: Skylines II code-mod bootstrap for a procedural park generator.

Current development version: 0.5.0. In addition to the polygon editor and
walkable procedural paths, ParkManager plans and builds a grass surface, trees,
bushes, benches, lamps, trash bins and an optional boundary fence from loaded
Vanilla assets.

Current controls:

- Left click places points and closes the polygon on its first point.
- Dragging a point moves it; invalid moves are rolled back.
- Right click deletes a hovered point, otherwise opens a closed polygon or
  removes the last point.
- Double-clicking a hovered edge inserts a point at the cursor.
- Ctrl+Z restores the previous polygon state; Escape exits the tool.
- The panel can reset the complete working polygon.

The tool header contains the same snap categories and game icons used by
ParkingLotTool: existing lot geometry, straight directions, road edges,
building edges, guide lines and zone grid. Snap targets contribute both a point
and a line; intersections receive higher priority, allowing a corner to sit on
a curb and remain exactly perpendicular at the same time. Colored cursor rings
identify the active target and a white ring marks a compound intersection.

After closing a valid polygon, **Parkplaner öffnen** switches to a locked
planning mode. Left-clicking a highlighted boundary edge marks an entrance;
**Generate Paths** now uses a self-contained hybrid polygon pipeline. Polygon
normalization and boundary-conforming ear-clipping triangulation are combined
with adaptive interior sampling to form a weighted visibility graph. A
terminal-metric MST connects the entrances; regional coverage paths and useful
low-stretch loops are then added. Right click removes a hovered entrance.

Each click on **Generate Paths** creates a new seed. The seed changes the
triangulation start, interior sample placement and small route preferences, so
repeated clicks create controlled variations while all polygon constraints stay
active.

Since M0.3.1, one click evaluates six deterministic variants derived from that
seed. A naturalness score rejects sharp bends, internal dead ends, crowded
junctions, extreme segment lengths, poor coverage and unsuitable loop counts.
Degree-two chains are corner-smoothed while gate approaches and shared junction
coordinates remain fixed.

Gate connections start orthogonally to their polygon edge. A short approach
segment forces the path into the park along the inward normal before it may turn;
the fallback connector cone is limited to ±45 degrees around that normal.

Optional loops are accepted only when independent segments keep at least eight
metres apart and new branches open by at least 30 degrees. Invalid narrow loops
are discarded atomically, so they cannot leave a partial spur behind.

The generated segment list is converted into a typed `ParkPathPlan`: coincident
endpoints become shared graph nodes, gate nodes are retained explicitly, bridge
edges are classified as primary routes and cycle edges as secondary routes.
Primary and gate paths use a four-metre corridor; secondary paths use three
metres.

**Testwege bauen** now prefers the original visible Vanilla pedestrian-path
prefab. Its native asphalt material and junction meshes therefore provide both
appearance and routing; separate rectangles no longer overlap at crossings.
Shared graph nodes reuse exactly one sampled terrain height so CS2 merges
adjacent courses into real junctions. If no suitable visible prefab exists, the
old invisible-path plus pavement-surface method remains available as a logged
compatibility fallback. **Testwege entfernen** removes the complete latest test
build as one unit.

Since 0.3.4, generated nodes, edges and fallback surfaces stay ordinary
top-level Vanilla entities instead of becoming `Game.Common.Owner` children.
Move It and Anarchy can therefore address individual path elements. ParkManager
retains group cleanup through serializable `ParkPathMember` relations, records a
geometry fingerprint and reports externally moved or deleted members as
manually edited. It will not silently regenerate over those changes.

Version 0.5 adds a versioned build receipt containing the park outline,
entrances, path/furnishing seeds and fence/build state. A loaded save can
reconstruct the locked planner and its deterministic previews. **Finish park**
now detaches that persisted record from the editor without deleting its Vanilla
entities and immediately starts a fresh outline, so several independently
grouped parks can coexist in one city. Completed records become closed bundles
and are deliberately not reopened in the editor. Every generated path, plant
and furnishing stays independently editable and deletable. Bulldozing the park
surface deliberately removes its path and fence edges before Vanilla's network reference
pass, then removes objects and areas in bounded batches and finally deletes the
logical record. Final in-game bulldoze and save/load verification remain within
M0.5.

Version 0.4 adds a deterministic furnishing pass. Trees and bushes are shown as
differently sized green preview points, benches as brown rectangles, lamps as
yellow points and trash bins as small brown-grey squares. Vegetation observes
path, entrance, furniture and boundary clearances; benches, lamps and trash bins
follow path sides. The optional fence follows the polygon and leaves gate
openings automatically. Every layer has a bounded object budget.

The panel exposes independent compact choosers for trees, bushes, benches,
lamps, fences and trash bins instead of one long park-template list. Each
category can stay on **Automatisch**, which reuses the coherent asset palette of
a deterministically selected Vanilla park, or pin one concrete scanned asset
without changing the other categories. The six selectors share one icon row;
their images come from the prefab itself and therefore also use Asset Icon
Library thumbnails when that mod is active. Trees and bushes support multiple
pinned species through a scrollable five-column icon matrix; checked tiles are
part of the deterministic species mix and the automatic tile restores
palette-based mixing. The same browser performs single selection for furniture,
fence and trash bins. Categories missing from a source park use vetted Vanilla
fallbacks.
Generated trees use the old age stage so a new procedural park reads as an
established landscape rather than a freshly planted development. Their target
density is one mature tree per roughly 380 m², capped at 90 trees with a
ten-metre minimum spacing; shrub density remains independent.
**Neu planen** creates another deterministic furnishing variant,
**Ausstattung bauen** creates normal editable game objects, and
**Ausstattung entfernen** keeps the paths while removing the other layers.
**Park entfernen** removes all ParkManager members together. Furniture offsets
use the selected path prefab's measured width; benches, lamps and trash bins
remain snapped to terrain height and their prefab axes are corrected to match
the preview. Post-build diagnostics report whether CS2 still marks any path
furniture as `Overridden` or `Hidden`. Fences are planned as continuous boundary
runs. Native `FencePrefab`/`NetFencePrefab` assets are built as real network
courses, letting CS2 repeat the fence mesh along the complete run; an
end-to-end object chain remains only as a compatibility fallback.

The polygon interaction and M0.3 entity-transfer pattern are adapted from the
GPL-3.0 ParkingLotTool: terrain raycast, click handling, editing conventions,
projected overlay, NetCourse creation, exact height reuse and Temp-entity
materialization. Parking-lot-specific generation and simulation code is not included.

## Requirements

- Official Cities: Skylines II modding toolchain
- `CSII_TOOLPATH` and `CSII_USERDATAPATH` configured by that toolchain
- .NET SDK compatible with the installed toolchain
- Node.js 18 or newer
- No third-party runtime assemblies are required by the mod.

## Build

```powershell
cd UI
npm install
cd ..
dotnet build -c Debug
```

The C# build also builds and deploys the UI bundle when `UI/node_modules`
exists.

## Reference projects

The bootstrap architecture was informed by ParkingLotTool, licensed under
GPL-3.0. ParkManager does not have a runtime dependency on ParkingLotTool.
Continuous fence spacing follows the mesh-bounds-based fence-mode behaviour of
the Apache-2.0 [Advanced Line Tool](https://github.com/algernon-A/LineTool-CS2);
ParkManager has no runtime dependency on that mod either.
