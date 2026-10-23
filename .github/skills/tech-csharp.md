# C# Standards

Load when `*.cs` is in scope. C# mechanisms for Section 4. Section order matches `tech-rust.md` and `tech-python.md`.

## Language

- Target `net10.0`, C# 14. Source generators: `netstandard2.0` (`tech-sourcegen.md`).
- Apply `tech-solution.md` build settings (nullable, analyzers, warnings as errors).
- ❗ Reference CSharpStyleChecker `1.*` in every SDK-style project (`tech-solution.md`).
- Line length 160 (`.cs`, `.razor`, `.razor.cs`, `.css`); 4 spaces, no tabs.

## Naming

- ❗ No `var`. Use target-typed `new()` / `[]`.
- ❗ `_PascalCase` for private fields, properties, methods, and statics of the outer type. Exempt: private nested types, explicit interface implementations, local functions, test methods (PascalCase, `tech-tunit.md`).
- Name `CancellationToken` parameters `cancellationToken`. Suffix awaitable methods with `Async`.

## Files and usings

- File-scoped namespaces.
- ❗ Global usings only in `GlobalUsings.cs`, grouped under comment headers. File-local aliases are allowed.
- Sort `System.*` → `Microsoft.*` → third-party → internal.
- ❗ Structure each file with `#region` blocks: fields, lifecycle, public API, private helpers.

## Style

- ❗ Pass `CultureInfo.InvariantCulture` to every parse and format call unless another locale is required.
- Interpolate with `Invariant($"…")` (`global using static System.FormattableString;`). On hot paths use `string.Create(CultureInfo.InvariantCulture, $"…")` or `StringBuilder.Append(CultureInfo.InvariantCulture, $"…")`.

```csharp
string message = Invariant($"Gap rate: {rate:P1}, count={count}");
string text = value.ToString(CultureInfo.InvariantCulture);
if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
{
    // …
}
```

- Brace every control-flow block.
- ❗ One callable exit per line. `?:`, switch expressions, `??`, `??=` only with each arm on its own line.

```csharp
// Wrong
return TryRead(out Item item) ? item : Fallback();
// Right
return TryRead(out Item item)
    ? item
    : Fallback();
```

- Use expression bodies for single expressions and `using` declarations for method-scoped disposables.
- Seal classes not designed for inheritance. Mark non-mutating fields `readonly`.
- Split complex methods into focused helpers.

## Types and API design

- Prefer auto-properties; use a field only when it must be `volatile`.
- Choose the least visibility and the least setter: `get` → `init` → `private set` → `set`.
- Model data as `record`. Use `readonly record struct` for small fixed values, `record` class for large or polymorphic payloads.
- Take `ReadOnlySpan<T>` for data the callee must not store.

```csharp
public readonly record struct GapCount(int Value);
public record ProjectReport(string Path, IReadOnlyList<string> Gaps);
private volatile int _Generation;
public int CountGaps(ReadOnlySpan<char> path) { /* do not store `path` */ }
```

- ❗ Keep read-only interface properties read-only in every implementation:
  - Interface `{ get; }`: implement with `{ get; }` assigned in the constructor, or `init`. Adding `set`, `private set`, or `protected set` is an Error; the compiler does not catch it. A non-readonly positional `record struct` generates `set` and is an Error here.
  - Declare `{ get; init; }` in the interface only when every implementation must accept object initializers. The compiler then rejects `set` (CS8854). Validate in the `init` body.

```csharp
public interface IExportInfo
{
    string Name { get; }
}

// Right
public sealed class ExportInfo(string name) : IExportInfo
{
    public string Name { get; } = name;
}

// Right: the generated init runs only during construction
public sealed record ExportSnapshot(string Name) : IExportInfo;

// Wrong: invented setter
public sealed class MutableExportInfo : IExportInfo
{
    public string Name { get; set; } = "";
}
```

- ❗ Validate parameters at entry with the built-in guards: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty` / `ThrowIfNullOrWhiteSpace`, `ArgumentOutOfRangeException.ThrowIfNegative` / `ThrowIfNegativeOrZero` / `ThrowIfGreaterThan` / `ThrowIfLessThan`, `ObjectDisposedException.ThrowIf`.
- ❗ Nullable is on. Use `!` only with a same-line comment that names the invariant.
- Annotate `Try*` outputs with `[NotNullWhen(true)]`.

```csharp
public ExportId Create(string name, int maxRows)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
    // …
}
```

- ❗ Return expected failures through `Try*`, result types, or error codes — never exceptions. On `false`, give a code, message, or error type, preferably as `out`. Use thread-local last-error only when `out` is impossible; document the accessor, when it is set and cleared, and that it is per-thread.

```csharp
public bool TryPair(string productionPath, out int index, out PairError error);

