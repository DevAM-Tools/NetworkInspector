# Rust Test Rules

Load when Rust tests are in scope (`#[cfg(test)]`, `tests/`). Content and case design: `tech-test.md`. Language: `tech-rust.md`.

## Layout

- Unit tests: `#[cfg(test)]` next to the code under test.
- Integration tests: `tests/`.
- Names: `tech-test.md` (`unit_scenario_expected`).

## Commands

When the full crate suite is not required, pass a test filter (`tech-test.md`).

```bash
# Exact match needs the full module path:
cargo test --release parser::tests::parse_empty_input_returns_none -- --exact
# Substring match (may run several tests):
cargo test --release parse_empty_input
cargo test --release
```

❗ A filtered run that prints `running 0 tests` for every target is a failed `Verify`, even though cargo exits 0. Fix the filter; do not report green.

`cargo fmt` / `clippy`: `tech-rust.md`. Debug and collision: `tech-test.md`.

## Authoring

Mechanics only. What to cover, speed, doubles, AAA: `tech-test.md`.

- Assert `Err` explicitly. Do not `unwrap` on error-path tests.

```rust
assert!(matches!(pair(Path::new("")), Err(PairError::Empty)));
```

- Cover Rust exits `Ok`, `Err`, `?`, and documented panic — each still needs a test per `tech-test.md`.
