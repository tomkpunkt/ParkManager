# Changelog

## Unreleased

- Use the full measured mesh footprint for plaza center previews and fit checks;
  reject building-like prefabs needing road access and composite prefabs whose
  child meshes cannot be bounded reliably by the parent geometry.
- Replace fixed plaza furniture mixes with a five-slot arrangement editor.
  Each ordered slot selects its own asset; whole arrangements are mirrored,
  validated and thinned together so placement never drops individual pieces.
- Add four seeded plaza layouts: axial (with two or three matching centerpieces
  on sufficiently long plots), radial, edge-oriented seating, and an open
  plaza. The centerpiece chooser also permits no centerpiece in other layouts;
  hidden pedestrian routes and furnishing clearances follow the chosen form.
- Inset fence runs 0.20 m from the outline, using shared inset corners so
  native fence-network segments stay connected while clearing neighbouring
  buildings at the boundary.
- Treat steep ground and nearby existing entities as advisory preflight findings
  instead of blocking builds that previously worked. Only invalid terrain data
  remains a pre-build stop; the panel shows the advisory and construction still
  runs through the normal CS2 materialization and rollback flow.
- Show path preflight failures with their coordinates directly in the Surface
  panel; keep the error visible after an aborted build until a new variant is
  generated.
- Add a pre-build terrain and collision check for paths, park surface and
  furnishing positions; show rejected locations in the overlay and report
  their world coordinates without creating game entities.
- Track creation definitions for each path/furnishing attempt and discard them
  on abort. Failure logs now include phase, seed and expected/observed counts.
- Remove obsolete park-count and standalone fence-toggle UI bindings, unused
  ECS queries and recovery helpers, unused site-type persistence, old draft/receipt serialization, and the
  pre-release orphan-node migration. Pre-release saves are no longer compatible.
- Keep polygon, entrance and placement previews in the current game session
  only. New park records no longer serialize draft points or entrances, and
  loading a save starts with an empty editor instead of restoring a draft.
- Build the selected park surface together with the pedestrian network in the
  Surface step, keep it when furnishings are removed, and hide surface prefabs
  without a dedicated UI preview from the picker.
- Structure the workflow shell as one shared panel header, body and footer;
  every step now renders its actions in the same bottom row.
- Compact the outline workflow panel, replace the completed-park count with
  the live polygon area in square metres, and leave more of the map visible
  while drawing a new park.
- Compact the path panel to the same height and add park/plaza type,
  path-width and thumbnail-based surface selection controls.
- Give every furnishing category its own preview-tile enable checkbox, replace
  the separate fence switch, align preview and chooser heights, and add full-
  width vegetation and furniture-density controls that regenerate the plan.
- Compact the completion panel to 210 UI units and remove the unrelated city-
  wide park count from the active park workflow.

- Add a narrow/wide footpath selector to the Paths workflow step. The chosen
  Vanilla pathway prefab controls its measured clearance during construction.
- Resolve the narrow choice explicitly to Vanilla's `Pavement Path` and reject
  bike/bicycle pathway prefabs; align the compact path selector to the right of
  the Paths step.
- Anchor every workflow step number, title and description to the common
  top-left content origin. Right-side settings now share the footer action
  alignment, and three-digit plant-density percentages no longer wrap.
- Recalculate an existing furnishing preview immediately, with its existing
  deterministic seed, when the user switches between narrow and wide paths.

- Fence boundary runs are again created as one native network edge per run, so
  Move It's manipulation mode can expose the edge's two Bezier curve handles
  instead of leaving a chain of short, straight fence pieces.
- Bundle deletion now adopts the actual start and end nodes materialized by CS2
  for every owned network edge before deletion, preventing invisible fence
  endpoint nodes from surviving when a park is removed.
- Fence courses share a quantized endpoint-height cache. Adjacent polygon sides
  now pass bit-identical 3D corner positions to CS2 instead of intermittently
  becoming separate overlapping nodes after independent terrain samples.

## 0.5.0 – development

