# Rust Test Rules

Load when Rust tests are in scope (`#[cfg(test)]`, `tests/`). Content and case design: `tech-test.md`. Language: `tech-rust.md`.

## Layout

- Unit tests: `#[cfg(test)]` next to the code under test.
- Integration tests: `tests/`.
- Names: `tech-test.md` (`method_scenario_expected_result`).

## Commands

```bash
cargo test --release
cargo test --release pairing_empty_repo_returns_none -- --exact
```

`cargo fmt` / `clippy`: `tech-rust.md`. Debug and collision: `tech-test.md`.

## Authoring

Mechanics only. What to cover, speed, doubles, AAA, exit paths: `tech-test.md`.

- Assert `Err` explicitly. Do not `unwrap` on error-path tests.

```rust
assert!(matches!(pair(Path::new("")), Err(PairError::Empty)));
```

- Cover Rust exits `Ok`, `Err`, `?`, and documented panic — each still needs a test per `tech-test.md`.
