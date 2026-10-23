# C# Profiling

Load with `tech-csharp.md` when profiling is requested, a step optimizes a hot path, a `Verify` or `Debug` needs a measurement, or a finding claims a measured delta (Section 3).

❗ Use no profiler that needs admin rights (Section 4.15): no ETW tools (PerfView kernel mode, WPR, xperf).

## dotnet-trace (every OS)

- Use `dotnet-trace`; it reads the runtime’s EventPipe in-process and needs no rights.
- Install it only after asking, as a local tool: `dotnet tool install dotnet-trace --tool-path .tools`.
- Profile Release builds only. Trace the built app, not `dotnet run` (that traces the host). Pass a Windows path to the app on Windows.

```bash
dotnet build path/App.csproj -c Release
dotnet-trace collect --profile dotnet-sampled-thread-time --format Speedscope -o hotpath.nettrace -- path/bin/Release/net10.0/App <app args>
dotnet-trace collect --profile gc-verbose -o allocations.nettrace -- path/bin/Release/net10.0/App <app args>
dotnet-trace collect -p <pid> --profile gc-verbose --duration 00:00:30 -o allocations.nettrace
```

- `dotnet-sampled-thread-time`: managed stacks at ~100 Hz — where time goes. Open the `.speedscope.json` at [speedscope.app](https://www.speedscope.app).
- `gc-verbose`: collections and sampled allocations per type — who allocates, what gets promoted.
- Do not use `cpu-sampling` or `thread-time`; they are kernel profiles for `collect-linux`.
- Attach with `-p <pid>` only to processes of the same user.
- Read `GC.CollectionCount` only in a benchmark, never in production code.
