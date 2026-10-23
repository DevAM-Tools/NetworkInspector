# Rust Standards

Load when `*.rs`, `Cargo.toml`, or `Cargo.lock` is in scope. Rust mechanisms for Section 4. Section order matches `tech-csharp.md` and `tech-python.md`.

## Language

- Edition 2024; `rust-version = "1.85"` in `[workspace.package]`; stable features only.
- ❗ Add `rustfmt.toml` with `max_width = 160`. Let `rustfmt` format; 4 spaces.
- Type public signatures fully. Annotate locals when inference hides a conversion or a heap type.

## Naming

- `snake_case` functions, modules, locals, fields; `PascalCase` types and traits; `SCREAMING_SNAKE_CASE` consts and statics.
- Name by role; no `foo2`, `foo_impl`, `data2`.
- Conversions: `as_` (free borrow), `to_` (costly copy), `into_` (consumes `self`). No `get_` prefix on getters.

## Files and modules

- One cohesive module per file: `foo.rs` plus `foo/` for children; no `mod.rs` unless the tree uses it.
- Declare `mod` in the parent. No glob re-exports in the public API.
- Split crates at API boundaries, not per type.

## Style

- ❗ Parse and format with explicit UTF-8 and fixed formats; never depend on process locale.
- ❗ One callable exit per line (`return`, `?`, `break` with value, diverging macro). Put match arms with `?` on their own lines.

```rust
// Wrong
let size = read_header(input)?.payload_len().ok_or(ParseError::Truncated)?;
// Right
let header = read_header(input)?;
let size = header.payload_len().ok_or(ParseError::Truncated)?;
```

- Write `if` / `match` as expressions, not `let mut x;` plus assignments.
- Use `mut` only when needed. Default to private; `pub(crate)` before `pub`.
- Split complex functions into focused helpers. Implement `Drop` for resource owners; `forget` only with a documented reason.

## Types and API design

Follow the Rust API Guidelines:

- Implement `Debug` on every public type (lint-enforced); derive `Clone`, `Copy`, `PartialEq` / `Eq`, `Hash`, `Ord`, `Default` where they mean something.
- Mark growable public enums and structs `#[non_exhaustive]`.
- Validate at construction: newtypes with private fields and `TryFrom` / `new -> Result` at trust boundaries.
- Accept borrowed or generic input (`&str`, `&[T]`, `impl AsRef<Path>`); return owned values.
- Put `#[must_use]` on pure functions and on values that must not be dropped silently.
- Make public futures `Send` unless documented otherwise.

```rust
/// A TCP port in `1..=65535`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Port(u16);

impl TryFrom<u32> for Port {
    type Error = PortError;

    fn try_from(value: u32) -> Result<Self, Self::Error> {
        // 0 is reserved; values above u16 cannot be ports.
        let narrowed = u16::try_from(value).map_err(|_| PortError::OutOfRange(value))?;
        if narrowed == 0 {
            return Err(PortError::OutOfRange(value));
        }

        Ok(Self(narrowed))
    }
}
```

## Errors and logging

- Return `Result` for expected failure and `Option` for absence. No sentinels.
- ❗ No `unwrap`, `expect`, `panic!`, `todo!`, `unimplemented!` in library code (lint-enforced). A documented invariant may `expect` under `#[expect(clippy::expect_used, reason = "…")]` with a `# Panics` section.
- Define one public error `enum` per crate or module: `#[non_exhaustive]`, `Debug + Display + Error`, `source()` for causes, `Send + Sync + 'static`, `From` for causes that `?` converts.
- Write `Display` messages lowercase, without trailing period or secrets.
- Keep `Box<dyn Error>` and `anyhow::Error` out of library APIs; only `main` may box. `thiserror` / `anyhow` need the New Dependency Protocol.
- Never print or initialize logging in libraries. With an approved facade (`log` / `tracing`), the binary installs the subscriber once in `main`.
- ❗ Never log secrets, tokens, or PII. Give secret-holding types a redacting `Debug`, not `#[derive(Debug)]`.

```rust
/// Errors from [`Port::try_from`].
#[derive(Debug, Clone, PartialEq, Eq)]
#[non_exhaustive]
pub enum PortError {
    /// The value is 0 or above 65535.
    OutOfRange(u32),
}

impl std::fmt::Display for PortError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::OutOfRange(value) => write!(formatter, "port {value} is outside 1..=65535"),
        }
    }
}

impl std::error::Error for PortError {}
```

