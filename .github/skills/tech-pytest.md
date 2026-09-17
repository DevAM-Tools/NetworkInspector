# Pytest Rules

Load when `test_*.py`, `*_test.py`, or pytest is in scope. Content and case design: `tech-test.md`. Typing: `tech-python.md`.

## Framework

❗ **pytest** only unless the repo already standardizes otherwise. Do not add unittest-style classes, extra assertion libs, or a second runner without approval.

| Piece | Role |
|-------|------|
| pytest | Discovery, fixtures, `assert` |
| pytest-asyncio | Only if the project is async and approved |

## Commands

```bash
uv run pytest -q
uv run pytest -q tests/test_gap_run.py::test_empty_solution_returns_zero
uv run pytest -q -k "pairing and not slow"
```

Do not add `-x` unless the user wants fail-fast. Debug and collision: `tech-test.md`.

## Authoring

Mechanics only. What to cover, speed, doubles, AAA, exit paths: `tech-test.md`.

- Files: `test_<unit>.py`. Functions: `test_` prefix (pytest discovery) + `tech-test.md` name shape.
- Type every test function and fixture. Do not leave fixtures untyped.
- Parametrize with `@pytest.mark.parametrize`:

```python
@pytest.mark.parametrize(
    ("size", "expected"),
    [(0, 0), (1, 1), (2, 2)],
)
def test_count_collection_size_matches(size: int, expected: int) -> None:
    ...
```

- Fixtures: typed, narrow, no shared mutable module globals.
- Write files on disk with `tmp_path`. Do not write outside the workspace.

## Markers

- `@pytest.mark.slow` only when unavoidable (`tech-test.md` speed).
- Document custom markers in `pytest.ini` / `pyproject.toml`.
