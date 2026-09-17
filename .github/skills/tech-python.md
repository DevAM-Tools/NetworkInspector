# Python Standards

Load when `*.py`, `pyproject.toml`, or `*.pyi` files are in scope. Python mechanisms for Section 4 in `copilot-instructions.md`. Idiomatic, **strictly typed** Python.

## Language

- ❗ Strict typing is mandatory on every public and internal function, method, and class attribute. Annotate parameters, returns, and stored fields.
- ❗ `Any` only with user approval. Same for untyped `*args` / `**kwargs` on public boundaries.
- Use `from __future__ import annotations` when it clarifies forward refs.
- Use `Protocol`, `TypedDict`, `enum.Enum`, and `@dataclass` (or equivalent). Do not pass loose `dict` / `object` across public boundaries.

```python
class Pairer(Protocol):
    def pair(self, path: str) -> int | None: ...
```

- Pin Python 3.12+ unless the project already pins otherwise. Do not silently bump.

## Type gate

- ❗ `pyright` in **strict** mode (or `mypy --strict` if that is the repo SSOT). Treat type errors as build errors.
- No implicit optional. No untyped defs.

## Style

- `ruff check` and `ruff format`. Line length 160.
- ❗ Never suppress (`# type: ignore`, `# noqa`, `cast` to `Any`) without user approval.
- When approved: this template. Rule id + reason. Narrowest scope.

```python
unused = probe()  # noqa: F841  # Reason: {why}. User approved.
```

```python
raw: Any = lib.call()  # pyright: ignore[reportUnknownMemberType]  # Reason: {why}. User approved.
```
- Explicit encodings (`encoding="utf-8"`). Do not depend on process locale.
- ❗ Executables with console I/O: set UTF-8 **once** at process start (Section 4.7). Required on Windows. Call before any print.

```python
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
sys.stdin.reconfigure(encoding="utf-8")
```
- Return a result type for expected failure (`Ok`/`Err`, or `(value, err)` with a typed error). Do not throw on expected paths, especially on hot paths.
- Validate at trust boundaries. Document omitted validation and caller guarantees in the docstring.

## Comments and docstrings

- Comment purpose, motivation, caveats, design choice. Do not restate syntax.
- Split non-trivial function bodies into semantic blocks. One blank line between blocks (two allowed). Lead each block with intent + what to watch for.

```python
def pair(self, path: str) -> int | None:
    # Index is immutable after Build. Keys match OS paths.
    index = TestProjectIndex.build(self._root)
    return index.get(path)
```

- Put a docstring on every public item. Document errors, raises, thread-safety, and omitted validation when they apply.

## Integer arithmetic

- ❗ Assess overflow/underflow when wrap-around would be wrong. Python `int` is unbounded; still validate ranges at boundaries (size, index, wire formats).

## Thread safety

- ❗ Shared mutable state: lock it or put it on a concurrent queue. Document the primitive.
- Share immutable data. Isolate mutation in a process or thread. Do not mutate shared objects without a lock.

## Performance

Performance is a feature. Hot path = per-item / per-byte after setup.

| Pattern | Intent |
|---------|--------|
| `__slots__` / dataclass slots | Fewer instance dicts |
| Reuse `bytearray` / `memoryview` | Avoid per-call `bytes` allocs |
| `__slots__` structs / `array` / `numpy` (approved) | Compact numeric data |
| Generator / iterator | Do not materialize full lists on the hot path |
| `lru_cache` only for pure, bounded keys | Do not cache unbounded inputs |
| Avoid hidden allocs | f-strings, `+`, comprehensions that build large temps in the loop |
| `memoryview` slices | Zero-copy views |
| `orjson` / known codec (approved) | Hot serialize/parse |

Minimize allocations in the loop. CPython has no SIMD — move bulk work to an approved native extension only with New Dependency Protocol. Fix algorithm and layout first.

- Stream files (Section 4.4). Iterate the file object or copy through a bounded buffer. Do not `Path.read_text()` / `read_bytes()` on unbounded files.

```python
with path.open(encoding="utf-8") as handle:
    for line in handle:
        ...
```

## Tests

`tech-test.md` + `tech-pytest.md`.

## Dependencies

Section 4.7. Never add a package without user approval. Present: name, **what it does**, **why needed**, license (`MIT` / `Apache-2.0` / BSD-like), alternatives.

## Copyright

`# {copyright}` — exact text from `COPYRIGHT`.

## Commands

```bash
# Use the project’s declared runner (uv / poetry / pip). Do not invent a wrapper script.
uv run ruff check .
uv run ruff format --check .
uv run pyright
uv run pytest -q
```

If the repo has no `uv`, use the documented equivalent (`poetry run`, `.venv/bin/pytest`). Do not create a venv-management script without approval.
