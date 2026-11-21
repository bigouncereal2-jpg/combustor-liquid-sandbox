# Liquid Sandbox for Combustor

> **Work in progress.** This is an early, unfinished mod. It runs and has been checked in game, but
> expect rough edges and changes. Known gap: changing the grid size from the dock was fixed after the
> last full in-game check and has not been rechecked yet.

A MelonLoader mod for the Steam version of Combustor. It adds **Liquid Sandbox** to the main menu,
directly below Sandbox: the regular sandbox scene with Combustor's grid, walls, camera, building
tools, undo, mirror axes, grid sizes, pause/step/speed and saving, but with water simulated instead
of gas. Regular Sandbox and every other mode are unchanged.

Tested with Combustor Steam build **26701522** (app 5047230, Unity 6000.3.2f1, game version 1.0),
MelonLoader **0.7.3** (IL2CPP, net6 runtime), Windows 11.

## New in 1.4.0 (work in progress)

- **Fuels burn as the game's own gas**: gasoline and diesel evaporate into Combustor's **Fuel** gas, so
  they burn exactly like fuel in an engine, through the game's own reactions, igniters and flames (no
  separate liquid fire). As in an engine, the game's combustion needs Oxidizer gas and an igniter or
  700 K. Hot gas above a pool makes it evaporate much faster, so a fire keeps feeding itself until the
  liquid is gone or the oxidizer runs out. Gasoline is volatile; diesel barely evaporates until heated.
  The heat brush boils liquid fuel straight into hot Fuel gas.

## New in 1.2.0 (work in progress)

- Liquid Sandbox is the full Sandbox plus liquids: every tool and every gas works as in Sandbox. Pick
  Water, Gasoline, Diesel or Gas under AMBIENT; the Gas tool pours the picked liquid or sprays gas.

## New in 1.1.0 (work in progress)

- **Three liquids**: water, gasoline and diesel, picked under AMBIENT. Each has its own density, so
  gasoline and diesel float on water and a heavier liquid sinks through a lighter one; gasoline flows
  fastest and diesel slowest.
- **Evaporation**: exposed surfaces evaporate into the empty cell above until that air is saturated
  (gasoline quickly, water slowly, diesel very slowly). Vapour spreads through empty space and shows as
  a faint haze; liquid plus vapour is still kept exactly, so a sealed tank only loses what saturates its
  air. It can be turned off.
- **Pressure**: flow is driven by the density-weighted pressure of the liquid above, and liquid pushed
  out of an opening leaves with a speed set by its depth, so a hole low in a full tank shoots a jet
  that flies further than one from a shallow tank. The inspector shows pressure.
- **Streams**: falling liquid is drawn as a continuous stream (thin trickles as droplets) instead of
  stacked bars; only resting liquid shows a level surface and darkens with depth.

## Features

- **Gravity control** in the AMBIENT panel, in place of the wind control. It is the game's own wind
  direction picker (`GasSimHud.DirectionPicker`): four arrow buttons in screen directions, the
  current one lit, and the "pulls down. F turns it clockwise." line. F turns it clockwise with the
  Wall, Water or Drain tool. Turning it changes how the existing water flows from the next tick;
  nothing is cleared or restarted.
