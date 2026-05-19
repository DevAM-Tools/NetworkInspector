<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Exporter Implementation Guide

> Canonical reference for implementing, reviewing, and maintaining exporters.
> Derived from the established patterns in `CsvExporter`, `JsonExporter`,
> `PcapngExporter`, `BlfExporter`, and `PbfExporter`.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Exporter Taxonomy](#2-exporter-taxonomy)
3. [Interface Hierarchy](#3-interface-hierarchy)
4. [File & Namespace Organisation](#4-file--namespace-organisation)
5. [Naming Conventions](#5-naming-conventions)
6. [Builder Pattern](#6-builder-pattern)
7. [Lifecycle](#7-lifecycle)
8. [Output Targets](#8-output-targets)
9. [Error Tolerance](#9-error-tolerance)
10. [Caller Contract](#10-caller-contract)
11. [Buffer Management](#11-buffer-management)
12. [Display Text & Formatting](#12-display-text--formatting)
13. [Field Traversal](#13-field-traversal)
14. [Dispose Pattern](#14-dispose-pattern)
15. [Documentation](#15-documentation)
16. [Testing](#16-testing)
17. [Checklist for New Exporters](#17-checklist-for-new-exporters)

---

## 1. Architecture Overview

Exporters implement a **push-based model**: the consumer sends packets or frames
to the exporter via `OnPacket()` / `OnFrame()` callbacks. The exporter serialises
each item to the configured output target (file, stream, or stdout).

```
  ┌──────────────────────────────────────────────────────────┐
  │                    Consumer Thread                        │
  │                                                          │
  │  exporter = XxxExporter.CreateBuilder()                  │
  │      .ToFile("output.xxx")                               │
  │      .WithDescription("...")                              │
  │      .Build();                                           │
  │                                                          │
  │  foreach (var packet in packets)                         │
  │      if (!exporter.OnPacket(packet)) break;              │
  │                                                          │
  │  exporter.OnFinish();                                    │
  │  exporter.Dispose();                                     │
  │                                                          │
  │  // Statistics available after finish:                   │
  │  Console.WriteLine(exporter.WrittenCount);               │
  │  Console.WriteLine(exporter.ErrorCount);                 │
  └──────────────────────────────────────────────────────────┘
```

---

## 2. Exporter Taxonomy

Exporters fall into two categories based on their input granularity:

| Category | Interface | Input | When to use |
|----------|-----------|-------|-------------|
| **Packet-level** | `IPacketListener` | `Packet` (parsed tree) | Formats that need field values (CSV, JSON, PBF) |
| **Frame-level** | `IFrameListener` | `Frame` (raw bytes) | Formats that need raw frame data (PCAPNG, BLF) |

All exporters additionally implement `IErrorTolerantExporter` for error handling
and statistics.

---

## 3. Interface Hierarchy

```
IPacketListener / IFrameListener
├── UiName          : string
├── Description     : string?
├── OnPacket(Packet) → bool  /  OnFrame(Frame) → bool
└── OnFinish()

IExporterStatistics
├── WrittenCount    : long
├── SkippedCount    : long
├── ErrorCount      : long
├── HasErrors       : bool
└── IsFinished      : bool

IErrorTolerantExporter : IExporterStatistics
├── ErrorTolerance  : ErrorToleranceMode   { get; set; }
└── event ItemSkipped : EventHandler<ExportErrorEventArgs>?

IDisposable
└── Dispose()
```

All exporters implement `IDisposable`, the appropriate listener interface, and
`IErrorTolerantExporter`.

---

## 4. File & Namespace Organisation

```
NetworkInspector.Exporters/
  FormatName/                           ← One folder per exporter format
    FormatNameExporter.cs               ← Main exporter + nested Builder class
    FormatNameWriter.cs                 ← Low-level binary/text writer (optional)
    FormatSpecificHelpers.cs            ← Format-specific utilities (optional)
    README.md                           ← Format documentation
  Shared/                               ← Cross-exporter utilities (if needed)
    ExportOutput.cs                     ← Output target abstraction
    PooledBuffer.cs                     ← Growable byte buffer
    SameFlags.cs                        ← Same-as-previous bitmask constants
```

**Namespace:** `NetworkInspector.Exporters.<FormatName>`

Shared utilities live in the root `NetworkInspector.Exporters` namespace.

---

## 5. Naming Conventions

| Element | Pattern | Examples |
|---------|---------|----------|
| Exporter class | `XxxExporter` | `CsvExporter`, `JsonExporter` |
| Builder class | `XxxExporter.Builder` | `CsvExporter.Builder` |
| Writer class | `XxxWriter` | `PcapngWriter`, `BlfWriter` |
| Static factory | `CreateBuilder()` | — |
| Builder methods | `WithXxx()` or `Xxx()` | `WithUiName()`, `WithDescription()` |
| Output methods | `ToFile()`, `ToStream()`, `ToStdout()` | — |
| Build method | `Build()` | — |
| Display name | `UiName` | — |
| Format enum | `XxxExportFormat` | `PbfExportFormat` |
| Options types | `XxxColumnKind`, `XxxColumnDefinition` | `CsvColumnKind` |

### Builder Method Naming

**Use `With` prefix consistently** for all builder configuration methods:

```csharp
// ✅ CORRECT — consistent With prefix
builder.WithUiName("My Export")
       .WithDescription("Export for analysis")
       .WithCancellationToken(token)
       .WithTargetPacketCount(1000)
       .ToFile("output.csv")
       .Build();
```

---

## 6. Builder Pattern

All exporters use a **nested sealed `Builder` class** with a fluent API.
The exporter constructor is `private` — instances are created exclusively
through the builder.

### 6.1 Structure

```csharp
public sealed class XxxExporter : IPacketListener, IErrorTolerantExporter, IDisposable
{
    // Private constructor — only accessible by Builder.
    // Accept explicit parameters rather than the Builder object to keep the
    // exporter fields independent of the builder's internal state.
    private XxxExporter(
        ExportOutput output,
        string uiName,
        string? description,
        CancellationToken cancellationToken,
        long targetPacketCount)
    {
        _Output = output;
        UiName = uiName;
        Description = description;
        _CancellationToken = cancellationToken;
        _TargetPacketCount = targetPacketCount;
        // ... format-specific fields
    }

    /// <summary>Creates a new builder for configuring and constructing an exporter.</summary>
    public static Builder CreateBuilder() => new();

    /// <summary>Builder for <see cref="XxxExporter"/>. Not thread-safe.</summary>
    public sealed class Builder
    {
        private ExportOutput? _Output;
        private string _UiName = "XXX Exporter";
        private string? _Description;
        private CancellationToken _CancellationToken;
        private long _TargetPacketCount;

        // --- Output target (exactly one required) ---

        /// <summary>Writes to a file at the given path.</summary>
        public Builder ToFile(string path)
        {
            _Output = ExportOutput.File(path);
            return this;
        }

        /// <summary>Writes to an existing stream. Caller retains stream ownership.</summary>
        public Builder ToStream(Stream stream)
        {
            _Output = ExportOutput.FromStream(stream);
            return this;
        }

        /// <summary>Writes to stdout.</summary>
        public Builder ToStdout()
        {
            _Output = ExportOutput.Stdout();
            return this;
        }

        // --- Common configuration ---

        /// <summary>Sets the display name shown in UI and logs.</summary>
        public Builder WithUiName(string uiName)
        {
            _UiName = uiName;
            return this;
        }

        /// <summary>Sets an optional description.</summary>
        public Builder WithDescription(string description)
        {
            _Description = description;
            return this;
        }

        /// <summary>Sets a cancellation token to abort the export.</summary>
        public Builder WithCancellationToken(CancellationToken token)
        {
            _CancellationToken = token;
            return this;
        }

        /// <summary>Stops after writing the specified number of items.</summary>
        public Builder WithTargetPacketCount(long count)
        {
            _TargetPacketCount = count;
            return this;
        }

        // --- Format-specific configuration ---

        // ...

        /// <summary>Validates configuration and creates the exporter.</summary>
        public XxxExporter Build()
        {
            if (_Output is null)
            {
                throw new InvalidOperationException(
                    "Output target is required. Call ToFile(), ToStream(), or ToStdout().");
            }

            return new XxxExporter(
                _Output,
                _UiName,
                _Description,
                _CancellationToken,
                _TargetPacketCount);
        }
    }
}
```

### 6.2 Rules

1. **Output target is mandatory.** `Build()` must throw `InvalidOperationException` if
   no output was configured.
2. **One output method per builder.** Calling `ToFile()` after `ToStream()` replaces
   the previous output (last-write-wins).
3. **Builder is not thread-safe.** Configuration happens on a single thread before
   calling `Build()`.
4. **Builder is single-use.** Do not reuse a builder after `Build()`.
5. **All common options** (`WithUiName`, `WithDescription`, `WithCancellationToken`,
   `WithTargetPacketCount`) should be available on every exporter builder unless the
   format truly cannot support them (document why).

---

## 7. Lifecycle

### 7.1 States

```
  Created  ──▶  Started  ──▶  Finished/Disposed
     │              │              ▲
     │              │              │
     │              └──── OnFinish / Dispose
     │
     └──── OnFinish (empty export)
```

### 7.2 Lazy Initialisation

Exporters use **lazy initialisation**: the output stream and writer are opened on
the **first `OnPacket` / `OnFrame` call**, not in `Build()`. This prevents creating
empty files when the export produces no items.

```csharp
private bool Start()
{
    _Started = true;
    Stream? stream = _Output?.GetOrCreateUnderlyingStream();

    if (stream is null)
    {
        _HasError = true;
        return false;
    }

    _Writer = new XxxWriter(stream);
    _Writer.WriteHeader();
    return true;
}
```

### 7.3 OnPacket / OnFrame

The callback returns `bool`:
- `true`: continue receiving items.
- `false`: unsubscribe (stop receiving items).

Return `false` when:
- `_Finished` is already true (guard at the top).
- A fatal error occurs (`_HasError = true`).
- `CancellationToken` is cancelled.
- `TargetPacketCount` is reached.

### 7.4 OnFinish

- Acquires the lock.
- Checks `if (_Finished) return;` — double-finish is safe.
- If never started: either produce an empty-but-valid file (preferred) or skip output.
- Writes any trailing data (footers, trailers).
- Flushes and closes the output.
- Sets `_Finished = true`.

### 7.5 Empty Export Behaviour

When `OnFinish()` is called without any items being written, the exporter should
produce a **valid but empty** output file where the format supports it:

| Format | Empty export result |
|--------|-------------------|
| CSV | May produce header-only file or 0-byte file |
| JSON | Valid `[]` or `{}` |
| PCAPNG | Valid SHB block |
| BLF | Valid LOGG file header |
| PBF | Valid magic + header + trailer + magic |

---

## 8. Output Targets

Use `ExportOutput` as the abstraction for output targets:

```csharp
ExportOutput.File(path)         // FileStream + BufferedStream (4 MiB), owns stream
ExportOutput.FromStream(stream) // Wraps existing stream, caller retains ownership
ExportOutput.Stdout()           // Console.OpenStandardOutput(), owns stream
```

**Rules:**
- Store `ExportOutput?` (nullable) — it becomes available after `Build()`.
- Access the underlying stream via `_Output.GetOrCreateUnderlyingStream()` in `Start()` — this creates the file on demand.
- To inspect the stream without creating it (e.g. for finalization calls), use `_Output.TryGetExistingStream()` — returns `null` if no data has been written yet.
- Dispose `_Output` in `OnFinish` / `Dispose` — this correctly handles ownership.
- After disposing, set `_Output = null` to prevent double-dispose.

---

## 9. Error Tolerance

All exporters must implement `IErrorTolerantExporter`.

### 9.1 Error Handling in OnPacket / OnFrame

**Every exporter must wrap the serialisation path in a try/catch:**

```csharp
private bool HandlePacket(Packet packet)
{
    try
    {
        // Serialise the packet
        _Writer!.Write(packet);
        WrittenCount++;
        return true;
    }
    catch (Exception ex)
    {
        return HandleError(ex, WrittenCount + SkippedCount);
    }
}
```

### 9.2 HandleError / HandleSkip Pattern

Centralise error handling in a `HandleSkip` or `HandleError` method:

```csharp
private bool HandleSkip(ExportErrorKind kind, string message, long itemIndex)
{
    ErrorCount++;
    SkippedCount++;

    if (ErrorTolerance == ErrorToleranceMode.Strict)
    {
        _HasError = true;
        return false;   // Stop the export
    }

    // Tolerant mode: fire event and continue
    ItemSkipped?.Invoke(this, new ExportErrorEventArgs
    {
        ItemIndex = itemIndex,
        Kind = kind,
        Message = message,
    });
    return true;        // Continue the export
}
```

### 9.3 Rules

1. **Never let exceptions propagate** from `OnPacket` / `OnFrame`. Catch all
   exceptions (including `IOException`, serialisation errors, `NullReferenceException`).
2. **Distinguish I/O errors from data errors.** Use appropriate `ExportErrorKind` values.
3. **Strict mode aborts immediately.** Set `_HasError = true` and return `false`.
4. **Tolerant mode skips and continues.** Fire `ItemSkipped` event with details.
5. **Increment counters with plain `++`**: `ErrorCount++`, `SkippedCount++`. No `Interlocked` is needed because exporters are single-threaded.

---

## 10. Caller Contract

### 10.1 Single-threaded access

Exporters are **not thread-safe**. `OnPacket` / `OnFrame` and `OnFinish` must be
called **sequentially from a single thread**. The caller is responsible for
synchronization if an exporter is ever driven from multiple threads.

This contract matches every existing built-in call site (CLI, MCP scanner,
Playground) — all of which call `OnPacket` / `OnFrame` sequentially — and avoids
unnecessary locking overhead in the common case.

### 10.2 Counter access

| Operation | Pattern |
|-----------|--------|
| Increment `WrittenCount` | `WrittenCount++` |
| Increment `SkippedCount` | `SkippedCount++` |
| Increment `ErrorCount` | `ErrorCount++` |
| Read counters (getters) | plain property read |

### 10.3 State fields

| Field | Write | Read |
|-------|-------|------|
| `_Finished` | `_Finished = true` in `OnFinish` | `_Finished` in `IsFinished` |
| `_Started` | `_Started = true` on lazy init | `!_Started` guard in `OnPacket` / `OnFrame` |
| `_HasError` | `_HasError = true` in error paths | `_HasError` in `IsFinished` |

### 10.4 IsFinished semantics

`IsFinished` reflects the **logical completion state** — the export is considered
finished when any of these conditions hold:

```csharp
public bool IsFinished =>
    _Finished ||
    _HasError ||
    _CancellationToken.IsCancellationRequested ||
    (_TargetPacketCount > 0 && WrittenCount >= _TargetPacketCount);
```

If the exporter also implements `IExporterStatistics.IsFinished` explicitly,
that property must delegate to `IsFinished` so both surfaces always agree.

---

## 11. Buffer Management

### 11.1 PooledBuffer

`PooledBuffer` is a growable byte buffer for building binary payloads without
per-item allocations:

```csharp
PooledBuffer buffer = new();

// Write data
buffer.Write(headerBytes);
buffer.WriteByte(0x00);
Span<byte> reserved = buffer.Reserve(4);
BinaryPrimitives.WriteUInt32BigEndian(reserved, value);

// Use the built content
ReadOnlySpan<byte> content = buffer.WrittenSpan;

// Reset for next item (keeps allocated array)
buffer.Reset();

// Release array on shutdown
buffer.Return();
```

**Rules:**
- Call `Reset()` between items (reuses the array).
- Call `Return()` in `OnFinish` / `Dispose` to release the array.
- Do not call `Return()` and then continue using the buffer.

### 11.2 Avoid Allocations

- Use `PooledBuffer` or `Span<byte>` for binary serialisation.
- Use `stackalloc` for small temporary buffers (≤ 256 bytes).
- Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` for small hot-path methods.

### 11.3 Instance scratch buffers vs `ArrayPool<byte>`

Because exporters are single-threaded (see Section 10), UTF-8 encoding scratch
buffers are stored as **private instance fields** (`private byte[]? _Utf8Scratch`)
rather than `[ThreadStatic]` statics. The buffer grows to the maximum length ever
needed by that exporter instance and is reused without allocation for subsequent
items.

Use **`ArrayPool<byte>.Shared.Rent`** when the buffer lifetime crosses components,
spans **zlib/LZ4** framing that needs a contiguous backing array tied to **`Stream`**
APIs, or when the upper bound size is unpredictable until runtime. Prefer
**`PooledBuffer`** / **`Utf8Formatter` + stackalloc** for sequential exporter rows
(CSV, text, JSON payloads). Source-side BLF **zlib** staging uses **`ArrayPool`** to
avoid a full extra heap copy of compressed spans.

### 11.4 BLF `start_date` and relative timestamps

The LOGG **`start_date`** field is **`SYSTEMTIME` in millisecond granularity** using
local-time components (matching Vector tooling / **`tshark`** expectations). Relative
LOB object times are **`10 μs`** ticks. The writer **floors the anchor `startNs`**
to whole milliseconds so sub-ms residuals live only in offsets. **`BlfExporter`**
tracks **min/max epoch nanoseconds**: the anchor aligns to the earliest **ordered**
revision possible before first written object (**`TryRealignStartEarlier`** on a
seekable stream), **`measurement_end`** uses the **maximum** timestamp, out-of-order
frames after data exists **clamp** negative relative ticks (**`BLF`** cannot express
them) and **`ItemSkipped`** fires **once per export** when that happens.

---

## 12. Display Text & Formatting

### 12.1 LazyString

Use `LazyString.FormatLazy()` for deferred formatting in field display text.
Prefer static lookup tables for known value domains.

### 12.2 Field Value Formatting

When converting `FieldValue` to a string representation, use a shared utility
method rather than duplicating the formatting logic in each exporter. The conversion
should handle all `FieldType` variants:

- `I64`, `U64`, `F64` → numeric string
- `String` → direct value
- `Bool` → `"true"` / `"false"`
- `Timestamp` → ISO 8601 or epoch string
- `Bytes` → Base64 or hex
- `MacAddress`, `IPv4Address`, `IPv6Address`, `Eui64`, `Uuid` → standard notation

---

## 13. Field Traversal

### 13.1 Zero-alloc enumeration

`Field.Children()` returns `FieldChildEnumerable` and `Field.Descendants()` returns
`FieldDescendantEnumerable`. Both types expose a **public non-interface**
`GetEnumerator()` that returns a `ref struct`:

| Method | Return type | Enumerator type | Allocation |
|--------|-------------|-----------------|------------|
| `field.Children()` | `FieldChildEnumerable` | `ref struct FieldChildEnumerator` | zero |
| `field.Descendants()` | `FieldDescendantEnumerable` | `ref struct FieldDescendantEnumerator` | zero (inline stack ≤ 16 deep) |

C# `foreach` uses **duck-typed pattern matching** — it calls the public `GetEnumerator()`
method, not the explicit `IEnumerable<Field>` interface. This resolves to the
`ref struct` enumerator, which is allocated on the **C# stack**. No boxing,
no heap allocation on the hot path.

### 13.2 When to use each

| Scenario | API to use | Why |
|----------|-----------|-----|
| Format requires nesting (JSON, PBF rows, indented text) | Recursive `field.Children()` | Each level generates its own output; depth is tracked via call stack |
| Flat DFS over all descendants (columnar PBF, field lookup) | `field.Descendants()` | Single `FieldDescendantEnumerator` with `InlineStack16`; avoids per-level stack frame growth |

### 13.3 Rules

1. **Always iterate via `foreach (Field child in x.Children())` or
   `foreach (Field f in x.Descendants())`** — the duck-typed path calls the
   zero-alloc `ref struct` enumerator.
2. **Never assign to `IEnumerable<Field>`** — this forces boxing through the
   allocating `BoxedEnumerator` class.
3. **Never call LINQ on `Children()` / `Descendants()`** — LINQ operates on
   `IEnumerable<T>` and boxes the enumerator.
4. **Use `Descendants()` when depth information is not required** — simpler code
   and avoids recursion.
5. **Use `ChildCount`** when you need the number of direct children without iterating.

---

## 14. Dispose Pattern

### 13.1 Implementation

Exporters implement `IDisposable` by delegating to `OnFinish()`:

```csharp
public void Dispose() => OnFinish();
```

This ensures:
- If the consumer forgets to call `OnFinish()`, `Dispose()` triggers it.
- If `OnFinish()` was already called, the double-finish guard prevents duplicate work.
- Resources (streams, buffers) are always released.

### 13.2 Rules

1. `Dispose()` **must** delegate to `OnFinish()` — do not implement separate cleanup.
2. `OnFinish()` must be idempotent — safe to call multiple times.
3. After `OnFinish()` / `Dispose()`, subsequent `OnPacket()` / `OnFrame()` calls
   return `false` immediately.
4. Release all managed resources: call `_Output?.Dispose()`, `_Buffer.Return()`.

---

## 15. Documentation

### 15.1 README

Every exporter folder must contain a `README.md` with:
- Format description and purpose.
- File structure / layout (for binary formats).
- Builder configuration options.
- Usage examples.
- Limitations and known issues.

### 15.2 XML Documentation

- All public types and members must have XML doc comments.
- Types must state whether they are thread-safe.
- The exporter class doc should describe the overall format and refer to the README.

---

## 16. Testing

### 16.1 Test Organisation

Tests live in `NetworkInspector.Exporters.Tests` with one test file per exporter:

```
NetworkInspector.Exporters.Tests/
  Generators/                        ← Shared test data generators
    FrameGenerators.cs
    PacketGenerators.cs
    SocketCanGenerators.cs
    FlexRayGenerators.cs
    LinGenerators.cs
  Verification/                      ← Format-specific output verifiers
    PcapngVerifier.cs
    BlfStructuralVerifier.cs
    JsonVerifier.cs
    PbfVerifier.cs
  CsvExporterTests.cs
  JsonExporterTests.cs
  PcapngExporterTests.cs
  BlfExporterTests.cs
  PbfExporterTests.cs
  TestHarness.cs                     ← Shared protocol stack singleton
  TestDir.cs                         ← Temporary directory with auto-cleanup
```

> **Note:** `TsharkVerifier` lives in the separate `NetworkInspector.Testing.Tshark` project, not
> inside `NetworkInspector.Exporters.Tests`.

### 16.2 Required Test Scenarios

Every exporter **must** have tests for:

| Category | Scenario | Description |
|----------|----------|-------------|
| **Builder** | Requires output target | `Build()` throws without `ToFile/ToStream/ToStdout` |
| **Basic write** | Single item → verify output | Write one packet/frame, verify the output format |
| **Multi-item** | Multiple items → verify count | Write several items, check `WrittenCount` |
| **Empty export** | No items → valid empty output | Call `OnFinish()` without writing items |
| **File output** | Write to file | Verify file is created and valid |
| **Stream output** | Write to MemoryStream | Verify stream content |
| **Cancellation** | Cancel mid-export | Cancel token, verify export stops |
| **Target count** | Stop at N items | Set `WithTargetPacketCount`, verify `IsFinished` |
| **IsFinished** | Property transitions | Verify `IsFinished` is false before, true after finish |
| **Double finish** | OnFinish + OnFinish | Verify idempotent, no exceptions |
| **Statistics** | WrittenCount, ErrorCount | Verify all counter properties |
| **Error tolerance** | Strict mode abort | Verify strict mode stops on first error |
| **Error tolerance** | Tolerant mode skip | Verify tolerant mode fires `ItemSkipped` and continues |
| **I/O error** | Failing stream | Use a stream that throws, verify error handling |
| **UiName/Description** | Property roundtrip | Verify builder values appear on exporter |

### 16.3 External Validation

Where feasible, use external tools to verify exported files:
- **PCAPNG**: `tshark` for structural validation.
- **BLF**: Structural verification via `BlfStructuralVerifier` (header + top-level LOBJ scan; does not expand compressed LOG_CONTAINER blobs).
- **JSON**: `System.Text.Json.JsonDocument` for parse validation.

---

## 17. Checklist for New Exporters

- [ ] Create folder: `NetworkInspector.Exporters/<FormatName>/`
- [ ] Implement exporter class with nested `Builder`
- [ ] Implement appropriate listener interface (`IPacketListener` or `IFrameListener`)
- [ ] Implement `IErrorTolerantExporter` and `IDisposable`
- [ ] Add `WithUiName`, `WithDescription`, `WithCancellationToken`, `WithTargetPacketCount` to builder
- [ ] Use `ExportOutput` for output targets (`ToFile`, `ToStream`, `ToStdout`)
- [ ] Use lazy initialisation (open stream on first item)
- [ ] Implement error tolerance (try/catch in HandlePacket/HandleFrame, `HandleSkip`)
- [ ] Exporters are single-threaded; use plain reads/writes and `++` for counters (see §10)
- [ ] Iterate fields with `foreach (Field f in x.Children())` or `foreach (Field f in x.Descendants())` — never assign to `IEnumerable<Field>` or use LINQ (see §13)
- [ ] Implement `IsFinished` with full compound condition
- [ ] Implement `Dispose()` delegating to `OnFinish()`
- [ ] Call `_Buffer.Return()` and `_Output?.Dispose()` in cleanup
- [ ] Create `README.md` in the exporter folder
- [ ] Add XML doc comments to all public members
- [ ] Write tests for all scenarios in §15.2
- [ ] Add external validation tests where feasible
- [ ] Register in project file if needed