## Lints and suppressions

❗ Enforce rules with lints. Put this in the workspace `Cargo.toml`; add `[lints] workspace = true` to every member:

```toml
[workspace.lints.rust]
unsafe_code = "deny"                    # approved sites open it with #[expect]
missing_docs = "deny"
missing_debug_implementations = "deny"
unreachable_pub = "deny"

[workspace.lints.clippy]
unwrap_used = "deny"
expect_used = "deny"
panic = "deny"
todo = "deny"
unimplemented = "deny"
dbg_macro = "deny"
print_stdout = "deny"                   # binaries: #[expect] on the one output function
print_stderr = "deny"
undocumented_unsafe_blocks = "deny"     # enforces // SAFETY:
missing_errors_doc = "deny"             # enforces # Errors
missing_panics_doc = "deny"             # enforces # Panics
await_holding_lock = "deny"

[profile.release]
overflow-checks = true
```

`clippy.toml` at the workspace root (tests may unwrap):

```toml
allow-unwrap-in-tests = true
allow-expect-in-tests = true
```

- Add `#![forbid(unsafe_code)]` to crates without an approved `unsafe` site.
- ❗ Suppress nothing (`#[allow]`, `#![allow]`, `#[expect]`) without user approval. Then use `#[expect]` with lint id, reason, and the narrowest scope — a stale `#[expect]` fails the build.

```rust
#[expect(clippy::too_many_arguments, reason = "{why}. Scope: this fn. User approved.")]
```

## Comments and docs

- Comments and semantic blocks: Section 4.6; one blank line between blocks.
- Put units in comments, not in names.
- Write `///` on every public item and `//!` on crates and modules. Add `# Errors`, `# Panics`, `# Safety`, `# Thread Safety` where they apply; document omitted validation.
- Keep doc examples compiling; they run as tests.

## Integer arithmetic

- ❗ Check every integer operation for overflow: `checked_*`, `saturating_*`, `wrapping_*`, widen, or validate.
- Keep `overflow-checks = true` in release. Turn it off only with a measurement on a named hot path, and then use explicit `wrapping_*` / `checked_*` there; record the decision in `Cargo.toml`.
- Narrow with `try_from`, never `as` on values that may not fit.
- Document safe ranges; prove bounds at boundaries, not in inner loops.

## Security

- **Commands:** `Command::new(program).arg(value)`; never `sh -c` / `cmd /C` with input.
- **Paths:** canonicalize, then confirm the result stays under the root.

```rust
fn resolve_inside(root: &Path, relative: &Path) -> Option<PathBuf> {
    // canonicalize resolves `..` and symlinks; both sides must exist.
    let base = root.canonicalize().ok()?;
    let candidate = base.join(relative).canonicalize().ok()?;
    candidate.starts_with(&base).then_some(candidate)
}
```

- **Deserialization:** `#[serde(deny_unknown_fields)]` on boundary types; bound input size first; validate into newtypes.
- **Hash maps:** keep SipHash for untrusted keys (HashDoS); fast hashers only for internal keys.
- **SQL:** bind parameters; never format values into queries.
- **Limits** (Section 4.2): a limits struct with private fields, a documented `Default`, and `TryFrom` / a builder that rejects out-of-range values. Host-derived defaults: `std::thread::available_parallelism()`. Format and protocol facts are `const`. Comment the security decision at the definition:

```rust
// Threat: deeply nested input overflows the parser stack. Secure by default: input is remote.
// Operator may set 16..=1024 in config. Rejected: unbounded recursion (stack overflow aborts the process).
// Residual risk: 1024 levels still cost CPU per request; the rate limit covers that.
max_depth: u16,
```
- **Secrets:** redacting `Debug`; no secrets in `Display` or errors. `subtle` / `zeroize` only through the New Dependency Protocol.
- **`unsafe`:** only at approved sites, opened with `#[expect(unsafe_code, reason = "…")]`; `// SAFETY:` on every block (aliasing, lifetimes, validity, `Send`/`Sync`). Validate FFI and I/O input first.
- ❗ No `unsafe impl Send` / `Sync` without a documented invariant.

## Async

- Treat the runtime as a dependency decision; keep libraries runtime-agnostic where possible.
- ❗ Never block the executor (`std::fs`, `thread::sleep`, long locks, heavy CPU); use `spawn_blocking` or a dedicated thread.
- ❗ Never hold a `std::sync` guard across `.await`; end its scope first.
- Never `block_on` or `futures::executor` inside async library code.
- Document `# Cancel safety` on public async functions used in `select!`; leave no partial state when dropped at an await.
- Bound every external wait with a timeout.

