# Liquid Sandbox (Combustor MelonLoader mod)

- Game: C:\Program Files (x86)\Steam\steamapps\common\EngineSimulator (Steam app 5047230), MelonLoader 0.7.3, IL2CPP; bindings in MelonLoader\Il2CppAssemblies.
- Build + tests + ZIPs: `powershell -ExecutionPolicy Bypass -File build.ps1` (add `-Install` to copy the DLL into Mods).
- Solver checks only: `dotnet run --project tests/LiquidSandbox.Tests -c Release -- --png work/png`.
- In-game checks: build tools/DevHarness, copy LiquidSandbox.DevHarness.dll into Mods, start the game through Steam
  (`steam://rungameid/5047230`); it writes UserData\LiquidSandboxDev\report.txt and screenshots, then quits.
  Remove the harness DLL and that folder afterwards; it is not part of the release.
- Reverse-engineering aids used: Cpp2IL (MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL) with
  `--output-as isil` and `--use-processor callanalyzer --output-as diffable-cs`.
