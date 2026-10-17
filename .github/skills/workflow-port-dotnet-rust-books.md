# .NET → Rust Port Books — Templates and Worked Example

Load with `workflow-port-dotnet-rust.md`. Every port artifact under `port/` follows these templates. Field names are fixed so the loop and the balance check can find them. English only. Status words: `workflow-port-dotnet-rust.md` Section 5. Default `IM-*` rules: `tech-dotnet-rust.md` Section 2.

## 1) ID scheme

| ID | Meaning | Lives in |
|----|---------|----------|
| `T:` `M:` `P:` `F:` `E:` | .NET documentation comment ID of a public member | inventory, unit records |
| `Namespace.Class.Method(args)` | .NET test case as the runner lists it | inventory, unit records |
| `BH-nn` | Behavior item, numbered per unit (`MacAddress:BH-07`) | unit record |
| `IM-nn` | Idiom Map rule | `tech-dotnet-rust.md`, MANIFEST amendments |
| `PA-nn` | Pre-approved deviation class | MANIFEST |
| `EX-nn` | Exclusion | MANIFEST |
| `HS-nn` | Hard spot and its decision | MANIFEST |
| `DV-nnn` | Deviation record | `DEVIATIONS.md` |
| `BL-nnn` | Blocker (question for the human) | `BLOCKERS.md` |
| `SB-nnn` | Suspected bug in the .NET source | `BLOCKERS.md` |

IDs never appear in Rust code or comments.

## 2) `port/MANIFEST.md`

```markdown
# Port Manifest — {Solution} → Rust

**Status:** Draft | Approved
**Approved by:** {name} · **Amendments:** {list of BL-* that changed this file}

## Scope

| Assembly | Crate | Tests |
|----------|-------|-------|
| {Assembly} | `{crate}` | {TestProject} |

- **Out:** {assemblies not ported, one line each with reason}
- **Exclusions:** `EX-01` {member or feature} — {reason} — {who decided}

## Workspace

- Path: `rust/` · edition 2024 · `rust-version = "1.85"` · resolver 3
- Approved crates: `{crate}` {version} — {purpose} — {license}
- `unsafe` approved at: {module or pattern} — {invariant}. Everything else `#![forbid(unsafe_code)]`.

## Idiom Map

Defaults: `tech-dotnet-rust.md` Section 2, all adopted unless listed.

- Changed: `IM-04` {new wording}
- Added: `IM-40` {rule}

## Pre-approved deviation classes

- `PA-01` {class} · **Allowed when:** {condition} · **Required test:** {pattern}

## Hard spots

- `HS-01` {spot, source location} · **Decision:** {handling} · **Rows:** {IDs or "all of type X"}

## Oracle

- Ported .NET tests: {projects}
- Golden fixtures: `port/oracle/{Harness}` writes `port/fixtures/{area}/…` with `{command}`
- Differential runs: {inputs, comparison}

## Performance

| Scenario | .NET command | Rust command | Target |
|----------|--------------|--------------|--------|

## Limits

- {limit}: {value} · constant | configurable ({where, default})

## Priority targets

- {unit list}. Order only; every unit is ported completely.

## Units

Generated in Phase 0: topological order per assembly, cycles merged.

1. `{Assembly}/{Unit}` — types: {list} — depends on: {units}
```

## 3) `port/STATUS.md`

Short by design; read first on resume. Lists, not wide tables.

```markdown
# Port Status

**Cursor:** `{Assembly}/{Unit}` ({status}) · **Next:** `{Unit}`, `{Unit}`
**Waits on:** `{Unit}` ← `BL-nnn`
**Last completed:** `{Unit}`
**Balance:** ok | broken ({what})

## Balance per assembly

- **{Assembly}** API {n}: ported {n} · deviated {n} · excluded {n} · blocked {n} · wip {n} · open {n}
  - Behavior {n}: ported {n} · deviated {n} · n/a {n} · blocked {n} · wip {n} · open {n}
  - Tests {n} (.NET) + {n} added: ported {n} · n/a {n} · blocked {n} · wip {n} · open {n}
  - Units {n}: done {n} · wip {n} · open {n} · blocked {n} · Milestone: ⬜ | ✅ {review path}