- Fix completed-park deletion leaving wide-path node meshes behind. Internal
  path nodes are now deleted after their edges, externally connected nodes are
  preserved, and legacy unconnected `PedestrianPathWide01` orphans are cleaned
  automatically. If CS2 reuses an old orphan during a new build, the two-pass
  graph discovery adopts it into the new park instead of losing ownership.
- Materialize degree-two path chains as fitted cubic Vanilla courses instead
  of one straight course per preview segment. Gates and real junctions remain
  nodes; curves are recursively split only when a 1.5 m fit tolerance or the
  park boundary would be violated. This removes the wide prefab's circular
  node meshes from ordinary bends while retaining editable network geometry.
- Add staged `ParkManager PATH-DIAG` logging for critical path-rendering
  failures. Every build now records the generated graph, temporary ECS
  component coverage and the final Vanilla network, including segment lengths,
  endpoint ownership, node degrees, composition widths/flags and
  hidden/overridden or missing geometry components.
- Stabilize furnishing materialization with the same per-build baseline model
  used by paths. Permanent objects and surfaces are rediscovered after Apply;
  native fence edges are the invariant while merged fence nodes remain free to
  vary. A failed pass tags every discovered remainder before rollback.
- Enforce workflow locks in the simulation layer as well as the UI. A built
  park can no longer have its outline, gates, path variant, fence option,
  density or asset plan silently changed before the relevant build is removed.
  Tabs remain inspectable where safe, with invalid actions disabled.
- Select the established broad Vanilla park pavement
  `PedestrianPathWide01` deterministically. The similarly named narrow asset is
  a cycle path in the current asset set and is no longer chosen as a substitute.
- Normalize the procedural path graph before preview and construction: prune
  non-gate dead branches in multi-entrance parks and merge geometrically
  redundant, nearly collinear degree-two vertices, reducing unnecessary round
  node meshes. Real entrances, bends and junctions remain independent editable
  Vanilla nodes.
- Rediscover built path entities after `ApplyMode.Apply` from a per-build
  permanent-entity baseline. Validation now treats edges as the invariant unit
  while allowing CS2 to merge junction nodes, eliminating intermittent false
  aborts that left isolated path points. Failed builds also capture and delete
  every newly materialized edge, node and fallback surface and log separate
  expected/actual counts.
- Add the first multi-park lifecycle slice. Finishing a park now preserves its
  complete editable Vanilla entity group and receipt, detaches it from the
  workspace and starts an independent empty draft for the next park.
- Mark completed records as closed park bundles and stop restoring them into the
  editor. The UI only reports the number of bundles; the previous/latest-park
  action is no longer needed.
- Keep paths, plants and furnishing independently editable and deletable; only
  the completed park surface is the deliberate group-bulldoze anchor. Group
  cleanup ignores `Deleted + Temp` previews, marks path and fence edges in
  Modification2 before `ReferencesSystem`, removes ordinary members in bounded
  Modification3 batches and deletes the logical record last.
- Split workspace/record lifecycle, asset DTO parsing, asset-browser rendering
  and reusable workflow controls into focused source files; `panel.tsx` now
  coordinates the workflow rather than implementing every UI concern itself.
- Add a persisted procedural-site discriminator. Current and legacy records are
  parks; a reserved plaza kind prevents a future PlazaBuilder with its own
  settings and layout planner from being mistaken for a park preset.
- Replace the always-visible three-column control panel with a four-step park
  creation workflow: outline, paths, furnishing and completion. Each step now
  exposes one primary action and only the secondary actions valid in that state.
- Arrange the six asset categories as a stable 2-by-3 tile matrix beside a
  permanently reserved five-column icon chooser, preventing label wrapping and
  panel-height jumps while switching categories.
- Add German and English UI dictionaries selected from the active game locale;
  unsupported languages fall back to English.
- Add a separately versioned, serializable `ParkPlacementReceipt` to every new
  logical park build record.
- Persist the closed world-space outline, manual entrances, chosen path seed,
  furnishing seed, fence state and current build-result metadata.
