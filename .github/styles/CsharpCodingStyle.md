<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# C# Coding Style

> Extends the global engineering rules in [copilot-instructions.md](../copilot-instructions.md). Read both.
> Global policies are not repeated here unless a C#-specific mechanism must be named explicitly.

## Language and Framework

- Target C# 14 with the latest .NET LTS.
- Use modern, idiomatic C# features; avoid obsolete or verbose patterns.

## Naming

| Element | Convention | Example |
|---|---|---|
| Public / internal type | PascalCase | `NetworkPacket` |
| Public / internal member | PascalCase | `ParseFrame()` |
| Private member (field, property, method; including static) | `_PascalCase` | `_Buffer`, `_Validate()` |
| Interface | `I` + PascalCase | `IPacketSource` |
| Type parameter | `T` + PascalCase | `TPacket` |
| Local variable | camelCase | `packetCount` |
| Parameter | camelCase | `bufferSize` |
| Constant | PascalCase | `MaxRetries` |

## File Structure

- File-scoped namespaces (`namespace Foo;`).
- One type per file; small, closely related types may share a file.
- Sorted `using` directives in individual files; system namespaces first. Any `using` that applies to nearly all files in a project must be a global using, not per-file.

### Global Usings

- Declare all `global using` directives in a single file named `GlobalUsings.cs` at the root of each project.
- Do not add `using` directives to individual `.cs` files for namespaces that are already covered by a global using.
- Keep `GlobalUsings.cs` sorted: `System.*` namespaces first, then `Microsoft.*`, then third-party, then internal namespaces.
- Do not add a namespace declaration to `GlobalUsings.cs`; it contains only `global using` statements.

## Solution-Level Settings

Manage as many settings as possible centrally so they are inherited consistently by all projects without repetition.

### Directory.Build.props

Place a `Directory.Build.props` file at the repository root. All `.csproj` files inherit its properties automatically.

- Set `ImplicitUsings` to `enable`; in addition, declare project-specific assembly-wide directives in `GlobalUsings.cs`.
- Set `TreatWarningsAsErrors` to `true` at solution level; never override it to `false` in individual projects.
- Individual `.csproj` files must override `Directory.Build.props` properties only when there is a genuine, documented reason.

### Central Package Management (Directory.Packages.props)

Use [NuGet Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management) to manage all package versions in one place.

- Place `Directory.Packages.props` at the repository root.
- Enable CPM by setting `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`.
- Declare every package version once with `<PackageVersion Include="..." Version="..." />`.
- In `.csproj` files, reference packages with `<PackageReference Include="..." />` — **omit the `Version` attribute**.
- Never specify a version in a `<PackageReference>` when CPM is enabled; if a project genuinely needs a different version, use `<PackageVersion>` override with an explicit comment explaining the divergence.

## Code Style

- No `var`; use explicit types or collection expressions (`[]`) when applicable.
- Always use curly braces for single-line `if`/`for`/`while` bodies (except expression-bodied members).
- Expression-bodied members for simple getters, setters, and single-expression methods.
- Primary constructors for simple data classes.
- In interfaces, read-only properties must use `get; init;`.
- `async`/`await` for all asynchronous workflows; never `.Result` or `.Wait()`.
- `using` declarations (not `using` statements) for disposables whose lifetime is scoped to a method.
- `sealed` on classes not designed for inheritance.
- `readonly` on fields wherever possible.
- Minimal visibility for all types and members; private by default.
- Prefer `static` lambdas and anonymous functions whenever no closure variables are captured; capturing lambdas always allocate a delegate and a closure object.
- Use regions to structure code files into logical sections, especially in larger files.

## Documentation

- XML doc comments on all public and private members.
- Every type must document its thread-safety expectations in its XML summary.
- Inline comments for complex logic, algorithms, or non-obvious decisions.
- Describe algorithm and data-structure choices with rationale — explain what was chosen and why.
- Comments reflect the current state; update them whenever the code changes.

## Thread Safety

- Every type must explicitly state in its XML `<summary>` whether it is thread-safe or requires caller synchronization — **unless it is an exempt data type** (see below).
- **Exempt types** — the following types carry no thread-safety documentation obligation because their structure makes the question moot:
  - Immutable `record` and `readonly record struct` types with only `get; init;` properties.
  - `readonly struct` types.
  - Plain data containers (DTOs, request/response objects, view models) whose sole purpose is holding data: classes or records with only auto-properties (`get; set;` or `get; init;`) and no constructors, methods, or background operations that mutate shared state.
  - `enum` types.