public readonly record struct PairError(PairErrorCode Code, string Message);
public enum PairErrorCode { None, PathEmpty, NotFound }
```

## Errors and logging

- Throw only for bugs, broken invariants, and failures the caller cannot handle locally (I/O, configuration, external services).
- ❗ Rethrow with `throw;`. Wrap with the original as `innerException`.
- ❗ No empty `catch`. Catch `Exception` only at a top-level boundary (`Main`, request pipeline, worker loop), then log and rethrow or convert. Elsewhere catch specific types and narrow with `when`.
- Let `OperationCanceledException` propagate; do not log it as an error.
- Make custom exceptions `sealed`, derive from the closest framework type, end the name in `Exception`, keep secrets out of messages.
- Inject `ILogger<T>`. Never configure providers or levels in libraries.
- ❗ Log only through `[LoggerMessage]` methods with constant templates and a stable `EventId` (CA1848, CA2254).
- ❗ Never log secrets, tokens, connection strings, or PII.
- Never use `Console` or `Trace` for library errors.

```csharp
internal static partial class ExportLog
{
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Export {ExportName} rejected: {Reason}")]
    public static partial void ExportRejected(ILogger logger, string exportName, string reason);
}

// Wrong: formats and allocates even when Warning is off
_Logger.LogWarning($"Export {name} rejected: {reason}");
// Right
ExportLog.ExportRejected(_Logger, name, reason);
```

## Lints and suppressions

- CSharpStyleChecker makes these compiler errors: line length (CSC001), `var` (CSC002), `_PascalCase` (CSC003), sync-over-async (CSC004), usings outside `GlobalUsings.cs` (CSC005), one exit per line (CSC006), `volatile` read-modify-write (CSC007), target-typed `new()` / `[]` (CSC008), invariant interpolation (CSC009). Review enforces the rest.
- CA1848 and CA2254 are errors under `10-recommended`. Library projects enable CA2007 (`tech-solution.md`).
- ❗ Suppress nothing (`#pragma warning disable`, `SuppressMessage`, `NoWarn`, `WarningsNotAsErrors`) without user approval. Prefer a fix; prefer local over global.
- Name per suppressed id, one comment each: **Id**, **Why** it is inapplicable, **Scope** (smallest that works; restore after `#pragma`), **Why global** (required for `NoWarn`, `WarningsNotAsErrors`, EditorConfig `severity = none`).

```csharp
#pragma warning disable CA1859 // Id: CA1859. Why: {why}. Scope: this method. User approved.
#pragma warning restore CA1859

[SuppressMessage("Performance", "CA1859:...", Justification = "Id: CA1859. Why: {why}. User approved.")]
```

```xml
<!-- Id: CS1591. Why: {why}. Why global: {why not method/file/pragma}. User approved. -->
<NoWarn>$(NoWarn);CS1591</NoWarn>
```

- `IDE1006`: never `NoWarn`; configure the root `.editorconfig` (`tech-solution.md`).

## Comments and docs

- Comments and semantic blocks: Section 4.6.
- Put units in comments, not in names.
- Write XML docs on all members. Add `<exception cref="…">` for thrown exceptions and document omitted validation.
- State thread safety in `<summary>` except for immutable records, readonly structs, DTOs, and enums.

```csharp
public int Pair(string productionPath)
{
    // Immutable after Build. Ordinal-ignore-case keys match MSBuild paths.
    TestProjectIndex index = TestProjectIndex.Build(_Root);

    // --test-project wins. Do not consult the index on that branch.
    if (_OverridePath is not null)
    {
        return RunOne(_OverridePath);
    }

    return index.TryGet(productionPath);
}
```

## Integer arithmetic

- ❗ Check every integer operation for overflow. Use `checked`, widen, or validate where wrap-around is wrong.
- Use `unchecked` only on hot paths with documented safe ranges. Prove bounds at boundaries, not in inner loops.
- Narrow with `checked((int)value)` or `int.CreateChecked(value)`, never a bare cast.

## Security

- **SQL:** bind parameters (`command.Parameters`, Dapper parameter objects, EF Core LINQ or `FromSqlInterpolated`). Never build SQL from strings.
- **Processes:** `UseShellExecute = false` and one `ArgumentList.Add` per argument. Never `cmd /c` or `sh -c` with input.
- **Deserialization:** `System.Text.Json` into concrete types. No type names from input except a closed `[JsonDerivedType]` set. Newtonsoft: `TypeNameHandling.None`.
- **XML:** `DtdProcessing.Prohibit` and `XmlResolver = null` for untrusted input.
- **Regex on input:** set a match timeout or use `RegexOptions.NonBacktracking`, e.g. `[GeneratedRegex(pattern, RegexOptions.None, matchTimeoutMilliseconds: 100)]`.
- **Paths:** resolve, then confirm the result stays under the root.