- Reconstruct the locked planner, deterministic path graph and furnishing
  preview from that receipt after a save/load cycle without overwriting a new
  draft polygon.
- Keep mutable build status separate from the immutable geometry inputs so a
  failed furnishing pass cannot damage the saved recipe.
- Start the M0.5 lifecycle milestone; selection-anchor bulldozing, migration
  coverage and in-game save/load verification remain explicit follow-ups.
- Place benches and lamps directly beside the rendered path using each chosen
  prefab's real collision bounds plus a 20 cm override-safety gap instead of a
  fixed two-to-three-metre furniture margin.
- Plan furniture before vegetation, reserve furniture footprints across both
  categories and test plants against the complete rendered path width plus
  their own collision radius, preventing hidden plants under paths and overlaps
  with benches or lamps.
- Replace the long park-template list with six compact, independent asset
  choosers for trees, bushes, benches, lamps, fences and trash bins. Each
  category can return to automatic Vanilla-park palette selection separately.
- Add deterministic path-side trash-bin planning, preview, prefab resolution,
  editable placement, clearance checks and post-build visibility diagnostics.
- Render the six asset selectors as one compact icon strip. Thumbnails come
  from each prefab's `UIObject.m_Icon` or `thumbnailUrl`, with a small index and
  a text fallback when no icon provider is available.
- Allow tree and bush selectors to pin multiple species. A compact category
  button opens a scrollable icon matrix; each tile toggles one species and the
  automatic tile restores deterministic Vanilla-park palette mixing.
  Single-choice categories close the matrix after selection.
- Discover native `FencePrefab`/`NetFencePrefab` assets used by industrial
  chain-link fences and build each boundary run as one editable fence-network
  course. Repeated object pieces remain only as a compatibility fallback.

## 0.4.0 – development

- Add a deterministic furnishing planner with independent random streams for
  trees, bushes, benches, lamps and the optional boundary fence.
- Preview trees and bushes as differently sized green points, benches as brown
  rectangles, lamps as yellow points and continuous fence runs along the park
  boundary.
- Keep vegetation clear of paths, entrances and the boundary; place benches and
  lamps along path sides with category-specific spacing.
- Leave automatic six-metre gate gaps in the fence and cap all generated layers
  with explicit object budgets.
- Add live Vanilla asset selection for grass surface, trees, bushes, benches,
  lamps and fences.
- Add a compact Vanilla park-template chooser that copies a coherent asset
  palette from an existing park prefab, with deterministic seed selection as
  the automatic fallback.
- Implement the runtime chooser from ordinary buttons and containers instead
  of a native HTML `select`, which is not reliable in CS2's Cohtml runtime and
  can abort the shared game UI render tree.
- Measure the selected path prefab's actual `NetGeometryData` width and keep
  benches and lamps beyond its half-width instead of assuming a four-metre path.
- Terrain-snap benches and lamps without a fixed height offset, rotate their
  prefab forward axis by 90 degrees to match the preview and log their
  `Overridden`/`Hidden` state after materialization.
- Replace fixed four-metre fence sampling with mesh-bounds-based fence mode:
  one deterministic fence prefab is laid continuously end-to-end, with explicit
  six-metre gate gaps and bounded final-piece overlap.
- Treat generated parks as established landscapes and create trees only in the
  old age stage; species, size and placement variation remains deterministic.
- Keep the denser established-park distribution at roughly one mature tree per
  380 m², capped at 90 trees with a ten-metre minimum spacing.
- Build the park surface and all furnishing as individually editable top-level
  Vanilla entities related through persistent `ParkPathMember` data.
- Allow furnishing to be removed and rebuilt independently, or remove paths,
  surface and all furnishing together as one park.

## 0.3.4 – development

- Keep generated Vanilla path nodes, edges and fallback surfaces as top-level
  entities so Move It and Anarchy can access individual elements.
- Replace `Game.Common.Owner` child binding with serializable
  `ParkPathMember` relations and a persistent editable-build record.
- Preserve grouped removal through ParkManager without hiding child entities
  from external editing tools.
