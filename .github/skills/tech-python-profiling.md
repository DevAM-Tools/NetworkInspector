# Python Profiling

Load with `tech-python.md` when profiling is requested, a step optimizes a hot path, a `Verify` or `Debug` needs a measurement, or a finding claims a measured delta (Section 3).

❗ Use no profiler that needs admin, root, or changed kernel settings (Section 4.15).

## Deterministic (default, every OS)

- Use `cProfile`; it ships with Python and prints text an agent can read.

```bash
uv run python -m cProfile -s cumtime -m package.module | head -40
uv run python -X importtime -c "import package" 2> importtime.txt
```

## Sampling

- Use `py-spy` (MIT) for lower overhead and real call stacks; add it through the New Dependency Protocol as a dev tool.
- Let py-spy launch the program: Windows and Linux then need no rights. Never attach with `--pid` when that needs elevation.
- Use `cProfile` on macOS; py-spy needs root there.

```bash
uv run py-spy record --format raw -o stacks.txt -- python -m package.module
uv run py-spy record --format speedscope -o profile.speedscope.json -- python -m package.module
```

- Read `stacks.txt` (one collapsed stack per line with its sample count). Open the speedscope file at [speedscope.app](https://www.speedscope.app) for a flame graph.