```rust
// Wrong
let mut queue = shared.lock()?;
send(queue.pop()).await;
// Right
let next = {
    let mut queue = shared.lock()?;
    queue.pop()
};
send(next).await;
```

## Thread safety

- ❗ Pick the cheapest correct primitive (Section 4.3); design so threads rarely block.

| Need | Use | Avoid |
|------|-----|-------|
| No sharing | Move ownership; per-thread state; `Arc<T>` of immutable data | Any lock |
| One word | `Atomic*` with the weakest correct `Ordering` | `Mutex<u64>` |
| Built once | `OnceLock`, `LazyLock` | `Mutex<Option<T>>` |
| Short update of a small struct | `Mutex`, a few instructions held | Holding it across I/O, allocation, `.await`, callbacks |
| Long reads, rare writes | `RwLock`, or an `Arc<T>` snapshot replaced under a short `Mutex` | `RwLock` for tiny sections |
| Hand-off | Channel where waiting is the design | Channel per item on a hot path |

- Document lock order and poison handling; never hold two locks without a documented order.
- Give atomics an explicit `Ordering`; justify `SeqCst`.
- Add `Send` / `Sync` bounds only where the type guarantees them. Document `# Thread Safety` unless the type is plain data.
- Name threads.

## Performance and allocation

❗ Every heap allocation costs allocator time and fragments memory. Avoid allocations on **every** path. On hot paths (per item, per byte after setup) allocate nothing.

- Allocate in this order: stack and borrows → reused buffers → arena / bump → heap.
- Avoid on all paths: `clone` of heap data, intermediate `Vec` / `String`, `collect()` that only feeds another loop, `format!` where `write!` into a buffer works, `Box<dyn Fn>` where a generic works. Pre-size with `with_capacity`.
- ❗ On hot paths: no `String`, `Vec`, `Box`, `format!`, `collect()`, or heap-capturing closures.
- Return or reuse scratch buffers; keep iterators lazy; prefer `&[u8]` slices over copies.
- Prefer enums and generics over `dyn` inside loops; keep loops inlinable (`#[inline]` across crates).
- Use thread-local scratch for single-thread parse/format, never across `.await`. Use arenas for many values with one lifetime; grow chunked stores without copying.
- SIMD only after profiling shows a hot loop that did not auto-vectorize: `std::arch` behind `is_x86_feature_detected!` / `is_aarch64_feature_detected!`, scalar fallback with the same tests, inside an approved `unsafe` site.

### Profiling

Load `tech-rust-profiling.md` when a measurement is needed (Section 3). Never use a profiler that needs admin rights (Section 4.15).

## Console and files

- ❗ Set UTF-8 once at process start in executables with console I/O (Section 4.7). No extra crate.

```rust
#[cfg(windows)]
{
    #[link(name = "kernel32")]
    // Edition 2024 requires `unsafe extern`.
    unsafe extern "system" {
        fn SetConsoleOutputCP(code_page: u32) -> i32;
        fn SetConsoleCP(code_page: u32) -> i32;
    }

    const CP_UTF8: u32 = 65001;
    // SAFETY: kernel32 APIs taking a u32 code page; no pointers or lifetimes.
    // The return value is ignored so a console that cannot switch does not abort.
    unsafe {
        let _ = SetConsoleOutputCP(CP_UTF8);
        let _ = SetConsoleCP(CP_UTF8);
    }
}
```

- ❗ Stream files with `BufReader` / `Read` / `Write`; never `fs::read` / `read_to_string` on unbounded files.

## Tests

`tech-test.md` + `tech-rust-test.md`.

## Dependencies

New Dependency Protocol: Section 4.7. After approval add to `[workspace.dependencies]` first, then the crate. Keep `Cargo.lock` tracked. `cargo audit` / `cargo deny` are tools that need the protocol too.

## Copyright

`.rs`: `// {copyright}` from `COPYRIGHT`.

## Commands

```bash
cargo fmt --check
cargo clippy --all-targets --all-features -- -D warnings
cargo test --release
cargo test --doc
RUSTDOCFLAGS="-D warnings" cargo doc --no-deps
```

PowerShell: set `$env:RUSTDOCFLAGS = "-D warnings"` first. `target/` lock errors: concurrent agent (Section 4.14).