## Open blockers

- `BL-nnn` {one-line question} — holds {n} rows
```

## 4) Unit record `port/units/<Assembly>/<Unit>.md`

```markdown
# {Unit} — {Assembly}

**Status:** open | wip | done | blocked
**Source:** [`{path}.cs`](../../../{path}.cs)
**Rust:** [`{path}.rs`](../../../rust/{path}.rs) · `{crate}::{Type}`
**Depends on:** `{Unit}` · **Used by:** `{Unit}`

## Contract

{What the type is for. Invariants. Thread safety. Failure leaves state unchanged or not. Three to eight sentences, from the source.}

## Design

- **Rust form:** {struct / enum / trait / newtype}, derives
- **Ownership:** {memory class} · **Rejected:** {C#-shaped form and why}
- **Idiom rules applied:** `IM-nn` {short}, …
- **Source optimizations:** {optimization} → mapped | adapted ({how}) | dropped ({why})
- **Strings:** {where Text / &str / String is used and why}

## API rows

- `{doc ID}` → `{Rust item}` — **{status}** — tests: `{rust_test}` · {IM/DV/BL reference when needed}

## Behavior items

- `BH-nn` [L{line}] {observable outcome} → `{Rust location}` — **{status}** — tests: `{rust_test}`

## Test rows

- `{.NET test case}` → `{rust_test}` — **{status}** · {note when not 1:1}

## Deviations / blockers

- `DV-nnn` {title} · `BL-nnn` {title}

## Verification

- `{command}` → {result}
- Hunt: {counts compared, markers found = 0}
```

## 5) `DV-nnn` in `port/DEVIATIONS.md`

```markdown
### DV-nnn — {title}

- **Rows:** `{doc ID}`, `{Unit}:BH-nn`
- **Class:** forced | improvement | scope · **Approval:** `PA-nn` | `BL-nnn` answer | MANIFEST `HS-nn`
- **.NET behavior:** {exact, with source line}
- **Rust behavior:** {exact}
- **Observable differences:** {every one; "none besides the listed" is not allowed}
- **Why:** {reason}
- **Caller gains / loses:** {…} / {…}
- **Pinning test:** `{rust_test}`
```

## 6) `BL-nnn` and `SB-nnn` in `port/BLOCKERS.md`

```markdown
### BL-nnn — {question as one sentence}

- **Rows:** {IDs} · **Holds units:** {list}
- **Context:** {source location, what the agent found}
- **Options:** A {…, consequence} · B {…, consequence}
- **Agent recommendation:** {A/B and why}
- **Answer:** {filled by the human}
- **Applied:** {filled by the loop: MANIFEST amendment, rows re-opened}

### SB-nnn — {suspected .NET bug}

- **Source:** [`{path}`](../{path}) L{line}
- **Reproducer:** {input → .NET output → expected output}
- **Port keeps .NET behavior:** yes · **Pinning test:** `{rust_test}`
- **Answer:** {keep | fix in both | fix in Rust only → becomes DV}
```

## 7) `port/BENCHMARKS.md` row and `port/REPORT.md`

```markdown
### {Scenario}

- Workload: {input, counts, warmup} · Machine: {cpu, os}
- .NET: {throughput, wall time} · Rust: {throughput, wall time} · Target: {met | missed by x %}
- Owning units: `{Unit}`
```

```markdown
# Port Report — {Solution}

**Verdict:** complete | waiting ({n} blockers)
**Balance:** {per assembly line from STATUS}
**Deviations:** {count by class, link list}
**Suspected source bugs:** {list}
**Performance:** {scenarios vs target}
**Reviews:** {review-loop paths per crate}
**How to verify:** {commands}
```

## 8) Worked example — `MacAddress`

A complete unit record for [`NetworkInspector.Values/MacAddress.cs`](../../NetworkInspector.Values/MacAddress.cs) and [`MacAddressTests.cs`](../../NetworkInspector.Values.Tests/MacAddressTests.cs). It shows the level of detail the loop must reach: every member, every outcome, every test row, and the reason for each form difference.

```markdown
# MacAddress — NetworkInspector.Values

**Status:** done
**Source:** [`NetworkInspector.Values/MacAddress.cs`](../../../NetworkInspector.Values/MacAddress.cs)
**Rust:** [`ni-values/src/mac_address.rs`](../../../rust/ni-values/src/mac_address.rs) · `ni_values::MacAddress`
**Depends on:** `ValueError`, `InlineText` · **Used by:** `Eui64`, `EthernetProtocol`

## Contract

48-bit EUI-48 address in the low 48 bits of a `u64`. The constructor masks the upper 16 bits, so every value is valid.
Text form is exactly `XX:XX:XX:XX:XX:XX` with upper-case hex on output; input accepts both cases and no other
separator or length. Binary form is 6 bytes big-endian. Equality, hash, and order follow the raw value.
Immutable value type; thread safe by construction.

## Design

- **Rust form:** `#[derive(Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Default)] struct MacAddress(u64)`
- **Ownership:** `Copy` (8 bytes, value semantics are the meaning) · **Rejected:** `&MacAddress` parameters; nothing to borrow
- **Idiom rules applied:** `IM-01` ctor → `new`, `IM-03` Try/throw pair → one `Result` fn, `IM-05` out-param on failure
  not observable, `IM-07` null arguments, `IM-08` object-typed overloads, `IM-10` `IParsable` → `FromStr`,
  `IM-12` `ToString` → `Display`, `IM-13` record equality → derives, `IM-14` hash values not ported,
  `IM-20` count-or-zero → `Result<usize, _>`
- **Source optimizations:** `AggressiveInlining` → mapped (`#[inline]`, `const fn`) · `stackalloc char[17]` →
  adapted (`[u8; 17]` on the stack) · `TempString` thread-static buffer → adapted (`InlineText<17>` value, no
  thread-local, no dispose) · `byte[]` from `ToBytesArray` → adapted (`[u8; 6]`, no heap)
- **Strings:** parse takes `&str`; format writes bytes into a caller buffer; owned text is `InlineText<17>`;
  `to_text()` gives `Text` for display pipelines

## API rows

- `T:NetworkInspector.Values.MacAddress` → `MacAddress` — **ported**
- `F:NetworkInspector.Values.MacAddress.FormattedLength` → `MacAddress::FORMATTED_LENGTH` — **ported** — tests: `formatted_length_is_17`
- `M:NetworkInspector.Values.MacAddress.#ctor(System.UInt64)` → `MacAddress::new` — **ported** — tests: `constructor_masks_to_48_bits`, `constructor_valid_value_stored` · IM-01
- `P:NetworkInspector.Values.MacAddress.RawValue` → `raw_value()` — **ported** — tests: `constructor_valid_value_stored`
- `P:NetworkInspector.Values.MacAddress.IsBroadcast` → `is_broadcast()` — **ported** — tests: `classification_matches_first_octet_bits`
- `P:NetworkInspector.Values.MacAddress.IsMulticast` → `is_multicast()` — **ported** — tests: `classification_matches_first_octet_bits`
- `P:NetworkInspector.Values.MacAddress.IsUnicast` → `is_unicast()` — **ported** — tests: `classification_matches_first_octet_bits`
- `P:NetworkInspector.Values.MacAddress.IsLocal` → `is_local()` — **ported** — tests: `classification_matches_first_octet_bits`
- `P:NetworkInspector.Values.MacAddress.IsGlobal` → `is_global()` — **ported** — tests: `classification_matches_first_octet_bits`
- `P:NetworkInspector.Values.MacAddress.IsZero` → `is_zero()` — **ported** — tests: `default_is_all_zeros`
- `M:NetworkInspector.Values.MacAddress.TryFromBytes(System.ReadOnlySpan{System.Byte},NetworkInspector.Values.MacAddress@)` → `from_bytes` — **ported** — tests: `try_from_bytes_short_span_returns_false` · IM-03
- `M:NetworkInspector.Values.MacAddress.FromBytes(System.ReadOnlySpan{System.Byte})` → `from_bytes` — **ported** — tests: `from_bytes_big_endian`, `from_bytes_short_span_throws` · IM-03
- `M:NetworkInspector.Values.MacAddress.TryParse(System.ReadOnlySpan{System.Char},NetworkInspector.Values.MacAddress@)` → `parse` — **ported** — tests: `try_parse_valid_addresses`, `try_parse_invalid_addresses_returns_false` · IM-03
- `M:NetworkInspector.Values.MacAddress.System#ISpanParsable{NetworkInspector#Values#MacAddress}#TryParse(System.ReadOnlySpan{System.Char},System.IFormatProvider,NetworkInspector.Values.MacAddress@)` → `impl FromStr` — **ported** — tests: `from_str_matches_parse` · IM-10
- `M:NetworkInspector.Values.MacAddress.System#ISpanParsable{NetworkInspector#Values#MacAddress}#Parse(System.ReadOnlySpan{System.Char},System.IFormatProvider)` → `impl FromStr` — **ported** — tests: `from_str_invalid_is_invalid_format` · IM-10
- `M:NetworkInspector.Values.MacAddress.System#IParsable{NetworkInspector#Values#MacAddress}#TryParse(System.String,System.IFormatProvider,NetworkInspector.Values.MacAddress@)` → `impl FromStr` — **ported** — tests: `from_str_matches_parse` · IM-10
- `M:NetworkInspector.Values.MacAddress.System#IParsable{NetworkInspector#Values#MacAddress}#Parse(System.String,System.IFormatProvider)` → `impl FromStr` — **ported** — tests: `from_str_invalid_is_invalid_format` · IM-10
- `M:NetworkInspector.Values.MacAddress.ToBytes(System.Span{System.Byte})` → `write_bytes` — **ported** — tests: `to_bytes_big_endian`, `write_bytes_short_buffer_is_buffer_too_small` · IM-20
- `M:NetworkInspector.Values.MacAddress.ToBytesArray` → `to_bytes() -> [u8; 6]` — **ported** — tests: `to_bytes_array_big_endian`
- `M:NetworkInspector.Values.MacAddress.TryGetWrittenSize(System.Int32@)` → `BinarySerializable::written_size` — **ported** — tests: `try_get_written_size_is_6`
- `M:NetworkInspector.Values.MacAddress.TryWrite(System.Span{System.Byte},System.Int32@)` → `BinarySerializable::try_write` — **ported** — tests: `try_write_short_buffer_is_buffer_too_small`
- `M:NetworkInspector.Values.MacAddress.TryFormat(System.Span{System.Char},System.Int32@,System.ReadOnlySpan{System.Char},System.IFormatProvider)` → `format_into(&mut [u8])` — **ported** — tests: `try_format_buffer_too_small_returns_false` · IM-21
- `M:NetworkInspector.Values.MacAddress.TryFormat(System.Span{System.Byte},System.Int32@,System.ReadOnlySpan{System.Char},System.IFormatProvider)` → `format_into(&mut [u8])` — **ported** — tests: `try_format_buffer_too_small_returns_false`, `format_produces_correct_string` · IM-21
- `M:NetworkInspector.Values.MacAddress.TryGetStringSize(System.ReadOnlySpan{System.Char},System.IFormatProvider,System.Int32@)` → `StringSize::string_size` — **ported** — tests: `string_size_is_17`
- `M:NetworkInspector.Values.MacAddress.FormatInto(System.Span{System.Char})` → `format_into` — **ported** — tests: `format_into_short_buffer_writes_nothing`
- `M:NetworkInspector.Values.MacAddress.FormatTemp` → `format_inline() -> InlineText<17>` — **ported** — tests: `format_temp_produces_correct_string` · IM-22
- `M:NetworkInspector.Values.MacAddress.Format` → `to_text()` — **ported** — tests: `format_produces_correct_string`
- `M:NetworkInspector.Values.MacAddress.ToString(System.String,System.IFormatProvider)` → `impl Display` — **ported** — tests: `format_produces_correct_string` · IM-12
- `M:NetworkInspector.Values.MacAddress.ToString` → `impl Display` — **ported** — tests: `format_produces_correct_string` · IM-12
- `M:NetworkInspector.Values.MacAddress.CompareTo(NetworkInspector.Values.MacAddress)` → `impl Ord` — **ported** — tests: `compare_to_ordering`
- `M:NetworkInspector.Values.MacAddress.System#IComparable#CompareTo(System.Object)` → `impl Ord` — **ported** — tests: `compare_to_ordering` · IM-08
- `M:NetworkInspector.Values.MacAddress.op_LessThan(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialOrd` — **ported** — tests: `compare_to_ordering`
- `M:NetworkInspector.Values.MacAddress.op_GreaterThan(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialOrd` — **ported** — tests: `compare_to_ordering`
- `M:NetworkInspector.Values.MacAddress.op_LessThanOrEqual(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialOrd` — **ported** — tests: `compare_to_ordering`
- `M:NetworkInspector.Values.MacAddress.op_GreaterThanOrEqual(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialOrd` — **ported** — tests: `compare_to_ordering`
- `M:NetworkInspector.Values.MacAddress.Equals(NetworkInspector.Values.MacAddress)` → `impl PartialEq` — **ported** — tests: `equality_same_value_are_equal` · IM-13
- `M:NetworkInspector.Values.MacAddress.Equals(System.Object)` → `impl PartialEq` — **ported** — tests: `equality_same_value_are_equal` · IM-08, IM-13
- `M:NetworkInspector.Values.MacAddress.GetHashCode` → `impl Hash` — **ported** — tests: `equal_values_hash_equal` · IM-14
- `M:NetworkInspector.Values.MacAddress.op_Equality(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialEq` — **ported** — tests: `equality_same_value_are_equal`
- `M:NetworkInspector.Values.MacAddress.op_Inequality(NetworkInspector.Values.MacAddress,NetworkInspector.Values.MacAddress)` → `impl PartialEq` — **ported** — tests: `inequality_different_values`

## Behavior items

- `BH-01` [L26] constructor keeps only the low 48 bits → `new` — **ported** — tests: `constructor_masks_to_48_bits`
- `BH-02` [L38] broadcast ⇔ raw = `0xFFFF_FFFF_FFFF` → `is_broadcast` — **ported** — tests: `classification_matches_first_octet_bits`
- `BH-03` [L44] multicast ⇔ bit 40 set → `is_multicast` — **ported** — tests: `classification_matches_first_octet_bits`
- `BH-04` [L56] local ⇔ bit 41 set; global = not local; unicast = not multicast → `is_local`, `is_global`, `is_unicast` — **ported** — tests: `classification_matches_first_octet_bits`
- `BH-05` [L83] fewer than 6 bytes → failure; extra bytes ignored → `from_bytes` `Err(ValueError::ShortInput)` — **ported** — tests: `try_from_bytes_short_span_returns_false`, `from_bytes_ignores_trailing_bytes`
- `BH-06` [L84] failure leaves `address = default` → — **n/a** (IM-05: no out value exists on `Err`)
- `BH-07` [L104] `FromBytes` short input throws `ArgumentException` → `Err(ValueError::ShortInput)` — **ported** — tests: `from_bytes_short_span_throws` · IM-03
- `BH-08` [L113] length ≠ 17 UTF-16 units → false → `parse` `Err(InvalidFormat)` on byte length ≠ 17 — **ported** — tests: `try_parse_invalid_addresses_returns_false`, `parse_non_ascii_returns_invalid_format`
  Note: UTF-16 length vs UTF-8 length differs only for non-ASCII input; any non-ASCII character fails the hex or separator check in .NET, and fails the length or hex check in Rust. Outcome is identical; the test covers a 17-unit non-ASCII string.
- `BH-09` [L121] separator at index 2, 5, 8, 11, 14 must be `:` → `parse` — **ported** — tests: `try_parse_invalid_addresses_returns_false`
- `BH-10` [L125] hex digits accept `0-9a-fA-F` only → `parse` — **ported** — tests: `try_parse_valid_addresses`, `try_parse_invalid_addresses_returns_false`
- `BH-11` [L147] `ISpanParsable.Parse` invalid → `FormatException` → `FromStr` `Err(InvalidFormat)` — **ported** — tests: `from_str_invalid_is_invalid_format`
- `BH-12` [L156] `IParsable.TryParse(null)` → false; `IParsable.Parse(null)` → `ArgumentNullException` → — **n/a** (IM-07)
- `BH-13` [L172] `ToBytes` short destination → returns 0, writes nothing → `write_bytes` `Err(BufferTooSmall)`, buffer untouched — **ported** — tests: `write_bytes_short_buffer_is_buffer_too_small` · IM-20
- `BH-14` [L177] 6 bytes big-endian → `write_bytes`, `to_bytes` — **ported** — tests: `to_bytes_big_endian`, `from_bytes_to_bytes_round_trip`
- `BH-15` [L203] written size is always 6 → `written_size` — **ported** — tests: `try_get_written_size_is_6`
- `BH-16` [L212] `TryWrite` short → false, 0 written → `try_write` `Err(BufferTooSmall)` — **ported** — tests: `try_write_short_buffer_is_buffer_too_small`
- `BH-17` [L230] format short destination (< 17) → false, 0 written, nothing written → `format_into` `Err(BufferTooSmall)` — **ported** — tests: `try_format_buffer_too_small_returns_false`, `format_into_short_buffer_writes_nothing`
- `BH-18` [L236] output is upper-case hex with `:` → `format_into` — **ported** — tests: `format_produces_correct_string`, `parse_format_round_trip`
- `BH-19` [L254] format ignores `format` and `provider` arguments → — **n/a** (IM-21: Rust signature has no format or provider parameter; no culture dependence exists)
- `BH-20` [L281] `FormatInto` returns chars written, 0 when short → `format_into` `Err(BufferTooSmall)` — **ported** — tests: `format_into_short_buffer_writes_nothing` · IM-20
- `BH-21` [L297] `FormatTemp` buffer valid until dispose; next call on the same thread overwrites → `format_inline` returns an owned value; no aliasing exists — **ported** — tests: `format_temp_produces_correct_string`, `format_inline_two_values_do_not_alias` · IM-22
- `BH-22` [L338] order follows raw value → `Ord` derive on `u64` — **ported** — tests: `compare_to_ordering`
- `BH-23` [L341] `IComparable.CompareTo(null)` → 1; wrong type → `ArgumentException` → — **n/a** (IM-07, IM-08)
- `BH-24` record equality on raw value; equal values hash equal → derives — **ported** — tests: `equality_same_value_are_equal`, `equal_values_hash_equal`
- `BH-25` `default` is the zero address → `Default` derive — **ported** — tests: `default_is_all_zeros`

## Test rows

- `NetworkInspector.Values.Tests.MacAddressTests.Constructor_MasksTo48Bits` → `constructor_masks_to_48_bits` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.Constructor_ValidValue_Stored` → `constructor_valid_value_stored` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.Default_IsAllZeros` → `default_is_all_zeros` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.TryParse_ValidAddresses("00:11:22:33:44:55",…)` and 3 more rows → `try_parse_valid_addresses` (4 rows) — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.TryParse_InvalidAddresses_ReturnsFalse("")` and 4 more rows → `try_parse_invalid_addresses_returns_false` (5 rows, each asserts `Err(InvalidFormat)`) — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.FromBytes_BigEndian` → `from_bytes_big_endian` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.TryFromBytes_ShortSpan_ReturnsFalse` → `try_from_bytes_short_span_returns_false` — **ported** · second assert (`address == default`) is BH-06 n/a
- `NetworkInspector.Values.Tests.MacAddressTests.FromBytes_ShortSpan_Throws` → `from_bytes_short_span_throws` (asserts `Err(ValueError::ShortInput)`) — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.Format_ProducesCorrectString(…)` 3 rows → `format_produces_correct_string` (3 rows, asserts `to_text()` and `to_string()`) — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.TryFormat_BufferTooSmall_ReturnsFalse` → `try_format_buffer_too_small_returns_false` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.FormatTemp_ProducesCorrectString` → `format_temp_produces_correct_string` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.ParseFormat_RoundTrip(…)` 3 rows → `parse_format_round_trip` (3 rows) — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.Equality_SameValue_AreEqual` → `equality_same_value_are_equal` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.CompareTo_Ordering` → `compare_to_ordering` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.IComparable_CompareTo_Null_Returns1` → — **n/a** (IM-07: no null `MacAddress` exists)
- `NetworkInspector.Values.Tests.MacAddressTests.IComparable_CompareTo_WrongType_Throws` → — **n/a** (IM-08: no object-typed compare; a wrong type does not compile)
- `NetworkInspector.Values.Tests.MacAddressTests.TryGetWrittenSize_Is6` → `try_get_written_size_is_6` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.ToBytes_BigEndian` → `to_bytes_big_endian` — **ported**
- `NetworkInspector.Values.Tests.MacAddressTests.FromBytes_ToBytes_RoundTrip` → `from_bytes_to_bytes_round_trip` — **ported**
- (Rust only) `formatted_length_is_17`, `classification_matches_first_octet_bits`, `from_bytes_ignores_trailing_bytes`,
  `parse_non_ascii_returns_invalid_format`, `from_str_matches_parse`, `from_str_invalid_is_invalid_format`,
  `write_bytes_short_buffer_is_buffer_too_small`, `to_bytes_array_big_endian`, `try_write_short_buffer_is_buffer_too_small`,
  `string_size_is_17`, `format_into_short_buffer_writes_nothing`, `format_inline_two_values_do_not_alias`,
  `equal_values_hash_equal`, `inequality_different_values` — **added**

## Deviations / blockers

- none

## Verification

- `cargo test --manifest-path rust/Cargo.toml -p ni-values --release mac_address` → 33 passed
- `cargo clippy --manifest-path rust/Cargo.toml -p ni-values --all-targets --all-features -- -D warnings` → clean
- Hunt: 40 API IDs = 40 rows · 2 `throw` sites = BH-07, BH-11 (+ null throw BH-12 n/a) · 0 markers · 0 `clone`/`Box`/`Arc`/`unsafe`
```

### What the example teaches

- Two .NET members may point to one Rust item (`TryFromBytes` and `FromBytes` → `from_bytes`). Both rows stay in the books, each with its own tests and the `IM-*` rule.
- A .NET assertion that cannot exist in Rust (`address == default` after failure) is a behavior item with **n/a** and a rule, not a silently dropped line.
- A suspicious difference (UTF-16 vs UTF-8 length) is analyzed in the item and proven by an added test. It is not waved through.
- Optimizations are mapped explicitly: the thread-static `TempString` becomes an owned stack value; the result is cheaper and has no aliasing hazard.
- `ported` with a different form is only possible through a named `IM-*` rule. Without one, the row is `deviated` or `blocked`.

### Counter-example (what the books must never look like)

```markdown
## MacAddress
- Status: **shape** — parse/format/bytes ported, minor API differences
```

It hides: the missing `Ord` and comparison operators, the missing `FromBytes` throw path, unported tests (7 Rust tests for 19 .NET test methods with 15 data rows), the `FormatTemp` mapping, and the null and wrong-type cases. Nobody can tell what is missing.
