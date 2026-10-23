# Python Standards

Load when `*.py`, `pyproject.toml`, or `*.pyi` is in scope. Python mechanisms for Section 4. Strictly typed, idiomatic Python. Section order matches `tech-csharp.md` and `tech-rust.md`.

## Language

- Prefer Python **3.13** for new projects (`requires-python = ">=3.13"`); minimum 3.12. Soft default: keep an existing `requires-python`; propose a bump in Grill Me, never bump silently.
- Use 3.12 syntax: `type` aliases, type parameters (`def first[T](…)`, `class Box[T]`), `@override`, `Self`.
- Use 3.13-only typing (`TypeIs`, `warnings.deprecated`, `ReadOnly` in `TypedDict`, type-parameter defaults) only when `requires-python` is ≥ 3.13; on 3.12 `typing_extensions` needs the New Dependency Protocol.
- ❗ Annotate every function, method, parameter, return, and class attribute.
- ❗ No `Any`, and no untyped `*args` / `**kwargs` on public boundaries, without user approval.

## Naming

- `snake_case` functions, modules, variables; `PascalCase` classes and type aliases; `UPPER_SNAKE_CASE` constants marked `Final`.
- Prefix private names with `_`; no `__` mangling unless a subclass clash is real.
- Name by role; no `data2`, `helper`, or topic-less `util` modules.

## Files and modules

- One cohesive module per file; export public names via `__all__` in `__init__.py`.
- Use absolute imports. Ordering: ruff `I`.
- No import-time side effects (I/O, logging setup, network).

## Style

- Format with `ruff format`; line length 160.
- ❗ One callable exit per line; in a conditional expression put each calling branch on its own line.
- Use `match` for structural branching instead of `isinstance` ladders.
- ❗ Pass `encoding="utf-8"` explicitly; never depend on the process locale.

## Types and API design

- Cross public boundaries with `Protocol`, `TypedDict`, `Enum`, and dataclasses — never loose `dict` or `object`.
- Make data immutable: `@dataclass(frozen=True, slots=True)`, `Final` constants, `tuple` returns when callers must not mutate.
- Accept `Sequence` / `Mapping` / `Iterable`; return concrete types.
- Mark overrides with `@override`.

```python
MAX_NAME_LENGTH: Final = 200


class Pairer(Protocol):
    def pair(self, path: str) -> int | None: ...


@dataclass(frozen=True, slots=True)
class Export:
    name: str
    tags: tuple[str, ...]


def first[T](items: Sequence[T]) -> T | None:
    return items[0] if items else None
```

## Errors and logging

Choose one form per API:

| Situation | Form |
|-----------|------|
| Plain absence | `T \| None` |
| Expected failure the caller must handle (parsing, validation, hot paths) | `Result[T, E]` below, consumed with `match` |
| Failure the caller cannot handle locally (I/O, configuration, external service) | Documented exception from the package hierarchy |

```python
@dataclass(frozen=True, slots=True)
class Ok[T]:
    value: T


@dataclass(frozen=True, slots=True)
class Err[E]:
    error: E


type Result[T, E] = Ok[T] | Err[E]


def parse_port(text: str) -> Result[int, PortError]:
    if not text.isdigit():
        return Err(PortError.NOT_A_NUMBER)
    port = int(text)
    if not 1 <= port <= 65535:
        return Err(PortError.OUT_OF_RANGE)
    return Ok(port)


match parse_port(raw):
    case Ok(value=port):
        connect(port)
    case Err(error=error):
        report(error)
```

- Define `Ok`, `Err`, `Result` once per package (for example `result.py`).
- Derive package exceptions from one base (`class ExportError(Exception)`). Chain with `raise … from err`. List them under `Raises:`.
- ❗ No bare `except:`. Catch `Exception` only at a top-level boundary (CLI entry, request handler, worker loop), log with `logger.exception`, then re-raise or convert. Never swallow `BaseException`, `KeyboardInterrupt`, or `CancelledError`.
- Create `logger = logging.getLogger(__name__)` per module. Never configure handlers in libraries.
- Pass log arguments lazily (`logger.info("exported %s rows", count)`), never f-strings (ruff `G004`).
- ❗ Never log secrets, tokens, or PII.
- Validate at trust boundaries; document omitted validation.