```csharp
public static bool TryResolveInside(string root, string relative, [NotNullWhen(true)] out string? fullPath)
{
    // GetFullPath normalizes "..". Windows paths are case-insensitive.
    string baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    string candidate = Path.GetFullPath(Path.Join(baseDirectory, relative));
    StringComparison comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    fullPath = candidate.StartsWith(baseDirectory, comparison)
        ? candidate
        : null;
    return fullPath is not null;
}
```

- **Secrets:** read from configuration (user secrets, environment, vault); never commit them. Generate tokens with `RandomNumberGenerator`; compare with `CryptographicOperations.FixedTimeEquals`.
- **Web input:** validate on the server (`tech-blazor.md`, `tech-web.md`).
- **Limits** (Section 4.2): one `sealed record` per boundary with documented defaults and ranges. Validate in a `Try` factory (libraries) or `IValidateOptions<T>` with `ValidateOnStart()` (hosted apps). Host-derived defaults: `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, `Environment.ProcessorCount`. Format and protocol facts are `const`. Comment the security decision at the definition:

```csharp
// Threat: one hostile file exhausts memory. Secure by default: imports come from untrusted senders.
// End user may set 1 MB to available RAM in Settings. Rejected: a fixed constant (cripples large hosts).
// Residual risk: a value near RAM size can still stall the app; the setting warns about it.
public long MaxImportBytes { get; init; } = 1L << 30;
```

## Async

- ❗ Never `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
- ❗ No `async void` except UI event handlers.
- Put `CancellationToken cancellationToken` last on every cancellable method (`= default` only on public APIs). Pass it to every cancellable call.
- ❗ Await with `ConfigureAwait(false)` in library code, including async dispose: `await using (resource.ConfigureAwait(false)) { … }`. Omit it in application code (ASP.NET Core, Blazor components).
- Return `ValueTask` when the result is often synchronous. ❗ Await a `ValueTask` exactly once; call `.AsTask()` to await it more than once.
- Bound external waits with `WaitAsync(timeout, cancellationToken)` or `CancelAfter`.
- Do not wrap synchronous work in `Task.Run` inside libraries.
- Use `SemaphoreSlim.WaitAsync` with `try`/`finally` for exclusion across `await`.

```csharp
public async ValueTask<Export> LoadAsync(string path, CancellationToken cancellationToken = default)
{
    ArgumentException.ThrowIfNullOrEmpty(path);

    // Library code: the async dispose is configured too (CA2007).
    FileStream stream = File.OpenRead(path);
    await using (stream.ConfigureAwait(false))
    {
        Export? export = await JsonSerializer.DeserializeAsync<Export>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return export
            ?? throw new InvalidDataException(Invariant($"Empty export file: {path}"));
    }
}
```

## Thread safety

- ❗ Make a field that several threads access **without a lock** `volatile`, or access it only through `Volatile` / `Interlocked`. Lock-protected fields need neither.
- ❗ Use `Interlocked` for read-modify-write on shared fields; `++`, `--`, `+=` on `volatile` are errors.
- ❗ Pick the cheapest correct primitive (Section 4.3): no sharing → `Interlocked` / `Volatile` → `Lazy<T>` / `LazyInitializer` → `System.Threading.Lock` with a short section (no I/O, `await`, or callbacks inside) → `SemaphoreSlim` / `Channel<T>` only where waiting is the design. `ReaderWriterLockSlim` only for long, read-dominated sections.
- Name threads; set `CultureInfo.InvariantCulture` as thread culture.

```csharp
// Wrong
_Count++;
// Right
Interlocked.Increment(ref _Count);
```

## Performance and allocation

❗ Every allocation is garbage: the GC must trace and reclaim it, and any allocation can trigger a collection that pauses all threads. Avoid allocations on **every** path. On hot paths (per item, per byte after setup) allocate nothing.

### Allocation

