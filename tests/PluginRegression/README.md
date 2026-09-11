Run with `dotnet run --project tests/PluginRegression` from the repository root.

This dependency-free runner compiles the production trackers and map loader against
simulated game entities. It covers transient and partial memory-read failures, real
despawns, party replacement and rejoining, weapon swaps, and address-map precedence.
Part capture tests exercise the production memory reader and damage detours with
simulated native call nesting, including unknown parts and hook signature checks.
Actual native patching and game data still require in-game verification.

Hunter action-name checks cover the quest-loading crash's invalid pointer,
non-hunter owners, list bounds and bounded strings. To exercise the actual OS
memory-copy path, run `dotnet run --project tests/NativeMemoryRegression -c Release`
on Windows. On Linux, build that project and run its DLL with the Windows .NET
runtime through Proton. This separate runner deliberately tests inaccessible,
partially readable and freed pages; it requires no game or save data.
