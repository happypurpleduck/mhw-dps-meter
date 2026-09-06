Run with `dotnet run --project tests/PluginRegression` from the repository root.

This dependency-free runner compiles the production trackers and map loader against
simulated game entities. It covers transient and partial memory-read failures, real
despawns, party replacement and rejoining, weapon swaps, and address-map precedence.
Native hooks and plugin callbacks still require in-game verification.