- Allocate in this order: stack (`Span<T>`, `stackalloc`) → reuse (recycled objects, `ArrayPool<T>`, `[ThreadStatic]` scratch) → one shared slab → heap.
- Avoid on all paths: LINQ chains, capturing lambdas (use `static` lambdas), boxing (value types as `object` or interface, non-generic collections), `params` arrays, string concatenation in loops, `ToList()` / `ToArray()` copies, temporary collections, `Enum.ToString()`, `string.Split` when a span slice works.
- ❗ On hot paths: no LINQ, no closures, no `IEnumerable` enumerators, no `async` state machines when a synchronous path exists.
- Pre-size collections and builders (`new List<T>(count)`, `new StringBuilder(capacity)`).
- Compute immutable lookups once (`static readonly`, `FrozenDictionary`, `SearchValues<T>`).
- Return `ArrayPool<T>` rentals in `finally`. Use `[ThreadStatic]` for single-thread scratch (never across `await`); pool cross-thread or variable-size buffers.
- Keep arrays below the LOH threshold (~85 KB) or pool them.
- Use `ref struct` only for a hot-path stack reader, builder, or enumerator.
- Use `GC.AllocateUninitializedArray` only when every element is written before read.

```csharp
// Wrong: allocates an array per call and a LINQ iterator
int[] lengths = names.Select(name => name.Length).ToArray();
// Right: rent, fill with a loop, return
int[] lengths = ArrayPool<int>.Shared.Rent(names.Count);
try
{
    for (int index = 0; index < names.Count; index++)
    {
        lengths[index] = names[index].Length;
    }

    Process(lengths.AsSpan(0, names.Count));
}
finally
{
    ArrayPool<int>.Shared.Return(lengths);
}
```

### GC generations

| Generation | Holds | Cost |
|------------|-------|------|
| 0 | New objects | Cheapest; frequent short pauses |
| 1 | Survivors of one gen-0 collection | Much more expensive than gen 0 |
| 2 | Long-lived objects | Full collection; can pause the process |
| LOH | Objects ≳ 85 KB | Collected with gen 2 |

- Let short-lived objects die in gen 0. Do not cache or capture per-item objects “for later” — that promotes them.

### Representation and I/O

- Use `readonly struct` IDs and values; no class identities or string keys on hot paths.
- Store mixed values as tagged unions (inline payload + discriminant), not boxed objects or class hierarchies.
- Link trees by index into chunked arrays; grow chunked stores without copying (single writer, `Volatile` readers).
- Mark presence with bitsets; chain set operations as alias → one clone → in-place mutation.
- Expand nested structure lazily; record presence without materializing children.
- Pass `ReadOnlySpan<T>` / `ReadOnlyMemory<T>`; slice instead of copying.
- Format into caller buffers (`ISpanFormattable`, `IUtf8SpanFormattable`, `u8` literals). Precompute display tables; concatenate only when the text is read.
- ❗ Stream files (`FileStream`, `StreamReader`, `PipeReader`); never `File.ReadAllText` / `ReadAllBytes` on unbounded files. Use `MemoryMappedFile` views for large random access.

### Dispatch, concurrency, compute

- Precompute dispatch (dense arrays, small tables); no per-item dictionary lookup or interface call on the common path.
- Keep `virtual` and interfaces at boundaries; bind concrete delegates or generic struct pipelines inside loops.
- `[MethodImpl(AggressiveInlining)]` on tiny measured hot helpers; `[MethodImpl(NoInlining)]` on throw helpers.
- Source-generate tables and parsers; no reflection on hot paths.
- Hot-path sharing: single writer, `Volatile` publish, `Interlocked` RMW, copy-on-write for rare writes; no `Concurrent*`, `Channel`, or `lock` per item. Publish related arrays as one object; store the value before its flag. Coalesce wakeups. `SpinLock` only for very short sections, never across `await`.
- SIMD (`Vector128` / `Vector256`) with a scalar fallback and the same tests, after profiling shows the loop is hot. `BinaryPrimitives` for endianness.

### Profiling

Load `tech-csharp-profiling.md` when a measurement is needed (Section 3). Never use a profiler that needs admin rights (Section 4.15).

## Console and files

- ❗ Set UTF-8 once at process start in executables with console I/O (Section 4.7).

```csharp
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
```

## Tests

`tech-test.md` + `tech-tunit.md`. Test methods: PascalCase without underscores. Browser journeys: TUnit + `TUnit.Playwright` in `{App}.UiTest` (`tech-playwright.md`).

## Dependencies

New Dependency Protocol: Section 4.7; mechanics: `tech-solution.md`. Check vulnerabilities with `dotnet list package --vulnerable --include-transitive`.

## Copyright

`.cs`, `.razor.cs`: `// {copyright}` from `COPYRIGHT`. Other types: `tech-solution.md`.

## Commands

```bash
dotnet build path/Proj.Tests/Proj.Tests.csproj -c Release
dotnet test path/Proj.Tests/Proj.Tests.csproj -c Release --no-build
dotnet build path/App.slnx -c Release
dotnet list path/App.slnx package --vulnerable --include-transitive
```

Filter tests to the change (`tech-tunit.md`). Verify in Release. PDB or file-in-use errors: concurrent agent (Section 4.14).
