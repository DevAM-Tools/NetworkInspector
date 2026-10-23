# Rust Profiling

Load with `tech-rust.md` when profiling is requested, a step optimizes a hot path, a `Verify` or `Debug` needs a measurement, or a finding claims a measured delta (Section 3).

❗ Use no profiler that needs admin, root, or changed kernel settings (Section 4.15). samply is out on Windows (ETW elevation) and wherever `perf_event_paranoid` > 1 (WSL default: 2).

## Instrumentation (default, every OS)

- Add `tracing`, `tracing-subscriber`, `tracing-chrome` (MIT) through the New Dependency Protocol. Gate the subscribers behind a `profiling` feature of the binary.

```toml
[features]
profiling = ["dep:tracing-subscriber", "dep:tracing-chrome"]

[dependencies]
tracing = "0.1"
tracing-subscriber = { version = "0.3", optional = true, default-features = false, features = ["registry", "std", "fmt"] }
tracing-chrome = { version = "0.7", optional = true }

[profile.profiling]
inherits = "release"
debug = true
```

```rust
/// Installs the profiling subscribers; the guard flushes `trace.json` on drop.
#[cfg(feature = "profiling")]
fn init_profiling() -> tracing_chrome::FlushGuard {
    use tracing_subscriber::prelude::*;

    // Timeline for Perfetto or the Firefox Profiler.
    let (chrome_layer, guard) = tracing_chrome::ChromeLayerBuilder::new().file("trace.json").build();
    // Text for agents: one stderr line per closed span with time.busy.
    let timing_layer = tracing_subscriber::fmt::layer()
        .with_span_events(tracing_subscriber::fmt::format::FmtSpan::CLOSE)
        .with_ansi(false)
        .with_writer(std::io::stderr);
    tracing_subscriber::registry().with(chrome_layer).with(timing_layer).init();
    guard
}

#[tracing::instrument(level = "trace", skip_all)]
fn parse_batch(input: &[u8]) -> usize { /* … */ }

fn main() -> std::process::ExitCode {
    // Keep the guard alive to the end of main; std::process::exit would skip the flush.
    #[cfg(feature = "profiling")]
    let _profiling = init_profiling();
    // …
}
```

- Span coarse phases first, then narrow into the slowest span.
- Use `skip_all`; never format arguments into spans.
- Span loops, never per-item loop bodies.
- Emit spans in libraries; install subscribers only in the binary.
- ❗ Return `ExitCode` from `main`. `std::process::exit` skips the guard and leaves `trace.json` empty.

```bash
cargo run --profile profiling --features profiling -- <args> 2> timings.txt
```

- Read `timings.txt` (`close time.busy=…` per span). Open `trace.json` in [Perfetto](https://ui.perfetto.dev) for the timeline.

## Sampling (only where no rights are needed)

- Use samply only on macOS, or on Linux when `cat /proc/sys/kernel/perf_event_paranoid` already prints ≤ 1. Never change the value.

```bash
cargo build --profile profiling
samply record --save-only -o profile.json -- ./target/profiling/app
```
