# C# Standards

Load when `*.cs` files are in scope. C# mechanisms for Section 4 in `copilot-instructions.md`.

## Language
- Target .NET 10 (`net10.0`), C# 14. Modern idiomatic C#.
- Apply build settings from `tech-solution.md` when build files in scope.
- ❗ Require **CSharpStyleChecker** NuGet `1.*` on every SDK-style C# consumer per `tech-solution.md`.

## Naming
- ❗Never `var`. Use `new()` / `[]` instead of repeating the type.
- ❗`_PascalCase` for private fields, properties, methods, statics on the outer type; exempt inside private nested types, explicit interface implementations, local functions, and **test methods** (`tech-tunit.md`: PascalCase).

## Files & Usings
- File-scoped namespaces.
- Put global usings in `GlobalUsings.cs` only; group by category with comment headers (`tech-solution.md`). File-local type aliases (`using Alias = ...`) are allowed in source files.
- Sort: `System.*` → `Microsoft.*` → third-party → internal.

## Copyright
- `.cs` and `.razor.cs`: `// {copyright}` — exact text from `COPYRIGHT`. Other file types: `tech-solution.md` when that skill is loaded.

## Style
- ❗Always pass `CultureInfo.InvariantCulture` as parameter when strings are built or parsed unless another locale is required.
- Always brace control-flow blocks.
- At most one callable exit point per source line; `?:`, switch expressions, `??`, and `??=` are allowed when each arm is on its own line.
- ❗Structure each file with `#region` blocks by content (fields, lifecycle, public API, private helpers).
- Use expression-bodied members for simple single expressions.
- Use `get; init;` on interface read-only properties.
- Never `.Result` or `.Wait()` — `async`/`await` only.
- Do not throw for expected failures. Use `Try*` APIs, result types, or error codes — especially on hot paths.
- Put `Try*` on public boundaries for expected failure. On `false`, give the caller a **code**, **message**, or **error type** — prefer an `out` parameter. Thread-local last-error is allowed only when the signature cannot take `out`; then XML must name the accessor, when it is set and cleared, and that it is per-thread.

```csharp
public bool TryPair(string productionPath, out int index, out PairError error);

public readonly record struct PairError(PairErrorCode Code, string Message);
public enum PairErrorCode { None, PathEmpty, NotFound }

// TLS last-error: only when `out` cannot be added. Document on TryPair and on LastError.
public bool TryPair(string productionPath, out int index);
[ThreadStatic]
private static PairError _LastError;
/// <summary>Per-thread last error. Set when TryPair returns false; cleared at the start of the next TryPair on this thread. Do not read from another thread.</summary>
public static PairError LastError => _LastError;
```

- Return `ValueTask` when the API is often synchronous.
- Use `using` declarations for method-scoped disposables.
- Seal non-inheritable classes.
- Mark fields and properties `readonly` when they do not mutate.

## Data types

- Prefer auto-properties. Use an explicit field only when it must be `volatile` (`volatile` is not valid on a property).
- Choose the least visibility and the least setter the use case needs: `get` → `init` → `private set` → `set`.
- Model pure data (DTOs, snapshots, query rows) as `record` or `record struct`.
- Use `readonly record struct` when the shape is small, fixed, and value-like (no inheritance, no reference identity).
- Use `record` (class) when the payload is large, polymorphic, or a shared graph.
- Pass stack-only views as `ref struct` parameters (`Span` / `ReadOnlySpan`) when the data must not be stored (fields, collections, `async`, boxing). Not a default type — see Performance for hot-path use.

```csharp
public readonly record struct GapCount(int Value);
public record ProjectReport(string Path, IReadOnlyList<string> Gaps);
public int Count { get; init; }
internal string Path { get; private set; }
private volatile int _generation;
public int CountGaps(ReadOnlySpan<char> path) { /* do not store `path` */ }
```

## Diagnostics

- ❗Never suppress warnings (`#pragma warning disable`, `SuppressMessage`, `NoWarn`, `WarningsNotAsErrors`) without user approval.
- Prefer a code fix. Prefer a local `#pragma` / `SuppressMessage` over `NoWarn`.
- When approved: use the matching template below. A suppression without those fields is incomplete.

Every suppressed **id** must name:

1. **Id** — the compiler / analyzer code (`CS1591`, `CA1859`, …). One comment per id. Do not lump several ids into one reason.
2. **Why** — why this warning is wrong or inapplicable here.
3. **Scope** — the smallest place that is still correct (method, type, file, project, repo). Restore after a `#pragma` scope.
4. **Why global** — **required for `NoWarn` / `WarningsNotAsErrors` / EditorConfig `dotnet_diagnostic.*.severity = none`.** Why a local suppress cannot work.

### Local

```csharp
#pragma warning disable CA1859 // Id: CA1859. Why: {why}. Scope: this method. User approved.
#pragma warning restore CA1859
```

```csharp
[SuppressMessage("Performance", "CA1859:...", Justification = "Id: CA1859. Why: {why}. User approved.")]
```

