# .NET → Rust Mapping

Load with `workflow-port-dotnet-rust.md`. Rust mechanics stay in `tech-rust.md`; this skill says **what a .NET construct becomes** and **which .NET semantics must survive**. Target: Rust edition 2024, `rust-version = "1.85"`, stable toolchain only (no nightly features such as `portable_simd`).

Sections: 1 Mapping order · 2 Idiom Map defaults (`IM-*`) · 3 Semantic traps · 4 Memory and ownership · 5 Strings and `Text` · 6 Optimizations · 7 `unsafe`

## 1) Mapping order

For every construct ask, in this order:

1. What can a caller **observe**? That must survive (`workflow-port-dotnet-rust.md` Section 2).
2. Does an `IM-*` rule cover the **form**? Use it.
3. What **ownership** does the data have (Section 4)? Decide before writing the body.
4. Which **source optimization** is present, and is it mapped, adapted, or dropped (Section 6)?

## 2) Idiom Map defaults

These rules make a form difference count as `ported`. The MANIFEST may change or extend them; numbers are stable.

### Shape and naming

- `IM-01` Constructors → `new`; extra constructors → `with_*` / `from_*`. Parameterless struct state → `Default`.
- `IM-02` `PascalCase` members → `snake_case`. Property `X` → `x()`, setter → `set_x()`. `IsX` / `HasX` → `is_x()` / `has_x()`. Constants → `SCREAMING_SNAKE_CASE`.
- `IM-09` Overloads → distinct names by the differing parameter (`parse`, `parse_with_options`), or a trait when the set is generic over the input type. Default parameter values → separate fns or an options struct whose `Default` has **the same values**.
- `IM-29` Properties with logic → methods. Public fields only for plain data without invariants.
- `IM-31` Nested types → `outer::Inner` module path or `OuterInner`.
- `IM-32` `internal` → `pub(crate)`. `protected` → trait method or `pub(crate)` by use. `InternalsVisibleTo` tests → in-crate `#[cfg(test)]`.
- `IM-33` Assembly → crate; namespace → module path.

### Failure and absence

- `IM-03` `bool TryX(…, out T)` and a throwing `X(…)` for the same failure → one `fn x(…) -> Result<T, E>`; both rows point to it. A lone `TryX` → `Result<T, E>` when failure reasons differ, else `Option<T>`.
- `IM-04` Expected-failure exceptions → one error variant per exception type and condition. Exception message text is not ported verbatim unless the MANIFEST says so; `Display` of the error carries the same facts.
- `IM-05` The value of an `out` parameter after failure is not observable when the result is `Option` / `Result`.
- `IM-06` Several `out` parameters → tuple or named struct.
- `IM-07` Null guards on non-nullable reference parameters (`ArgumentNullException`, `is null` early returns) → `n/a`. Nullable `T?` → `Option<T>`.
- `IM-20` Count-or-zero and `-1` sentinels → `Result<usize, E>` / `Option<usize>`. The failure condition stays the same; the buffer stays untouched on failure as in the source.
- `IM-34` Negative-value guards on counts and sizes → `n/a` when the Rust parameter is unsigned. Upper bounds stay validated.

### Traits

- `IM-08` Object-typed members (`Equals(object)`, non-generic `IComparable`, `IEnumerable`) → covered by typed trait impls; wrong-type branches → `n/a`.
- `IM-10` `IParsable<T>` / `ISpanParsable<T>` → `FromStr` (plus an inherent `parse`). An ignored `IFormatProvider` is dropped.
- `IM-11` `IEquatable<T>`, `IComparable<T>`, `==`, `<` … → `PartialEq`, `Eq`, `PartialOrd`, `Ord`, consistent with each other.
- `IM-12` `ToString()` → `Display`; owned text when callers need it → `to_text()`.
- `IM-13` `record` / `record struct` value equality → derives over the same fields.
- `IM-14` `GetHashCode` values are not ported; only `Eq`/`Hash` consistency. Hash values that are persisted or emitted are ported exactly.
- `IM-15` Interfaces → traits. Generic constraints → generics. `dyn` only where the source stores heterogeneous implementations.
- `IM-16` Abstract hierarchy closed inside the assembly → enum (or sealed trait); open to consumers → trait.
- `IM-17` Static classes → module functions. Extension methods on own types → inherent fns; on foreign types → extension trait.
- `IM-27` Generic math interfaces (`INumber<T>` …) → traits implemented for the used types.

