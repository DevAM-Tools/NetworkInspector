# Source Generator Rules

Load when generator code or `IIncrementalGenerator` is in scope. Extends Sections 4.1–4.6 in `copilot-instructions.md`.

## Architecture

- Use `IIncrementalGenerator` for new generators and refactorings.
- Avoid non-incremental generator patterns unless documented and unavoidable.
- Keep pipelines deterministic and side-effect free.

## Generated Code

- Ensure generated code compiles without warnings.
- Fix generator root cause for generated warnings.
- Apply same quality rules as handwritten code: correctness, security, thread safety, performance, tests, docs.

## Symbols

- Prefer `nameof(...)` and `typeof(...)` over hard-coded symbol strings.
- Use fixed literals only when API requires it; document why `nameof`/`typeof` cannot apply.

## Verification

- Test functional output and incremental recomputation scope.
- Verify generated code stays warning-free under normal build settings.