### Global (`Directory.Build.props` or `.csproj`)

MSBuild property details: `tech-solution.md`. Put the comments immediately above the property. Append `$(NoWarn);` so earlier ids stay.

```xml
<!-- Id: CS1591. Why: {why this warning is inapplicable}. Why global: {why not method/file/pragma}. User approved. -->
<!-- Id: CA1707. Why: {why this warning is inapplicable}. Why global: {why not method/file/pragma}. User approved. -->
<NoWarn>$(NoWarn);CS1591;CA1707</NoWarn>
```

### IDE naming (`IDE1006`)

Do not use `NoWarn`. Root `.editorconfig`: `tech-solution.md` (IDE / code-style).

## Comments and XML docs
- Comment purpose, motivation, caveats, and design choice before non-trivial logic. State why; never restate obvious syntax.
- Split non-trivial method bodies into semantic blocks. Separate blocks with one blank line (two allowed). Lead each block with intent and what to watch for.
- Document physical units in comments, not variable names.
- Add XML doc on all members.
- Document omitted parameter validation in XML doc with reason and caller guarantees.
- Document thread-safety in XML `<summary>` for non-exempt types.
- Exempt from thread-safety summary: immutable records, readonly structs, plain DTOs, enums.

```csharp
public int Pair(string productionPath)
{
    // Immutable after Build. Ordinal-ignore-case keys match MSBuild paths.
    TestProjectIndex index = TestProjectIndex.Build(_root);

    // --test-project wins. Do not consult the index on that branch.
    if (_overridePath is not null)
    {
        return RunOne(_overridePath);
    }

    return index.TryGet(productionPath);
}
```

## Integer arithmetic
- ❗Assess every integer op for overflow/underflow; use `checked`, widen, or validate when wrap-around would be wrong.
- Document proven-safe ranges; use `unchecked` in hot paths only then.
- Prove bounds at boundaries; no redundant overflow checks in inner loops.

## Thread safety
- ❗ Cross-thread shared fields must be declared with the `volatile` keyword.
- ❗ Plain volatile read and write are allowed; increment, decrement, and compound assignment on `volatile` fields are Error-class — use `Interlocked` for atomic read-modify-write.
- Use `Interlocked` when atomic read-modify-write or compare-exchange is required; `Volatile.Read` / `Volatile.Write` remain valid when explicit APIs are preferred.
- Use `Interlocked` instead of `lock` when a single word is enough.

## Performance
Performance is a feature. Hot path = per-item / per-byte work after setup (build, compile, one-time init). Setup may allocate; the loop must not. Guidance below is for measured hot paths, not CLI, tests, or one-shot setup.

### Allocation
- ❗ Minimize allocations. Reduce GC runs to a minimum.
- Plan allocation order: `Span` / `stackalloc` → reuse (in-place recycle, `ArrayPool`, `[ThreadStatic]`) → bump/slab shared backing → heap.
- `ref struct` is a **special-case** tool (stack-only; cannot box, await, or store on the heap). Use it when a measured hot path needs a stack enumerator, reader, or builder — not as a default for types, APIs, or every loop.
- Compare `[ThreadStatic]` vs pooling: affinity, contention, lifetime, reuse safety. `[ThreadStatic]` for single-thread parse/format scratch; never across `await`. Pool for cross-thread or large/variable buffers.
- Return `ArrayPool<T>` rentals in `finally`. Grow a reusable writer; do not `new T[]` per call.
- Keep hot-path arrays below the LOH threshold (~85 KB) unless one large buffer is required; then pool it.
- Recycle hot objects in place instead of allocating per item.
- Use bump/slab slices `(buffer, offset)` when many values share a lifetime. Do not allocate a heap object per element.
- `GC.AllocateUninitializedArray` only when every element is overwritten before read.

### GC generations

The GC is generational. Cost rises sharply as objects live longer.

| Generation | What lives there | Cost |
|------------|------------------|------|
| **0** | New objects; most die here | Cheapest. Frequent, short pauses. |
| **1** | Survived one gen-0 collection | **Substantially more expensive than gen 0.** Treat survival into gen 1 as a smell on a hot path. |
| **2** | Long-lived / promoted again | Most expensive. Full GC; can pause the process. |
| **LOH** | Objects ≳ 85 KB | Collected with gen 2. Do not allocate large arrays per item. |

- Design so hot-path objects **die in gen 0**. A gen-0 alloc that never survives is cheap; the same alloc that is still reachable at the next collection becomes gen-1 memory and hurts more.
- Do not cache or capture per-item objects “for later” on a hot path — that is how gen 0 becomes gen 1.
- Use `Span` / stackalloc / pooled or recycled buffers so per-item work has nothing to promote. Use `ref struct` only in the special case above.

### Profiling

Profile only when the user asks or the step is hot-path optimization. Always **Release**. Do not profile Debug.

Use `dotnet-trace`. If it is missing, ask before `dotnet tool install -g dotnet-trace`. No wrapper script.

