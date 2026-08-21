# Rust Standards

Load when `*.rs`, `Cargo.toml`, or `Cargo.lock` is in scope. Rust mechanisms for Section 4 in `copilot-instructions.md`. Idiomatic Rust.

## Language
- Edition 2024. Modern idiomatic Rust.
- `clippy -D warnings`. `rustfmt`. Treat warnings as errors.
- ❗ Require `rustfmt.toml` at repo root with `max_width = 160`.
- Public signatures fully typed. Locals may infer when the type is obvious; annotate when inference hides a conversion or a heap type.

## Naming
- `snake_case` functions, methods, modules, locals, fields.
- `PascalCase` types and traits.
- `SCREAMING_SNAKE_CASE` consts and statics.
- Name by role. No `foo2`, `foo_impl`, `data2`.

## Files & modules
- One cohesive module per file. `foo.rs` plus `foo/` for submodules. No `mod.rs` unless the tree already uses it.
- `mod` declarations in the parent. No glob re-exports (`pub use foo::*`) in public API.
- Keep crates small; split at API boundaries, not at every type.

## Style
- ❗ Parse and format strings with explicit UTF-8 and well-defined formats. Do not depend on process locale.
- At most one callable exit per source line (`return`, `?`, `break` with value, diverging macro). Match arms with `?` each on their own line.
- Prefer expression tails (`if`/`match` as expressions). No `let mut x; if { x = } else { x = }`.
- Never `.unwrap()` / `.expect()` / `panic!` in library paths except documented invariants. `# Panics` on every public item that can panic.
- Never `block_on`, `Handle::join`, or `futures::executor` in async library paths — `.await` only.
- Return `Result<T, E>` for expected failure. `Option` for absence. No sentinel values.
- Public `E`: dedicated error type; implement `std::error::Error + Send + Sync + 'static`.
- `mut` only when mutation is required.
- `#[non_exhaustive]` on public enums and structs that may grow.
- Least visibility: default private; `pub(crate)` before `pub`.

## Diagnostics
- ❗ Never suppress warnings (`#[allow]`, `#![allow]`, `expect`) without user approval.
- When approved: state reason in comment; name lint id; use narrowest scope.

## Comments and rustdoc
- Comment purpose, motivation, and design choice before non-trivial logic.
- Document physical units in comments, not variable names.
- `///` on every public item. `//!` crate and module docs.
- Document `# Errors`, `# Panics`, `# Safety`, and `# Thread Safety` when they apply.
- Document omitted parameter validation in rustdoc with reason and caller guarantees.

## Integer arithmetic
- ❗ Assess every integer op for overflow/underflow. Use `checked_*`, `saturating_*`, `wrapping_*`, widen, or validate when wrap-around would be wrong.
- Do not rely on debug-only overflow panics for release correctness.
- Document proven-safe ranges; use wrapping APIs in hot paths only then.
- Prove bounds at boundaries; no redundant overflow checks in inner loops.

## Thread safety
- ❗ Shared mutable state: `Mutex` / `RwLock` / `Atomic*` only. Document lock order and poison handling.
- Explicit `Ordering` on atomics. Do not default to `SeqCst` without reason.
- Bound public APIs with `Send` / `Sync` only when the type guarantees it.
- ❗ No `unsafe impl Send` / `Sync` without a documented invariant.
- Document thread-safety in rustdoc `# Thread Safety` for non-exempt types.
- Exempt: types with no interior mutability that are automatically `Send + Sync` (plain data).
- Prefer atomics over mutexes when feasible.

## Safety
- Minimize `unsafe`. Each `unsafe` block: `// SAFETY:` invariant covering aliasing, lifetimes, validity, and `Send`/`Sync`.
- Validate at FFI and I/O boundaries before any `unsafe`.

## Performance
- Minimize heap allocations and `clone`. No GC — treat `String`, `Vec`, `Box`, `clone`, and `collect` as costs.
- For hot paths, plan allocation order: stack/borrows, then reuse buffers, then heap.
- Return rented/reused buffers; do not leak scratch `Vec`s.
- Do not `.collect()` or build intermediate `Vec`/`String` on hot paths without measured need. Keep iterators lazy.
- Avoid capturing closures that allocate on hot paths; prefer function items or `impl Fn` without heap capture.
- Provide SIMD plus scalar fallback for compute-heavy code.
- Maximize monomorphized inlining in measured hot paths; avoid `dyn` on those paths.

## Tests
- `cargo test`. Unit tests in `#[cfg(test)]` next to the code under test. Integration tests in `tests/`.
- Cover happy path, `Err` variants, empty/min/max, off-by-one, collections 0/1/2, concurrency.
- ❗ Every public or internal API exit path (`Ok`, `Err`, `?`, documented panic) must have a test before release.
- Deterministic; no `std::thread::sleep`.
- One logical assertion per test.
- Test names: `method_scenario_expected_result`.
- Separate Arrange, Act, Assert with blank lines.
- Assert `Err` explicitly — no `unwrap` on error-path tests.
- Prefer real deterministic implementations over mocks. Mock only external or non-deterministic dependencies.
- Windows/Linux/macOS, x64/ARM64.

## Dependencies
Intent: Section 4.7. Never add a crate without user approval.

- Never `cargo add` or edit `[dependencies]` / `[workspace.dependencies]` without user approval.
- Ask in Grill-Me when plan may need new crates.
- Present: crate name, purpose, license (`MIT` / `Apache-2.0` / BSD-like), alternatives.
- After approval: add to `[workspace.dependencies]` first when a workspace exists, then the crate’s `Cargo.toml`.
- Commit `Cargo.lock`.

## Copyright
- `.rs`: `// {copyright}` — exact text from `COPYRIGHT`.

## Formatting
- Limit line length to 160 (`rustfmt.toml` `max_width = 160`).
- 4-space indent; no tabs. Let `rustfmt` apply.
- Implement `Drop` for resource owners. No manual forget except documented intent.
- Decompose complex functions into focused helpers.
- UTF-8 for CLI I/O.
- Name threads.
- No `println!` / `eprintln!` / `dbg!` for library error handling.

## Verify
- `cargo fmt --check`
- `cargo clippy --all-targets --all-features -- -D warnings`
- `cargo test --release`