- **Strength slider** (the game's slider style), 0 g to 2 g in steps of 0.05, with Zero g and 1 g
  buttons. Strength changes speed (falling, spreading, levelling), not where water ends up. At 0 g
  water has no down; neighbouring cells exchange water until they are even.
- **Water**: falls along gravity, runs off and around walls, spreads across surfaces, fills
  containers from the bottom and finds a level surface perpendicular to gravity, including across
  connected vessels (U tubes). Walls block it, and sealed containers keep every drop.
- **Tools**: the Gas tool becomes Water (A) and pours with the gas brush's size, shape, mirror axes
  and wheel rate; Vacuum becomes Drain (S). Walls (W) and valves (B) work as usual. Gas-only parts
  and buttons (injectors, heat, igniters, holes, pistons, filters, fans, one-way gates, probe,
  views, ambient gas, open edges, script, engine, reactions) are hidden in this mode.
- **Inspector**: hovering a cell shows its water level, the water depth above it and the total
  water in the grid. The AMBIENT panel also shows the total and has "Drain all water".
- **Rendering**: full cells are drawn into the game's grid texture, darker with depth, before the
  game adds its cursor and part markings. Part-filled cells are drawn on top as a mesh of exact
  rectangles filled from the side gravity points to, so the water surface sits at the real level
  inside each cell at any zoom. At 0 g part-filled cells are tinted by how full they are.
- **Saving**: Esc > Save writes `<name>.engine.water` next to the engine file; loading it in
  Liquid Sandbox restores the water, gravity direction and strength.

## How the simulation works

`src/LiquidSandbox/Simulation/LiquidGrid.cs` has no Unity dependency. Each cell stores an integer
amount of water (65 536 = one full cell). Every change is an integer transfer from one cell to
another, so the solver cannot create or lose water, and amounts never go negative or above a full
cell. Water only enters or leaves through painting, draining, "Drain all water", Reset and loads.

It runs on Combustor's fixed tick (1/64 s), replacing `GasGrid.Step` for the Liquid Sandbox grid,
so the game's pause, single step, speed and per-frame tick budget apply. Each tick:

1. **Falling** – cells move water to the neighbour along gravity while it has room, processed from
   the low side so bodies fall together (2 cells per tick at 1 g, scaled by strength).
2. **Spreading** – water that rests on a wall or on resting full water flows sideways towards the
   lower water column, with momentum, limited so no cell sends more than it holds or receives more
   than it has room for. A resting cell needs 1/8 of a cell to wet a dry neighbour.
3. **Pressure** – each connected body of full cells moves water from its highest surfaces to its
   lowest openings, so connected vessels reach one level.

At 0 g only an even exchange between neighbouring cells runs. Walls painted over water push that
water into the nearest cells with room (through full cells), and changing the grid size moves the
water with the walls; anything that cannot fit anywhere is held and counted until there is room.

Performance on the default 256 x 160 sandbox grid (measured in game, RTX 4060 laptop): about
140 fps with water moving, 0.4 ms of solver time per frame, about 0.6 KB of managed allocation per
frame. The solver itself allocates nothing per tick.

## Building

Needs the .NET SDK 6 or newer (built with 10.0.201) and Combustor with MelonLoader installed and
started once, so `MelonLoader\Il2CppAssemblies` holds the generated game bindings the project
compiles against.

```powershell
# from this folder; builds, runs the solver checks and writes dist\*.zip
powershell -ExecutionPolicy Bypass -File build.ps1
# another install path:
powershell -ExecutionPolicy Bypass -File build.ps1 -GameDirectory "D:\SteamLibrary\steamapps\common\EngineSimulator"
```

Or by hand:

```powershell
dotnet build src\LiquidSandbox\LiquidSandbox.csproj -c Release -p:GameDirectory="C:\...\EngineSimulator"
dotnet run --project tests\LiquidSandbox.Tests -c Release
```

The DLL is `src\LiquidSandbox\bin\Release\net6.0\LiquidSandbox.dll`; copy it to the game's `Mods`.
`dist\LiquidSandbox-1.0.0.zip` contains `Mods\LiquidSandbox.dll` and `INSTALL.txt`, laid out to be
extracted into the folder containing `EngineSimulator.exe`.

## Layout

- `src/LiquidSandbox` – the mod. `Hooks.cs` lists every Harmony hook; `LiquidSession.cs` runs one
  Liquid Sandbox; `LiquidHud.cs`, `WaterView.cs`, `WaterFile.cs`, `TextSwaps.cs` are the panels,
  drawing, save file and renamed tool texts.
- `tests/LiquidSandbox.Tests` – console checks for the solver: exact totals over thousands of
  random ticks with walls, painting and gravity changes; settling flat; a cup; a U tube; turning
  gravity; zero gravity; strength; displacement by walls; grid resizing; speed and allocations.
  `--png <dir>` writes snapshots.
- `tools/DevHarness` – a development-only mod used to check the mod in game (menu click, regular
  Sandbox, gravity picker, F key, pouring, levelling, wall displacement, save/load, timing). It is
  not part of the release.

## Limitations

- One liquid (water). No boiling, reactions, temperature or other liquids.
- Parts that need gas to work are not available in this mode. Valves open and close by hand; their
  sensor programs do not run.
- Share codes and Workshop engines carry walls only. Water files are tied to the grid size saved.
- The mod hooks Combustor's code by name. After a game update it checks every hook at start and,
  if one is missing, turns itself off and logs why.

## License

Open source under the MIT License, see [LICENSE](LICENSE). Combustor and MelonLoader are not included and keep their own licenses.