### Resources, iteration, events, async

- `IM-18` `IDisposable` → `Drop`. A `Dispose` that can fail or must flush → `close(self) -> Result<…>` plus best-effort `Drop`. `ObjectDisposedException` after dispose → `n/a` when consuming `self` makes reuse impossible.
- `IM-19` `IEnumerable<T>` results → `impl Iterator<Item = T>` or a named iterator type in public API. `List<T>` / `IReadOnlyList<T>` → `Vec<T>` / `&[T]`. Lazy re-enumeration semantics are kept where observable.
- `IM-24` Events and delegates → observer trait or owned callback registered on the subject; a handle for unsubscribe; invocation order as in the source.
- `IM-25` `async Task<T>` → the MANIFEST hard-spot decision. Without real I/O awaits → synchronous fn.
- `IM-26` `CancellationToken` → the MANIFEST hard-spot decision (cancel flag type).
- `IM-28` `const` / `static readonly` → `const` / `static`. Static constructor → `LazyLock` / `OnceLock`.

### Text and formatting

- `IM-21` `format` / `IFormatProvider` parameters the source ignores → removed. Used format strings → an explicit format enum. Culture is invariant unless the source uses culture on purpose (then hard spot).
- `IM-22` Thread-static or pooled temporary text (`TempString`, rented `char[]`) → owned stack value `InlineText<N>` or a caller buffer.
- `IM-23` `Span<char>` output that is ASCII by construction → `&mut [u8]` UTF-8 output.

### Enums