```bash
dotnet build path/App.csproj -c Release
dotnet-trace collect --profile cpu-sampling --format speedscope --output hotpath.nettrace -- dotnet run -c Release --no-build --project path/App.csproj -- <app args>
dotnet-trace collect -p <pid> --profile gc-verbose --duration 00:00:30 -o hotpath.nettrace
dotnet-trace convert hotpath.nettrace --format speedscope
```

- `cpu-sampling`: where time goes. Open the speedscope file (or the `.nettrace` in Visual Studio).
- `gc-verbose`: allocations and GC. Then inspect gen-0 / gen-1 / gen-2 / LOH in the trace. `GC.CollectionCount(1)` / `CollectionCount(2)` only in a bench for that session — not in production.

### Representation
- Use `readonly struct` IDs and values. Do not use class identities or string keys on hot paths. Resolve names once at build into frozen maps (`FrozenDictionary`).
- Store mixed values as compact tagged unions (inline payload + discriminant), not boxed objects or class hierarchies.
- Link trees by index into chunked arrays. Do not pointer-chase object graphs on hot paths.
- Grow-only chunked stores for dense integer keys (single writer, `volatile` / `Volatile` readers). Never compact/copy the whole store on growth.
- Index presence with compact bitsets/bitmaps. Chain set ops as alias → one clone → in-place mutate; do not scan records for membership.

### Zero-copy and I/O
- Pass `ReadOnlySpan<T>` / `ReadOnlyMemory<T>`; slice, do not copy. Copy only when ownership or lifetime requires it.
- Format into caller buffers (`ISpanFormattable`, `IUtf8SpanFormattable`, UTF-8 spans / `u8` literals). Precompute display lookup tables; defer string concat until observed.
- Match I/O to access: sequential `MemoryMappedFile` / `Span` views, pooled/striped views for random access, stream when the consumer is forward-only. Buffer large sequential writes.
- Stream files with `FileStream` / `StreamReader` / `PipeReader` (Section 4.4). Do not `File.ReadAllText` / `ReadAllBytes` on unbounded files.

```csharp
await using FileStream stream = File.OpenRead(path);
```

### Dispatch and inlining
- ❗ No LINQ, no heap-capturing closures, no `IEnumerable` enumerators on hot paths. `foreach` over arrays/spans; static lambdas. A `ref struct` enumerator only when a heap enumerator is actually on the measured hot path.
- Precompute dispatch at start: dense array for small domains, linear scan of tiny sparse tables. No per-item dictionary or interface vtable on the common path.
- Bind concrete delegates or generic struct pipelines so the JIT can inline. Keep `virtual` / interface at boundaries only.
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on tiny measured hot helpers. `[MethodImpl(MethodImplOptions.NoInlining)]` on throw helpers and rare growth/error paths.
- Source-generate IDs, tables, and parsers. No reflection on hot paths.
- Lazy-expand nested structure; record presence without materializing children.

### Concurrency on the hot path
- Use single-writer / multi-reader on hot paths: `volatile` / `Volatile` publish, `Interlocked` RMW, copy-on-write CAS for rare writes. Do not default to `Concurrent*`, `Channel`, or `lock` on per-item paths.
- Coalesce wakeups (atomic flags + wait handle) instead of per-signal queues.
- Short `SpinLock` only for brief exclusive mutation; kernel wait when the pause can be long. Never `await` while held; always `try`/`finally`.
- Publish related arrays as one object. Store value, then flag (release), for optional fields.

### Compute
- SIMD (`Vector256` / `Vector128`) plus scalar fallback for bulk bitwise, checksum, fill, and scan/escape work.
- `SearchValues<T>` for multi-value scans in parsers.
- Endian via `BinaryPrimitives` / span readers; no temporary reverse buffers.

## Formatting
- Limit line length to 160 in `.cs`, `.razor`, `.razor.cs`, `.css`.
- 4-space indent; no tabs.
- Follow `IDisposable` / `IAsyncDisposable` patterns.
- Decompose complex methods into focused helpers.
- ❗ Executables with console I/O: set UTF-8 **once** at process start (Section 4.7). Required on Windows.

```csharp
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
```
- Name threads; `CultureInfo.InvariantCulture` for thread culture.
- No console/trace for library error handling.

## Tests
`tech-test.md` + `tech-tunit.md`. Test methods: PascalCase without underscores (`PairEmptyRepoReturnsNone`).
- ❗ Require 100% exit-path coverage on every public or internal API before release. Branch coverage is not the gate. Run ExitPointGaps per `tech-tunit.md`.
- C# browser journeys: TUnit + `TUnit.Playwright` in `{App}.UiTest` (`tech-playwright.md`). Not NUnit, xUnit, or MSTest.

## Commands

```bash
dotnet build path/Proj.csproj -c Release
dotnet test path/Proj.Tests.csproj -c Release --no-build
dotnet build CopilotAIWorkflow.slnx -c Release
```

Use Release/optimized for Verify. Filter a single test when debugging. PDB / file-in-use errors: concurrent agent (Section 4.14), not a logic bug. Do not add a wrapper script without approval.