- Store a geometry fingerprint and mark externally moved or deleted path
  elements as manually edited instead of silently overwriting them.
- Recover editable build records after loading and retain cleanup support for
  legacy owner-bound 0.3.3 test builds.

## 0.3.3 – development

- Keep action button labels compact and on one line.
- Show the park outline in green while editing and blue in planner mode.

## 0.3.2 – development

- Added ParkingLotTool-style snapping to road edges, building lot sides,
  existing lot geometry, straight directions, guide lines and the zone grid.
- Added compound line intersections so two constraints can be fulfilled at the
  same point, such as curb alignment plus a right angle.
- Added snap priorities compatible with the Vanilla lot-area tool and visual
  feedback with colored cursor rings and dashed guide lines.
- Preserve per-point snap axes through dragging, insertion, deletion and undo.
- Added snap switches using the game's original snap-option icons and bindings.
- Reworked the panel into a compact horizontal tool bar with mode tabs and
  grouped Outline, Path Network and Asset sections, following the interaction
  structure of ParkingLotTool.

## 0.3.1 – development

- Generate six deterministic variants per click and select the best candidate
  using a naturalness score.
- Penalize sharp turns, internal dead ends, overly short or long segments,
  crowded high-degree junctions, poor coverage and unsuitable loop counts.
- Smooth degree-two chains while preserving exact gate and junction nodes.
- Prefer the original visible Vanilla pedestrian path prefab so CS2 renders its
  native asphalt material and connected junction meshes.
- Disable per-edge pavement rectangles when a visible path prefab is available;
  retain the M0.3 invisible-path/surface combination only as a compatibility
  fallback.

## 0.3.0 – development

- Added a typed `ParkPathPlan` with shared nodes, deduplicated edges, gate
  metadata and primary/secondary route classification.
- Added a separate build action; generating a preview no longer mutates the
  game world.
- Added real pedestrian `NetCourse` construction from the generated graph.
- Added visible pavement corridor areas with three- and four-metre widths.
- Added exact shared terrain-height sampling so adjacent courses form stable
  CS2 junctions.
- Added staged Temp-entity materialization, shared ownership and atomic apply.
- Added removal of the complete latest path test build.
- Added build progress and state to the React panel.

## 0.2.0 – development

- Added terrain-based polygon drawing and closing.
- Added projected polygon, point, edge and cursor overlays.
- Added point dragging with invalid-edit rollback.
- Added right-click point deletion and Ctrl+Z undo.
- Added edge hover and double-click edge splitting without edge dragging.
- Added a panel action to reset the working polygon.
- Added the first read-only M0 asset-catalog survey for pathways, surfaces,
  vegetation and name-based park-furniture candidates.
- Added a separate Park Planner mode that locks polygon editing, places
  entrances on hovered edges and generates a deterministic path preview.
- Replaced the radial preview with a polygon-constrained visibility graph:
  the farthest entrance pair forms the main connection and further entrances
  join the existing network by shortest valid routes.
- Added hovered-gate removal with right click in Park Planner mode.
- Replaced the provisional grid/visibility layout with a hybrid deterministic
  pipeline using inward polygon buffers, constrained Delaunay triangulation, a
  triangle dual graph, terminal-metric MST routing, regional coverage links and
  stretch-based loop enrichment.
- Replaced the external geometry runtime with an internal triangulation and
  visibility-roadmap implementation so CS2 can register the mod reliably.
- Added a fresh generation seed per Generate Paths click for controlled layout
  variations in triangulation, sampling, routing and loop selection.
- Added orthogonal gate approaches with a strict ±45-degree fallback cone so
  paths can no longer enter the park at flat or obtuse angles.
- Added atomic loop-quality validation with minimum path separation and branch
  angles to reject extremely narrow, near-parallel path corridors.
- Added live polygon state bindings to the React panel.
- Adapted interaction patterns from ParkingLotTool under GPL-3.0.

## 0.1.0 – development

- Added the standalone CS2 mod entry point.
- Added a minimal C# to React binding.
- Added a toolbar launcher and informational bootstrap panel.
