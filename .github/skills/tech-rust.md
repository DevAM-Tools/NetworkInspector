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
- Write `if`/`match` as expressions. Do not `let mut x; if { x = } else { x = }`.
- Never `.unwrap()` / `.expect()` / `panic!` in library paths except documented invariants. `# Panics` on every public item that can panic.
- Never `block_on`, `Handle::join`, or `futures::executor` in async library paths — `.await` only.
- Return `Result<T, E>` for expected failure. Return `Option` for absence. Do not use sentinels or `unwrap` on those paths.

```rust
pub(crate) fn pair(path: &Path) -> Result<usize, PairError>
```

- Give public `E` a dedicated error type; implement `std::error::Error + Send + Sync + 'static`.
- Use `mut` only when mutation is required.
- Mark public enums and structs that may grow `#[non_exhaustive]`.
- Keep items private by default. Use `pub(crate)` before `pub`.

## Diagnostics
- ❗ Never suppress warnings (`#[allow]`, `#![allow]`, `expect`) without user approval.
- When approved: this template. Lint id + reason + narrowest scope.

```rust
#[allow(clippy::too_many_arguments)] // Reason: {why}. Scope: this fn. User approved.
```

## Comments and rustdoc
- Comment purpose, motivation, caveats, and design choice before non-trivial logic. State why; never restate syntax.
- Split non-trivial function bodies into semantic blocks. One blank line between blocks (two allowed where `rustfmt` preserves them; prefer one — `blank_lines_upper_bound` is typically 1).
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
- Use atomics instead of mutexes when a single word is enough.

## Safety
- Keep `unsafe` rare. Put `// SAFETY:` on each `unsafe` block: aliasing, lifetimes, validity, `Send`/`Sync`.
- Validate at FFI and I/O boundaries before any `unsafe`.

## Performance
Performance is a feature. Hot path = per-item / per-byte after setup. Setup may allocate; the loop must not.

- Minimize heap allocations and `clone`. No GC — treat `String`, `Vec`, `Box`, `clone`, and `collect` as costs.
- Plan allocation order: stack/borrows, then reuse buffers, then bump/arena, then heap.
- Return rented/reused buffers; do not leak scratch `Vec`s.
- Do not `.collect()` or build intermediate `Vec`/`String` on hot paths without measured need. Keep iterators lazy.
- Do not capture-allocate on hot paths. Use function items or `impl Fn` without heap capture.
- Provide SIMD plus scalar fallback for compute-heavy bulk work.
- Maximize monomorphized inlining in measured hot paths; avoid `dyn` on those paths.

| Pattern | Intent |
|---------|--------|
| Stack / borrows / `MaybeUninit` scratch | Per-item work off the heap |
| Buffer reuse / `BytesMut`-style | Avoid per-call `Vec` |
| Thread-local scratch | Single-thread parse/format; not across `.await` |
| Arena / bump | Many values, one lifetime |
| SIMD + scalar fallback | Bulk scan, fill, checksum |
| Zero-copy `&[u8]` / slices | Borrow instead of copy |
| Enums over trait objects | No per-item vtable on the common path |
| `#[inline]` / monomorphize | Keep the loop inlinable |
| Grow-only chunks | Do not copy the whole store on growth |

## Tests
`tech-test.md` + `tech-rust-test.md`.

## Dependencies
Intent: Section 4.7. Never add a crate without user approval.

- Never `cargo add` or edit `[dependencies]` / `[workspace.dependencies]` without user approval.
- Ask in Grill-Me when plan may need new crates.
- Present: crate name, **what it does**, **why it is needed**, license (`MIT` / `Apache-2.0` / BSD-like), alternatives.
- After approval: add to `[workspace.dependencies]` first when a workspace exists, then the crate’s `Cargo.toml`.
- Commit `Cargo.lock`.

## Copyright
- `.rs`: `// {copyright}` — exact text from `COPYRIGHT`.

## Formatting
- Limit line length to 160 (`rustfmt.toml` `max_width = 160`).
- 4-space indent; no tabs. Let `rustfmt` apply.
- Implement `Drop` for resource owners. No manual forget except documented intent.
- Decompose complex functions into focused helpers.
- ❗ Executables with console I/O: set UTF-8 **once** at process start (Section 4.7). Required on Windows. No extra crate. Call before any print.

```rust
#[cfg(windows)]
{
    #[link(name = "kernel32")]
    extern "system" {
        fn SetConsoleOutputCP(code_page: u32) -> i32;
        fn SetConsoleCP(code_page: u32) -> i32;
    }

    const CP_UTF8: u32 = 65001;
    // SAFETY: SetConsoleOutputCP / SetConsoleCP are Windows kernel32 APIs.
    // Both take a u32 code page; 65001 is CP_UTF8. No pointers, aliases, or
    // lifetimes. Calls are valid on Windows. Return is ignored so a console
    // that cannot switch CP does not abort; later I/O still runs.
    unsafe {
        let _ = SetConsoleOutputCP(CP_UTF8);
        let _ = SetConsoleCP(CP_UTF8);
    }
}
```
- Stream files with `BufReader` / `Read` / `Write` (Section 4.4). Do not `fs::read` / `fs::read_to_string` on unbounded files.

```rust
let reader = BufReader::new(File::open(path)?);
```
- Name threads.
- No `println!` / `eprintln!` / `dbg!` for library error handling.

## Commands

```bash
cargo fmt --check
cargo clippy --all-targets --all-features -- -D warnings
cargo test --release
```

Do not wrap these in a new script without approval. `target/` lock errors: concurrent agent (Section 4.14).