- **Non-exempt types** — all other types (services, repositories, caches, state containers, types with background operations) must document thread safety in their `<summary>`.
- Fields that require volatile access must be documented in their XML `<summary>` stating: *"Volatile field — all access sites must use `Volatile.Read` / `Volatile.Write` or `Interlocked`"*. Add a `// volatile` annotation comment at the declaration as an additional visual marker.
- Use `Volatile.Read` / `Volatile.Write` and `Interlocked` for shared mutable fields at every access site — both reads and writes.
- A field with volatile semantics must never be accessed with a plain read or write.
- Prefer lock-free approaches using `Interlocked` before reaching for `lock`.
- Identify and guard against TOCTOU, async-interleaving, and partial-state publication risks.

## Performance and Allocations

- Reduce allocations to a minimum throughout the codebase — not only in hot paths. Every unnecessary allocation is a tax on the GC.
- Use `ReadOnlySpan<T>`, `Span<T>`, `ReadOnlyMemory<T>`, `Memory<T>` to avoid heap allocations when working with buffers and strings.
- Use `ArrayPool<T>` or dedicated object pools for short-lived buffers; always return rented arrays in a `finally` block.
- Prefer `static` lambdas to avoid closure allocations (see Code Style).
- Avoid LINQ in hot paths; LINQ allocates enumerators, closures, and intermediate collections.
- Apply `[MethodImpl(MethodImplOptions.AggressiveInlining)]` to small, performance-critical methods.
- Provide SIMD-accelerated implementations for compute-heavy paths; always include a scalar fallback.
- Evaluate `[ThreadStatic]` scratch buffers versus pooling for per-thread hot-path state and document the trade-off (thread affinity, pool pressure, buffer-reuse safety).
- Maximising throughput and minimising latency in hot paths is critical; profile before and after any significant change.

## Formatting

- Line length: 160 characters maximum (global rule; enforced by `.editorconfig`).
- Indentation: 4 spaces, no tabs (enforced by `.editorconfig`).

## Testing

- Use TUnit as the test framework.
- Place tests in a project that mirrors the production project's folder and namespace structure.
- Name test classes `<TypeUnderTest>Tests`.
- Name test methods `<Method>_<Scenario>_<ExpectedResult>` — e.g., `Parse_NullInput_ThrowsArgumentNullException`.
- Mark every test method with `[Test]`; all test methods must be `async Task` — TUnit is async-first.
- Structure every test with **Arrange / Act / Assert**, each block separated by a blank line. Use `await Assert.That(actual).Is...` for assertions.
- Each test verifies exactly one logical outcome; multiple unrelated assertions in one test are a smell.
- Use `[Arguments(...)]` (equivalent to xUnit `[InlineData]`) or `[MethodDataSource(nameof(...))]` (equivalent to `[MemberData]`) for data-driven scenarios; enumerate every corner case explicitly as a separate `[Arguments]` row rather than relying on a single representative value.
- Corner cases include: `null` and empty inputs, maximum and minimum values of all numeric types used, off-by-one conditions, strings at exactly the length limit, collections with zero, one, and two elements, concurrent access, and any value that straddles a branch boundary.
- Mock only external or non-deterministic dependencies (I/O, network, clock); prefer real implementations otherwise.
- Test thread-safety claims: exercise concurrent access for every type documented as thread-safe.
- For thread-safety tests, verify that every volatile field is accessed exclusively via `Volatile.Read` / `Volatile.Write` / `Interlocked` — write tests that expose races if plain reads or writes are used.
- Never use `Thread.Sleep` in tests; use deterministic synchronization (`ManualResetEventSlim`, `SemaphoreSlim`, `TaskCompletionSource`, etc.).
- Every public API error path, precondition violation, boundary value, and corner case must have a dedicated test; covering only the happy path is not acceptable.

## Patterns and Conventions

- Follow the standard `IDisposable` / `IAsyncDisposable` pattern.
- Favour maintainable, readable code: extract helper methods and decompose complex methods into smaller, focused functions.
- CLI projects must set console encoding to UTF-8 on startup.
- Name created threads appropriately and set their culture to `CultureInfo.InvariantCulture`.
- Libraries must not write to console or trace for error handling; use try-pattern APIs, result types, or exceptions.
- Do not encode physical units in variable names; put the unit in a comment instead.
- No `#if DEBUG` or `Debug.Assert()` behavioral differences between debug and release builds.
- Use `using` for resource management rather than manual `try`/`finally` dispose blocks.