## Lints and suppressions

❗ Enforce rules with tools. Start every project with this baseline (pre-approved; changes need approval):

```toml
[project]
requires-python = ">=3.13"

[tool.ruff]
line-length = 160
target-version = "py313"

[tool.ruff.lint]
select = [
  "E", "W", "F", "I", "N", "UP", "B", "SIM", "C4", "RUF",
  "ANN",   # annotations everywhere; ANN401 bans Any
  "S",     # security (bandit)
  "BLE",   # blind except
  "TRY",   # exception patterns
  "RET", "PTH", "PERF", "PL",
  "G", "LOG",  # logging format
  "T20",   # print
  "D",     # docstrings
]

[tool.ruff.lint.pydocstyle]
convention = "google"

[tool.ruff.lint.per-file-ignores]
"tests/**" = ["S101", "D", "PLR2004"]   # tests stay fully typed (tech-pytest.md)

[tool.pyright]
typeCheckingMode = "strict"
pythonVersion = "3.13"
```

- On 3.12 set `target-version = "py312"` and `pythonVersion = "3.12"`.
- ❗ Treat pyright strict errors (or `mypy --strict` where that is the SSOT) as build errors.
- ❗ Suppress nothing (`# noqa`, `# type: ignore`, `# pyright: ignore`, `cast` to `Any`) without user approval; then name the rule id and reason at the narrowest scope.

```python
unused = probe()  # noqa: F841  # Reason: {why}. User approved.
raw: Any = lib.call()  # pyright: ignore[reportUnknownMemberType]  # Reason: {why}. User approved.
```

## Comments and docs

- Comments and semantic blocks: Section 4.6.
- ❗ Write Google-style docstrings on every public module, class, function, and method (ruff `D`): summary, then `Args:`, `Returns:`, `Raises:`. Document thread safety and omitted validation.

```python
def pair(self, path: str) -> int | None:
    """Return the index of the test project that covers `path`.

    Args:
        path: Production file path, relative to the repository root.

    Returns:
        The project index, or None when no test project covers the path.
    """
    # Index is immutable after build. Keys match OS paths.
    index = TestProjectIndex.build(self._root)
    return index.get(path)
```

## Integer arithmetic

- ❗ Validate ranges at boundaries. `int` does not overflow, but fixed-width targets do: check before `struct.pack`, wire formats, `numpy` dtypes, and database columns.

## Security

- **Processes:** `subprocess.run([program, arg], check=True)`; never `shell=True`, `os.system`, or `os.popen` with input.
- **Deserialization:** never `pickle`, `marshal`, `shelve`, or `yaml.load` on untrusted data; use `json` / `yaml.safe_load`, then validate into typed objects.
- **Code execution:** no `eval`, `exec`, or `compile` on input.
- **SQL:** driver placeholders (`cursor.execute("… WHERE id = ?", (item_id,))`); never f-strings or `%`.
- **Limits** (Section 4.2): a frozen dataclass with documented defaults; check ranges in `__post_init__` and raise the package's configuration error. Host-derived defaults: `os.process_cpu_count()` (3.13+, else `os.cpu_count()`). Format and protocol facts are `Final` constants. Comment the security decision at the definition:

```python
# Threat: a huge CSV floods memory. Open by default: the tool reads the user's own local files.
# User may tighten it with --max-rows (1 or more). Rejected: a fixed cap (blocks large local datasets).
# Residual risk: an untrusted file can still exhaust memory unless the user sets --max-rows.
max_rows: int | None = None
```
- **Paths:** resolve, then confirm the result stays under the root.

