# ParkManager

ParkManager is a Cities: Skylines II mod for designing and building custom parks and plazas directly in-game. Draw an outline, choose how the space should work, preview the planned layout, and build it from editable game assets.

## What you can build

### Parks

- Draw and edit a park boundary, then mark its entrances on the outline.
- Generate pedestrian paths that follow the shape of the site, with a choice of narrow or wide paths and a selectable ground surface.
- Plan trees, shrubs, benches, lamps and bins with separate vegetation and furnishing density controls.
- Choose specific Vanilla assets or let ParkManager select a coherent mix automatically; add an optional fence with automatic entrance gaps.
- Generate another layout variation without changing the park outline or its settings.

### Plazas

- Design a plaza around one centerpiece, a mirrored pair, or a set arranged along an axis—or build without a centerpiece.
- Place furniture arrangements around the centerpiece or along the boundary, and tune their spacing with direct controls.
- Create an ordered arrangement of up to five chosen assets, such as bench–bush–bench or lamp–bench–bench–lamp.
- Choose plaza surfaces and an optional fence, then preview the resulting layout before building.

## Designed for in-game editing

ParkManager previews planned placements on the map before construction. Built paths, surfaces, plants and furnishings use normal game entities, so they remain available to the game's editing tools. ParkManager also keeps track of each build so its contents can be removed together. Unfinished outlines and previews belong to the current game session; they are not restored when a save is loaded.

## Development

ParkManager is under active development. See the [changelog](CHANGELOG.md) for recent changes and development history.

### Requirements

- Official Cities: Skylines II modding toolchain
- `CSII_TOOLPATH` and `CSII_USERDATAPATH` configured by that toolchain
- .NET SDK compatible with the installed toolchain
- Node.js 18 or newer

The mod has no third-party runtime assembly dependencies.

### Build and deploy

```powershell
cd UI
npm install
cd ..
dotnet build -c Debug
```

When `UI/node_modules` is present, the C# build also builds and deploys the UI bundle.

### UI tests

The browser mock renders the real UI components without launching the game. To build it and run the Playwright workflow tests:

```powershell
cd UI
npm install
npm run test:ui
```

See [tests/README.md](tests/README.md) for mock and batch-test details.

## Credits

The polygon interaction and entity-transfer pattern are adapted from [ParkingLotTool](https://github.com/algernon-A/ParkingLotTool), licensed under GPL-3.0. ParkManager has no runtime dependency on it. Continuous fence spacing follows the mesh-bounds-based fence mode in [Advanced Line Tool](https://github.com/algernon-A/LineTool-CS2), licensed under Apache-2.0; ParkManager has no runtime dependency on that mod either.