- `IM-30` Closed enum never built from external data → Rust `enum`. Enum parsed from wire/file data or cast from integers → newtype `struct Kind(u16)` with associated consts, so unknown values stay representable (C# enums are open). `[Flags]` → newtype with bit consts and `BitOr`/`BitAnd`. `Enum.ToString()` for unknown values prints the number in .NET; keep that.

## 3) Semantic traps

Check each trap that the unit touches during the source walk. A trap that changes an outcome is a behavior item.

### Strings

- `string.Length`, indexers, `Substring`, `IndexOf` results count **UTF-16 units**; `str::len` counts bytes. Equal only for ASCII; prove ASCII or compute `encode_utf16().count()`.
- C# strings may hold unpaired surrogates; `str` cannot. Input from `char` arrays or UTF-16 sources needs a decision.
- Ordinal compare in .NET orders by UTF-16 unit; `str` `Ord` orders by code point. They differ for U+10000+ vs U+E000–U+FFFF. Sorted output with such text needs `encode_utf16` compare.
- `StartsWith(string)`, `EndsWith(string)`, `IndexOf(string)`, `Compare`, `ToUpper()`, `ToLower()` without arguments are **culture-sensitive** in .NET. Find which one the source calls; `Ordinal` / `Invariant` variants map to byte or ASCII operations.
- `ToUpperInvariant` maps one char to one char; `str::to_uppercase` may expand (`ß` → `SS`). Use `to_ascii_uppercase` when the source data is ASCII, else replicate per char.
- `Encoding.UTF8.GetString` replaces invalid bytes with U+FFFD (`String::from_utf8_lossy` matches). `Encoding.ASCII` replaces with `?`. `File.ReadAllText` strips a BOM; `fs::read_to_string` keeps it.
- `int.Parse` / `TryParse` with `NumberStyles.Integer` accept leading and trailing whitespace and a leading sign; Rust `str::parse` accepts no whitespace. Hex parse (`NumberStyles.HexNumber`) rejects `0x`.
- Format strings: `X2` is upper-case, `x2` lower-case; `D`, `N`, padding, negative hex (two’s complement) must match exactly.

### Numbers

- C# integer arithmetic is **unchecked by default** (wraps); Rust release wraps but debug panics. Use `wrapping_*` where the source relies on wrap, `checked_*` where the source uses `checked`.
- Shift counts are masked in C# (`x << 65` on `long` = `x << 1`); Rust overflows. Mask explicitly (`wrapping_shl`).
- Integer division by zero throws in C#; Rust panics. Map to the source’s exception path when callers can hit it.
- `Math.Round(x)` rounds half **to even**; `f64::round` rounds half away from zero. Use `round_ties_even` (stable since 1.77).
- `double.ToString()` and Rust `{}` both print shortest round-trip, but exponent form differs (`1E+20` vs `100000000000000000000`). Port the exact formatting rule.
- `double.Equals(NaN, NaN)` is true, `NaN == NaN` is false. Records with `double` fields use `Equals`. Rust `PartialEq` follows `==`; implement `Eq` by bits (`to_bits`) when the source relies on `Equals`.
- `decimal` → dedicated decimal type (crate approval) or scaled integer; never `f64`.
- `int` sizes and indexes → `usize` in Rust, but persisted or emitted widths keep their exact type (`i32` on the wire stays `i32`).

### Collections and order

- `Dictionary<K, V>` enumerates in insertion order while nothing is removed (implementation detail that tests often rely on); `HashMap` order is random. Observable order → insertion-ordered structure (`Vec` + index map) or `BTreeMap` when the source sorts.
- `Dictionary.Add` throws on duplicate; `HashMap::insert` overwrites. Indexer get throws `KeyNotFoundException`; `get` returns `Option`. Keep the source outcome.
- `List.Sort` / `Array.Sort` are unstable (introsort); LINQ `OrderBy` is stable. Rust `sort` is stable, `sort_unstable` is a different unstable order. Equal keys with observable order need a stable sort and an explicit tie rule; prove the source order with a fixture.
- `Distinct` keeps the first occurrence; `GroupBy` orders groups by first appearance; `ToDictionary` throws on duplicates.
- LINQ is lazy and re-runs per enumeration; side effects in the query repeat.

### Types, equality, lifetime

- Class equality defaults to reference identity; struct equality defaults to field-wise. Reference identity is observable only through `ReferenceEquals`, `==` on classes without override, or dictionary keys of class type.
- `default(T)` of a struct is always the all-zero value and always valid. Rust `Default` must produce exactly that state.
- Static constructors run lazily on first access; side effects and order matter → `LazyLock`.
- Finalizers do not port; behavior they guarantee (release of OS handles) moves to `Drop`.

### Concurrency and time

- `lock` (Monitor) is **reentrant**; `Mutex` is not. A call chain that re-enters deadlocks. Redesign (lock once at the boundary, pass `&mut` inward) before porting the methods.
- `Monitor.Wait` / `Pulse` → `Condvar` with a predicate loop.
- .NET stores of object references have release semantics (safe publication), and the runtime keeps data-dependent reads ordered. Lock-free code that publishes objects through plain fields needs `Release` / `Acquire` atomics in Rust. `Interlocked.*` is a full fence → `SeqCst`, or a weaker ordering with a written argument. `Volatile.Read` → `Acquire`, `Volatile.Write` → `Release`.
- `DateTime` ticks are 100 ns; `Kind` and local time are observable. `Stopwatch` → `Instant`. `TimeSpan` formatting is exact text.

### Hidden meaning

- Reflection, `dynamic`, expression trees, `AssemblyLoadContext`, DI containers, source generators: the meaning is in metadata, not in code. Each use is a hard spot with an explicit Rust design (registration functions, `macro_rules!`, `const` tables, `build.rs`).
- Regex: .NET backtracking features (lookaround, backreferences, `RegexOptions`) do not exist in the `regex` crate. Hard spot.

## 4) Memory and ownership

Central rule: a GC object graph becomes an **ownership tree plus indices**. Decide ownership per type before the body, and record it in the unit **Design** with the rejected C#-shaped form (usually `Arc<Mutex<_>>` or `Rc<RefCell<_>>`).

### Classify every reference field

| C# reference | Rust form |
|--------------|-----------|
| Owned child, one owner | value field, `Box<T>` when recursive or large |
| Back-reference to parent | index into the owner’s arena, or parent passed as parameter |
| Shared, immutable after build | `&'a T` when a scope outlives users; `Arc<T>` across threads; `Rc<T>` single thread |
| Shared and mutated | arena + index owned by one place; synchronization only where the source synchronizes |
| Lazy cache | `Option<T>` / `OnceLock<T>` / `OnceCell<T>` |
| Observer, callback | `Box<dyn Trait>` owned by the subject, or channel |
| Pooled object reused after recycle | free list owned by the pool owner; `clear()` and reuse |

### Ladder (first workable class wins)

| Class | Use when |
|-------|----------|
| Owned `T` (move) | Single owner. Default. |
| `&T` / `&mut T` | Caller outlives the use; value not stored. |
| `Copy` | Small, bitwise copy is the meaning. A C# `struct` alone is not enough. |
| `Clone` | A snapshot is the meaning. State the cost. |
| `Box<T>` / `Box<[T]>` | Recursive or large; one owner. Not “it was a class”. |
| Arena + index / generational index | Many nodes, one lifetime; trees with parent links; graphs. |
| `Arc<T>` / `Rc<T>` | True shared ownership, acyclic, immutable or documented interior mutation. |
| `Mutex` / `RwLock` / atomics | The source synchronizes the same state. Same guarantee, documented lock order. |
| `unsafe` | Only at MANIFEST-approved sites (Section 7). |

Never introduce `Arc<Mutex<_>>` to imitate GC aliasing. When the source needs aliasing plus mutation, design an owner with indices first; when that fails, the unit books `BL-*`.

### Common .NET shapes

- **Field and node trees** (parent, children, siblings): one `Vec<Node>` per tree, `u32` indices, parent index per node. Recycling clears lengths and keeps capacity.
- **Buffers shared by many views** (`ReadOnlyMemory<byte>` slices): borrow `&'a [u8]` when the buffer outlives the views; else one `Arc<[u8]>` plus offset and length per view.
- **Registries built at startup, read everywhere**: build with `&mut`, then freeze into an immutable `Arc<Registry>` (or a `static` only when the MANIFEST approves process lifetime).
- **DI lifetimes**: Transient → function returns owned `T`. Scoped → owned by the scope object, borrowed out. Singleton → owned by the process root. A singleton that holds a scoped service is a lifetime error; do not cover it with `Arc`.
- **Dispose patterns**: deterministic `Drop`; `close(self) -> Result` when release can fail.

### Allocation discipline

- Hot path (per item, per byte after setup) does not allocate: no `String`, `Vec`, `Box`, `clone` of heap data, `format!`, `collect()` in the loop.
- Allocation order: stack and borrows → reused buffers → arena → heap.
- `HashMap` with the default SipHash is slower than .NET `Dictionary` for integer keys. Use an approved fast hasher or an own `BuildHasher` for internal integer-keyed maps; keep SipHash where keys come from untrusted input.

## 5) Strings and `Text`

`Text` is the port’s owned string type: a 16-byte handle with absent, inline (≤ 15 bytes), static, shared (one ref-counted block), and lazy forms. Reference implementation: `ni_values::Text` ([`rust/ni-values/src/text.rs`](../../rust/ni-values/src/text.rs)), originally from packet-inspector `pi_values`. Another solution names its `Text` crate in the MANIFEST.

### Which Rust type for which C# use

| C# use | Rust |
|--------|------|
| Read-only parameter (`string`, `ReadOnlySpan<char>`) | `&str` |
| Stored or returned immutable text | `Text` |
| Literal, `const`, `static readonly`, interned name | `Text::from_static` / `&'static str` |
| Built step by step (`StringBuilder`, interpolation) | `Text::format(format_args!(…))`; `String` only while building in a loop |
| Short text with a fixed maximum (`stackalloc char[N]`, `TryFormat`) | `InlineText<N>` or caller `&mut [u8]` |
| `string?` | `Option<Text>`; parameter `Option<&str>` |
| Display text that may never be read | `Text::lazy` (see equality rule) |
| `char` as UTF-16 unit work | `u16`; `char` only for code-point logic |

`InlineText<N>` is a small owned stack string (`[u8; N]` plus length, `Deref<Target = str>`). The first unit that needs it creates it next to `Text`.

### Contract the port relies on

- Absent ≠ empty. `Text::absent()` is reserved for “no text of its own” in display models; general nullable strings use `Option<Text>`.
- Inline, static, and shared forms compare and hash **by content**, like C# ordinal string equality. A static and a copied `Text` with the same bytes are equal and hash equal.
- Lazy forms compare and hash by **recipe identity** and never equal a concrete text. Therefore a lazy `Text` must not replace a C# `string` whose equality or hash a caller observes (keys, comparisons, dedup). Use a concrete form there.
- Clone is O(1): copy for inline and static, one atomic increment for shared and lazy.
- Text produced on a hot path stays inline (≤ 15 bytes) or static; longer hot-path text uses a caller buffer or `InlineText<N>`.
- `Text` is `Send + Sync`; lazy bake runs at most once per node.

Changes to the `Text` contract (equality semantics, inline capacity, niche for `Option<Text>`) are MANIFEST decisions and pass through `BL-*` during Phase 2.

## 6) Optimizations

Every unit records how each source optimization was handled: **mapped** (same idea, Rust tool), **adapted** (goal kept, mechanism changed), **dropped** (the reason does not exist in Rust). Dropping is never a reason to make the port slower than the source on a hot path.

### Mapped

| .NET | Rust |
|------|------|
| `Span<T>`, `ReadOnlySpan<T>` | `&mut [T]`, `&[T]` |
| `stackalloc`, `[InlineArray]` | `[T; N]` |
| `readonly struct`, `in` parameters | `Copy` value or `&T` |
| `ref struct` | struct with a lifetime parameter |
| `BinaryPrimitives` | `from_be_bytes` / `to_be_bytes` / `from_le_bytes` |
| `AggressiveInlining` | `#[inline]` (needed across crates) |
| `Lazy<T>`, double-checked init | `OnceLock`, `LazyLock`, `OnceCell` |
| `Interlocked`, `Volatile` | atomics with the matching `Ordering` |
| `string.Intern`, static strings | `&'static str`, `Text::from_static` |
| `IUtf8SpanFormattable`, `Utf8Formatter` | write into `&mut [u8]` |
| Static abstract interface members | trait associated fns and consts |

### Adapted

| .NET | Rust |
|------|------|
| `ArrayPool<T>`, `MemoryPool<T>` | caller-owned reusable buffer; owner keeps capacity |
| `ObjectPool<T>`, recycle pools | free list in the owner; `clear()` keeps capacity |
| `[ThreadStatic]` scratch | caller buffer (preferred) or `thread_local!` (never across `.await`) |
| `Memory<T>` stored in objects | borrow with lifetime, or `Arc<[T]>` + range |
| `Vector128/256/512`, `Avx2`, `Sse`, `AdvSimd` | `std::arch` with `is_x86_feature_detected!` / NEON, plus scalar fallback; same results bit for bit |
| `SearchValues`, vectorized `IndexOfAny` | approved `memchr` or a loop LLVM vectorizes; measure |
| `FrozenDictionary`, `FrozenSet` | `match`, sorted slice + `binary_search`, `const` tables |
| `Unsafe.As`, `MemoryMarshal`, bounds-check tricks | iterators, `chunks_exact`, slice patterns; `get_unchecked` only at approved `unsafe` sites |
| `[SkipLocalsInit]` | zero init (usually free) or `MaybeUninit` at approved sites |
| `sealed` for devirtualization | generics and enum dispatch |
| Source generators for speed | `macro_rules!`, `const fn` tables, `build.rs` |
| `ConcurrentDictionary` | sharded `RwLock<HashMap>` or approved crate |
| `lock` on hot paths | single owner + `&mut`, atomics, or a lock with the same guarantee |
| `ValueTask` pooling | synchronous fn, or plain future |

### Dropped (reason does not exist in Rust)

- GC tuning: `GC.KeepAlive`, `GCSettings`, Large Object Heap avoidance, pinning (`fixed`, `GCHandle`), pinned arrays.
- Boxing avoidance via generic constraints (no boxing in Rust generics).
- Tiered compilation, ReadyToRun, `AggressiveOptimization` (Rust is compiled optimized).
- `WeakReference` / `ConditionalWeakTable` caches whose only purpose is GC cooperation (replace with explicit ownership).

### Rust-only wins (use them)

- Enums with data instead of class hierarchies; niche-optimized `Option`.
- Monomorphization and `const` generics on hot paths; no virtual call per item.
- Borrowed parse results (`&'a [u8]`) instead of copied fields.
- No object header and no write barriers: small structs in `Vec` are dense; struct-of-arrays where access patterns allow.
- Arena allocation for trees and graphs; one free per tree.

## 7) `unsafe`

- Port crates start with `#![forbid(unsafe_code)]`. A crate with approved sites uses `#![deny(unsafe_code)]` and `#[allow(unsafe_code)]` only on the approved module, with the `tech-rust.md` suppression template.
- Approved sites are listed in the MANIFEST (`Text`, SIMD kernels, OS calls such as the console code page). A new site is a `BL-*`.
- Every `unsafe` block has `// SAFETY:` with aliasing, lifetime, validity, and `Send`/`Sync` arguments, and a test that exercises it (Miri when the site is pure Rust and the MANIFEST lists a Miri run).