```python
def resolve_inside(root: Path, relative: str) -> Path | None:
    base = root.resolve()
    candidate = (base / relative).resolve()
    if not candidate.is_relative_to(base):
        return None
    return candidate
```

- **Secrets:** `secrets.token_urlsafe` for tokens, `hmac.compare_digest` for comparison; never `random`.
- **Temporary files:** `NamedTemporaryFile` / `mkstemp`; never `mktemp`.
- **Network:** give every HTTP call an explicit timeout.
- **Untrusted XML:** avoid it, or add a hardened parser through the New Dependency Protocol.

## Async

- ❗ Never block the event loop (`time.sleep`, blocking I/O, heavy CPU); use `asyncio.sleep`, async libraries, or `asyncio.to_thread`.
- Run concurrent work in an `asyncio.TaskGroup`; keep a reference to every task.
- Bound waits with `async with asyncio.timeout(seconds):`.
- ❗ Let `CancelledError` propagate; clean up in `finally`.
- Use `asyncio.Lock` in the loop; `threading.Lock` only for thread-shared state, never across `await`.

```python
async def fetch_all(urls: Sequence[str]) -> list[bytes]:
    async with asyncio.timeout(30):
        async with asyncio.TaskGroup() as group:
            tasks = [group.create_task(fetch(url)) for url in urls]
    return [task.result() for task in tasks]
```

## Thread safety

- ❗ Protect shared mutable state and document the primitive.
- Pick the cheapest correct primitive (Section 4.3): no sharing → one-time init → `threading.Lock` with a short section (no I/O, `await`, or callbacks) → `queue.Queue` where waiting is the design.
- Use threads for I/O only; run CPU work in `ProcessPoolExecutor` or an approved native extension.
- ❗ Never rely on the GIL: free-threaded builds (3.13t+) run bytecode in parallel, so every check-then-set and read-modify-write on shared objects needs a lock.

## Performance and allocation

❗ Every object is an allocation the runtime must reclaim, and Python allocates on almost every operation. Avoid needless objects on **every** path; on hot paths (per item, per byte) create none that the result does not need.

- Use `slots=True` dataclasses; reuse `bytearray` and `memoryview` slices instead of new `bytes`.
- Stream with generators; never materialize a full list that is only iterated once.
- Avoid temporaries in loops: f-strings, `+` concatenation (use `"".join`), comprehensions that build throwaway lists.
- Cache only pure functions with bounded keys (`functools.lru_cache(maxsize=…)`).
- Use `array` or approved `numpy` for numeric data; move bulk work to a native extension only through the New Dependency Protocol. Fix the algorithm first.

### Profiling

Load `tech-python-profiling.md` when a measurement is needed (Section 3). Never use a profiler that needs admin rights (Section 4.15).

## Console and files

- ❗ Set UTF-8 once at process start in executables with console I/O (Section 4.7).

```python
# Typeshed types the streams as TextIO; only TextIOWrapper has reconfigure().
for stream in (sys.stdin, sys.stdout, sys.stderr):
    if isinstance(stream, io.TextIOWrapper):
        stream.reconfigure(encoding="utf-8")
```

- ❗ Stream files: iterate the file object or copy through a bounded buffer; never `read_text()` / `read_bytes()` on unbounded files.
- `print` only in the CLI entry point (ruff `T20`).

## Tests

`tech-test.md` + `tech-pytest.md`.

## Dependencies

New Dependency Protocol: Section 4.7. After approval add packages with the project runner (`uv add`, `poetry add`); keep the lock file tracked. `pip-audit` is a tool that needs the protocol too.

## Copyright

`# {copyright}` from `COPYRIGHT`.

## Commands

```bash
uv run ruff check .
uv run ruff format --check .
uv run pyright
uv run pytest -q
```

Without `uv`, use the project’s runner (`poetry run`, `.venv/bin/…`).
