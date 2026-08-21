# C# Standards

Load when `*.cs` files are in scope. C# mechanisms for Section 4 in `copilot-instructions.md`.

## Language
- Target .NET 10 (`net10.0`), C# 14. Modern idiomatic C#.
- Apply build settings from `tech-solution.md` when build files in scope.
- ❗ Require **CSharpStyleChecker** NuGet `1.*` on every SDK-style C# consumer per `tech-solution.md`.

## Naming
- ❗Never `var`. Use `new()` / `[]` instead of repeating the type;
- ❗`_PascalCase` for private fields, properties, methods, statics on the outer type; exempt inside private nested types, explicit interface implementations, and local functions.

## Files & Usings
- File-scoped namespaces.
- Global usings in `GlobalUsings.cs` only; group by category with comment headers (`tech-solution.md`). File-local type aliases (`using Alias = ...`) are allowed in source files.
- Sort: `System.*` → `Microsoft.*` → third-party → internal.

## Style
- ❗Always pass `CultureInfo.InvariantCulture` as parameter when strings are built or parsed unless another locale is required.
- Always brace control-flow blocks.
- At most one callable exit point per source line; `?:`, switch expressions, `??`, and `??=` are allowed when each arm is on its own line.
- ❗Structure each file with `#region` blocks by content (fields, lifecycle, public API, private helpers).
- Expression-bodied members for simple single expressions.
- `get; init;` on interface read-only properties.
- Never `.Result` or `.Wait()` — `async`/`await` only.
- Avoid exceptions for expected failures. Prefer `Try*` APIs, result types, or error codes — especially in hot paths.
- Provide `Try*` APIs at public boundaries for expected failure paths.
- Evaluate `ValueTask` for often-synchronous `Task` APIs.
- `using` declarations for method-scoped disposables.
- `sealed` on non-inheritable classes.
- `readonly` fields/properties where possible.

## Diagnostics

- ❗Never suppress warnings (`#pragma warning disable`, `SuppressMessage`, `NoWarn`) without user approval.
- When approved: state reason in comment; name rule id; use narrowest scope; restore after scope.

## Comments and XML docs
- Comment purpose, motivation, and design choice before non-trivial logic.
- Document physical units in comments, not variable names.
- Add XML doc on all members.
- Document omitted parameter validation in XML doc with reason and caller guarantees.
- Document thread-safety in XML `<summary>` for non-exempt types.
- Exempt from thread-safety summary: immutable records, readonly structs, plain DTOs, enums.

## Integer arithmetic
- ❗Assess every integer op for overflow/underflow; use `checked`, widen, or validate when wrap-around would be wrong.
- Document proven-safe ranges; use `unchecked` in hot paths only then.
- Prove bounds at boundaries; no redundant overflow checks in inner loops.

## Thread safety
- ❗ Cross-thread shared fields must be declared with the `volatile` keyword.
- ❗ Plain volatile read and write are allowed; increment, decrement, and compound assignment on `volatile` fields are Error-class — use `Interlocked` for atomic read-modify-write.
- Use `Interlocked` when atomic read-modify-write or compare-exchange is required; `Volatile.Read` / `Volatile.Write` remain valid when explicit APIs are preferred.
- Prefer `Interlocked` over `lock` when feasible.

## Performance
Hot path = per-item / per-byte work after setup (build, compile, one-time init). Setup may allocate; the loop must not. Guidance below is for measured hot paths, not CLI, tests, or one-shot setup.

### Allocation
- ❗ Minimize allocations. Reduce GC runs to a minimum.
- Plan allocation order: `Span` / `stackalloc` / `ref struct` → reuse (in-place recycle, `ArrayPool`, `[ThreadStatic]`) → bump/slab shared backing → heap.
- Compare `[ThreadStatic]` vs pooling: affinity, contention, lifetime, reuse safety. `[ThreadStatic]` for single-thread parse/format scratch; never across `await`. Pool for cross-thread or large/variable buffers.
- Return `ArrayPool<T>` rentals in `finally`. Grow a reusable writer; do not `new T[]` per call.
- Keep hot-path arrays below the LOH threshold (~85 KB) unless one large buffer is required; then pool it.
- Recycle hot objects in place instead of allocating per item.
- Prefer bump/slab slices `(buffer, offset)` over per-element heap objects when many values share a lifetime.
- `GC.AllocateUninitializedArray` only when every element is overwritten before read.

### Representation
- Prefer `readonly struct` IDs and values over class identities or string keys. Resolve names once at build into frozen maps (`FrozenDictionary`).
- Store mixed values as compact tagged unions (inline payload + discriminant), not boxed objects or class hierarchies.
- Prefer index links and chunked arrays over object graphs for trees.
- Grow-only chunked stores for dense integer keys (single writer, `volatile` / `Volatile` readers). Never compact/copy the whole store on growth.
- Index presence with compact bitsets/bitmaps. Chain set ops as alias → one clone → in-place mutate; do not scan records for membership.

### Zero-copy and I/O
- Pass `ReadOnlySpan<T>` / `ReadOnlyMemory<T>`; slice, do not copy. Copy only when ownership or lifetime requires it.
- Format into caller buffers (`ISpanFormattable`, `IUtf8SpanFormattable`, UTF-8 spans / `u8` literals). Precompute display lookup tables; defer string concat until observed.
- Match I/O to access: sequential `MemoryMappedFile` / `Span` views, pooled/striped views for random access, stream when the consumer is forward-only. Buffer large sequential writes.

### Dispatch and inlining
- ❗ No LINQ, no heap-capturing closures, no `IEnumerable` enumerators on hot paths. Struct / `ref struct` enumerators; static lambdas; `foreach` over arrays/spans.
- Precompute dispatch at start: dense array for small domains, linear scan of tiny sparse tables. No per-item dictionary or interface vtable on the common path.
- Bind concrete delegates or generic struct pipelines so the JIT can inline. Keep `virtual` / interface at boundaries only.
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on tiny measured hot helpers. `[MethodImpl(MethodImplOptions.NoInlining)]` on throw helpers and rare growth/error paths.
- Source-generate IDs, tables, and parsers. No reflection on hot paths.
- Lazy-expand nested structure; record presence without materializing children.

### Concurrency on the hot path
- Prefer single-writer / multi-reader: `volatile` / `Volatile` publish, `Interlocked` RMW, copy-on-write CAS for rare writes. Do not default to `Concurrent*`, `Channel`, or `lock` on per-item paths.
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
- UTF-8 console encoding in CLI startup.
- Name threads; `CultureInfo.InvariantCulture` for thread culture.
- No console/trace for library error handling.
