# Source Generator Rules

Load when generator code or `IIncrementalGenerator` is in scope. Extends Sections 4.1–4.6 in `copilot-instructions.md`.

## Architecture

- Build new generators as `IIncrementalGenerator`.
- Do not use `ISourceGenerator` or walk `Compilation.SyntaxTrees` unless a comment records why incremental is impossible.
- Keep pipelines deterministic and side-effect free.

```csharp
// Wrong: context.Compilation.SyntaxTrees in ISourceGenerator.Execute
// Right: SyntaxProvider / ForAttributeWithMetadataName on IIncrementalGenerator
```

## Target framework

- Generators target `netstandard2.0` (`tech-solution.md`); `tech-csharp.md` applies with this carve-out: APIs newer than `netstandard2.0` are unavailable in the generator project — no `FrozenDictionary`, `SearchValues`, `Vector128` / `Vector256`, `ArgumentNullException.ThrowIfNull` and the other guard helpers, `System.Threading.Lock`, or `required` members without polyfills. Use the `netstandard2.0` equivalents (`Dictionary` built once, explicit `if (x is null) throw new ArgumentNullException(nameof(x));`, `lock` on a private `object`).
- Generated code targets the **consumer’s** framework and may use its APIs only when the generator checks the consumer’s language version or available types first.

## Generated Code

- Emit code that compiles with zero warnings under the consumer’s build settings.
- Fix the generator when generated code warns. Do not suppress in the consumer to hide generator output.
- Apply the same quality rules as handwritten code (Section 4).

## Symbols

- Use `nameof(...)` and `typeof(...)`. Do not hard-code symbol strings.
- Use a fixed literal only when the API requires it; document why `nameof`/`typeof` cannot apply.

## Verification

- Test functional output and incremental recomputation scope per `tech-test.md` + `tech-tunit.md`.
- Rebuild the consumer project in Release and confirm generated files stay warning-free.

## Commands

```bash
dotnet build path/Generator.csproj -c Release
dotnet test path/Generator.Tests.csproj -c Release
```
