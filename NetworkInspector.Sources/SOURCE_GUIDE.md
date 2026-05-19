<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# Source Implementation Guide

> Canonical reference for implementing, reviewing, and maintaining frame sources.
> Derived from the established patterns in `PcapSource`, `BlfSource`, `AscSource`,
> `RandomFrameSource`, and `CachedFrameSource`.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Source Taxonomy](#2-source-taxonomy)
3. [Interface Hierarchy](#3-interface-hierarchy)
4. [File & Namespace Organisation](#4-file--namespace-organisation)
5. [Naming Conventions](#5-naming-conventions)
6. [Options Classes](#6-options-classes)
7. [Factory Methods](#7-factory-methods)
8. [Lifecycle](#8-lifecycle)
9. [Interface Registration](#9-interface-registration)
10. [Frame Creation](#10-frame-creation)
11. [Memory Management & Backends](#11-memory-management--backends)
12. [Error Tolerance](#12-error-tolerance)
13. [Thread Safety](#13-thread-safety)
14. [Scan Modes (Full vs Lazy)](#14-scan-modes-full-vs-lazy)
15. [Dispose Pattern](#15-dispose-pattern)
16. [Checklist for New Sources](#16-checklist-for-new-sources)

---

## 1. Architecture Overview

All frame sources follow a **pull-based model**: the consumer calls `NextFrame()` in a loop
until `null` is returned. Random-access sources additionally expose `FrameById()` for
concurrent, thread-safe re-reads.

```
  ┌─────────────────────────────────────────────────────────┐
  │                    Consumer Thread                       │
  │                                                         │
  │  source = XxxSource.Open("file.xxx", options);          │
  │  source.Start(sourceId, registry);                      │
  │                                                         │
  │  while (source.NextFrame() is { } frame)                │
  │      process(frame);                                    │
  │                                                         │
  │  // Concurrent random access from other threads:        │
  │  Frame? f = source.FrameById(id);                       │
  │                                                         │
  │  source.Dispose();                                      │
  └─────────────────────────────────────────────────────────┘
```

---

## 2. Source Taxonomy

Every file format is represented by **two** source classes:

| Variant | Base Interface | Purpose |
|---------|----------------|---------|
| **`XxxSource`** | `IRandomAccessFrameSource` | Random-access file reader. Builds an in-memory frame index. |
| **`XxxStreamSource`** | `IFrameSource` | Forward-only stream reader. No index, minimal memory. |

Both variants implement `IErrorTolerantFrameSource` when the format can encounter parse errors.

Special-purpose sources:

| Source | Purpose |
|--------|---------|
| `RandomFrameSource` | Deterministic synthetic frame generation (testing, benchmarks) |
| `CachedFrameSource` | Decorator that adds `IRandomAccessFrameSource` to any `IFrameSource` |

---

## 3. Interface Hierarchy

```
IFrameSource : IDisposable
├── UiName, Description, EstimatedFrameCount, IsFrameCountTruncated, IsRunning
├── Start(FrameSourceId, FrameInterfaceRegistry)
└── NextFrame() → Frame?

IRandomAccessFrameSource : IFrameSource
└── FrameById(FrameId) → Frame?          // Thread-safe

IFrameSourceStatistics
├── ReadFrameCount, SkippedFrameCount, ErrorCount, HasErrors

IErrorTolerantFrameSource : IFrameSourceStatistics
├── ErrorTolerance { get; set; }
└── event FrameSkipped

IFileSourceOptions
├── ScanMode, PreloadBudget, ErrorTolerance
```

---

## 4. File & Namespace Organisation

```
NetworkInspector.Sources/
  SourceType/                              ← One folder per format
    SourceTypeSource.cs                    ← Random-access implementation
    SourceTypeStreamSource.cs              ← Stream implementation
    SourceTypeSourceOptions.cs             ← Options record
    SourceTypeFrameIndex.cs                ← Frame index (if applicable)
    SourceTypeScanner.cs                   ← Incremental parser/scanner
    Format/                                ← Sub-namespace for format types
      Constants.cs
      HeaderTypes.cs
      Blocks/                              ← Block/record parsers
```

**Namespace:** `NetworkInspector.Sources.<FormatName>`
**Sub-namespace for format internals:** `NetworkInspector.Sources.<FormatName>.Format`

---

## 5. Naming Conventions

| Element | Pattern | Examples |
|---------|---------|----------|
| Random-access source | `XxxSource` | `PcapSource`, `BlfSource`, `AscSource` |
| Stream source | `XxxStreamSource` | `PcapStreamSource`, `BlfStreamSource` |
| Options | `XxxSourceOptions` | `PcapSourceOptions`, `BlfSourceOptions` |
| Frame index | `XxxFrameIndex` or `FrameIndex` | `BlfFrameIndex`, `FrameIndex` |
| Scanner | `XxxIncrementalScanner` | `BlfIncrementalScanner`, `IncrementalScanner` |
| Frame entry struct | `XxxFrameEntry` or `FrameOffset` | `BlfFrameEntry`, `FrameOffset` |
| Display name property | `UiName` | — |
| Factory: file | `Open(path, options?)` | — |
| Factory: bytes | `FromData(byte[], uiName, options?)` | — |
| Factory: stream | `FromStream(stream, uiName, leaveOpen?)` | — |
| Factory: text | `FromText(string, uiName?, options?)` | — |

---

## 6. Options Classes

### 6.1 Interface

All file-based options classes **must** implement `IFileSourceOptions`:

```csharp
public interface IFileSourceOptions
{
    ScanMode ScanMode { get; }
    long? PreloadBudget { get; }
    ErrorToleranceMode ErrorTolerance { get; }
}
```

### 6.2 Immutability

**All option properties must use `init` setters** (not `set`).
Options are configuration snapshots — they must not be mutated after creation.
This prevents accidental state changes to a running source through a shared reference.

```csharp
// ✅ CORRECT — immutable after construction
public sealed class XxxSourceOptions : IFileSourceOptions
{
    public ScanMode ScanMode { get; init; } = ScanMode.Lazy;
    public long? PreloadBudget { get; init; } = 256 * 1024 * 1024; // 256 MiB
    public ErrorToleranceMode ErrorTolerance { get; init; } = ErrorToleranceMode.Tolerant;
    public string? UiName  { get; init; }
}

// ❌ WRONG — mutable options can be changed after Open()
public ScanMode ScanMode { get; set; }
```

### 6.3 Standard Defaults

| Property | Default | Rationale |
|----------|---------|-----------|
| `ScanMode` | `ScanMode.Lazy` | Fast open, index built on demand |
| `PreloadBudget` | `256 * 1024 * 1024` (256 MiB) | Files ≤ budget kept in memory |
| `ErrorTolerance` | `ErrorToleranceMode.Tolerant` | Skip errors, don't abort |
| `UiName` | `null` (derived from filename) | Override display name |

If a format does not support lazy scanning, make `ScanMode` a read-only property
returning `ScanMode.Full` and document why.

---

## 7. Factory Methods

### 7.1 Constructors Are Private

Sources are created exclusively through **static factory methods**.
Constructors are `private` to enforce validation and backend selection.

```csharp
public sealed class XxxSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    // Private constructor
    private XxxSource(/* internal state */) { }

    // Public factories
    public static XxxSource Open(string path, XxxSourceOptions? options = null) { ... }
    public static XxxSource FromData(byte[] data, string uiName, XxxSourceOptions? options = null) { ... }
}
```

### 7.2 Validation in Factories

Factory methods perform upfront validation:

```csharp
public static XxxSource Open(string path, XxxSourceOptions? options = null)
{
    ArgumentNullException.ThrowIfNull(path);
    if (!File.Exists(path))
        throw new FileNotFoundException("File not found.", path);

    XxxSourceOptions opts = options ?? new();

    // Parse file header, validate magic bytes
    // Select backend (in-memory vs mmap vs disk)
    // Build frame index (full scan) or initialise scanner (lazy scan)

    return new XxxSource(/* initialised state */);
}
```

### 7.3 Factory Naming

| Factory | Parameters | Purpose |
|---------|------------|---------|
| `Open` | `string path, XxxSourceOptions?` | File-based. Validates existence, selects backend. |
| `FromData` | `byte[] data, string uiName, XxxSourceOptions?` | In-memory. For tests, WASM, embedded. |
| `FromText` | `string text, string? uiName, XxxSourceOptions?` | Text formats only (ASC). |
| `FromStream` | `Stream stream, string uiName, bool leaveOpen = false` | Stream sources only. |

---

## 8. Lifecycle

### 8.1 State Machine

```
  ┌──────────┐     Open()      ┌───────────┐    Start()     ┌─────────┐
  │ (created)│ ──────────────► │  Opened    │ ────────────► │ Running │
  └──────────┘                 └───────────┘                └────┬────┘
                                                                 │
                                          NextFrame() returns null │
                                            or Dispose() called   │
                                                                 ▼
                                                           ┌──────────┐
                                                           │ Disposed │
                                                           └──────────┘
```

### 8.2 Start()

`Start()` initialises runtime state and registers interfaces:

```csharp
public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
{
    // Store source identity and registry
    _SourceId = sourceId;
    _Registry = registry;

    // Register all discovered interfaces (file sources with full/lazy-complete scan)
    RegisterDiscoveredInterfaces();

    // Reset iteration state
    _NextFrameIndex = 0;

    // Mark started — MUST use Volatile for cross-thread visibility
    Volatile.Write(ref _Started, true);
}
```

### 8.3 NextFrame()

```csharp
public Frame? NextFrame()
{
    // Guard: disposal check first
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

    // Guard: not started
    if (!Volatile.Read(ref _Started))
        throw new InvalidOperationException($"{UiName} has not been started.");

    // Guard: aborted (strict mode error)
    if (Volatile.Read(ref _Aborted))
        return null;

    // Read next frame from format-specific backend
    // ...

    // Increment counters via Interlocked
    Interlocked.Increment(ref _ReadFrameCount);

    return frame;
}
```

### 8.4 FrameById() (Random-Access Sources Only)

```csharp
public Frame? FrameById(FrameId id)
{
    // Guard: disposal check
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

    // Guard: ID in range
    if (id.Value < 0 || id.Value >= _Index.Count)
        return null;

    // Read from index + backend (thread-safe)
    // ...

    return frame;
}
```

---

## 9. Interface Registration

### 9.1 Random-Access Sources (Pre-Registration)

**Pre-register all interfaces in `Start()`** after the full scan is complete.
This avoids locking in `FrameById()`:

```csharp
// In Start():
foreach ((LinkType linkType, int channel) in _DiscoveredInterfaces)
{
    FrameInterfaceId id = registry.Register(new FrameInterfaceInfo(linkType, channel, _SourceId));
    _InterfaceMap[(linkType, channel)] = id;
}
```

**If lazy scan is active**, interfaces may be discovered during `NextFrame()`.
Register them as they appear and store in the interface map.
Since `NextFrame()` is single-threaded, no locking is needed for registration.

### 9.2 Stream Sources (Lazy Registration)

Stream sources register interfaces on first encounter:

```csharp
// In NextFrame() — single-threaded, no lock needed:
if (!_InterfaceMap.TryGetValue(key, out FrameInterfaceId interfaceId))
{
    interfaceId = _Registry.Register(new FrameInterfaceInfo(linkType, channel, _SourceId));
    _InterfaceMap[key] = interfaceId;
}
```

### 9.3 Thread Safety Rule

- **`NextFrame()`** → single-threaded → interface registration needs no lock
- **`FrameById()`** → concurrent → interface lookup only (pre-registered), no modification
- If `FrameById()` can trigger registration (lazy scan incomplete), use a lock

---

## 10. Frame Creation

All sources create frames through `Frame.Create()`:

```csharp
ReadOnlyMemory<byte> data = /* extracted from backend */;
Timestamp timestamp = new(timestampNanos);
FrameId frameId = new(sequentialIndex);
LinkType linkType = /* resolved from format metadata */;
FrameInterfaceId interfaceId = _InterfaceMap[key];

ParseResult<Frame> result = Frame.Create(
    frameId,
    timestamp,
    data,
    linkType,
    interfaceId,
    _Registry
);

if (!result.IsSuccess)
{
    // Handle error: skip frame, raise FrameSkipped event
    HandleSkip(frameIndex, fileOffset, FrameReadErrorKind.Other, result.Error);
    return null; // or continue to next
}

return result.Value;
```

**Rules:**
- Never construct `Frame` directly — always use `Frame.Create()`
- Handle `ParseResult` failures — do not discard errors silently
- The `FrameId` value is the zero-based sequential index of the frame

---

## 11. Memory Management & Backends

### 11.1 Backend Decision Tree

```
File-based source:
  └─ fileSize ≤ PreloadBudget?
     ├─ YES: In-Memory backend
     │  • Load entire file into byte[] or string[]
     │  • FrameById: slice from array (zero-copy)
     │  • Pro: fastest access, no I/O during FrameById
     │
     └─ NO: External backend
        ├─ Binary formats: Memory-Mapped (MemoryMappedFile)
        │  • Primary accessor for sequential scan
        │  • Slot-based pool for concurrent FrameById
        │  • slots = Math.Clamp(ProcessorCount, 1, 256)
        │
        └─ Text formats: Disk-Based (seek + read)
           • Index stores byte offsets per line
           • FrameById: FileStream seek + ReadLine
           • Memory ∝ line count, not file size
```

### 11.2 Buffer Sizes

| Context | Default | Source |
|---------|---------|--------|
| PreloadBudget | 256 MiB (64-bit) / 64 MiB (32-bit) | All file sources |
| Disk read buffer | 4 MiB | AscSource, AscStreamSource |
| Container cache (2Q) | 32 MiB | BlfSource |
| Mmap slot count | `ProcessorCount` | PcapSource, BlfSource |

### 11.3 Cache Pattern (BLF)

BLF uses a **2Q (Two-Queue) eviction cache** for decompressed containers:
- Prevents scan pollution (full-file scan doesn't evict hot data)
- The `TwoQueueCache` type is **not** thread-safe; `BlfSource` wraps every `TryGet`/`Put`/`Clear`
  in `_ContainerCacheLock` so concurrent `FrameById` calls cannot corrupt the cache.
- Sized by `CacheBudget` option

---

### 11.4 Large-File Support (Windowed Scanning)

Every scanner (incremental or one-shot) **must** use windowed I/O to support files larger than 2 GiB.

#### Why a whole-file span does not work

`ReadOnlySpan<byte>` uses an `int` length, so the maximum span is ≈ 2.15 GiB.
Passing the entire file as a single span to a scanner breaks for any file that exceeds this limit.

#### Required pattern

The scanner holds a backend reference (`DataBackend` / `BlfDataBackend`) and fetches
**one block or record per call** via the windowed accessor:

```csharp
// PCAPNG / PCAP — DataBackend
ReadOnlySpan<byte> block = _Backend.GetScanSpan(_Offset, (int)blockLength);

// BLF — BlfDataBackend
ReadOnlySpan<byte> obj = _Backend.GetSpan(_FileOffset, skipDistance);
```

For mmap backends `GetScanSpan` / `GetSpan` both use pointer + `long` arithmetic internally
(`_PrimaryPtr + offset`) so they correctly address any byte in the file regardless of size.

#### Rules

1. **Never** receive the entire file as a `ReadOnlySpan<byte>` parameter.
2. **Never** cast `_Offset` / `_FileOffset` (`long`) to `int` for span slicing — always pass
   the `long` offset directly to `GetScanSpan` / `GetSpan`.
3. Individual block/record lengths are 32-bit fields in every currently supported format
   (PCAPNG `block_total_length`, legacy PCAP `incl_len`, BLF `objectLength`), so a single
   block is always within `int` range.
4. Use a `MaxBlockReadSize` constant (recommended: 512 MiB) as a defensive cap to prevent
   OOM from corrupt headers claiming absurdly large block sizes.
5. Boundary conditions (`_Offset + blockLength > _Backend.FileSize`) must be tested as
   `long` comparisons — never cast to `int` first.

#### Concrete checklist

- [ ] Scanner constructor takes backend + `long fileSize` — **not** a `ReadOnlySpan<byte>`.
- [ ] `_Offset` / `_FileOffset` field type is `long`.
- [ ] No `int offset = (int)_Offset` or `(int)_FileOffset` casts at scan-loop level.
- [ ] Source factory methods (`Open`, `FromData`) do not pass a whole-file span to the scanner.
- [ ] No explicit `fileSize > int.MaxValue` guard in the source's `Open()` method.
- [ ] `MaxBlockReadSize` constant present in the scanner; applied before every `GetScanSpan` / `GetSpan` call.
- [ ] Corruption-recovery helpers (`ScanForMagic`, etc.) use the backend directly, not a cached span.

#### The only remaining limit

The only practical upper bound is available virtual address space for the mmap primary view.
On 64-bit processes this is effectively unlimited for capture files.

---

## 12. Error Tolerance

### 12.1 Modes

| Mode | Behaviour |
|------|-----------|
| `Tolerant` (default) | Skip errored frames, raise `FrameSkipped`, continue |
| `Strict` | Set `_Aborted`, stop reading, `NextFrame()` returns `null` |

### 12.2 HandleSkip Pattern

Every source with error tolerance must implement a consistent `HandleSkip` method.
`FrameSkipped` is always raised regardless of the tolerance mode so that callers can
observe skipped frames in both strict and tolerant scenarios. In strict mode the
abort flag is additionally set after the event fires.

```csharp
private void HandleSkip(int frameIndex, long fileOffset, FrameReadErrorKind kind, string message)
{
    // Always count errors
    Interlocked.Increment(ref _ErrorCount);
    Interlocked.Increment(ref _SkippedFrameCount);

    // Always raise the event so callers can observe skipped frames in both modes
    FrameSkipped?.Invoke(this, new FrameReadErrorEventArgs
    {
        FrameIndex = frameIndex,
        FileOffset = fileOffset,
        Kind = kind,
        Message = message,
    });

    if (ErrorTolerance == ErrorToleranceMode.Strict)
    {
        // Strict: abort after notifying the caller
        Volatile.Write(ref _Aborted, true);
    }
}
```

### 12.3 Counter Requirements

| Counter | Increment Method | Visibility |
|---------|-----------------|------------|
| `_ReadFrameCount` | `Interlocked.Increment` | `Volatile.Read` in getter |
| `_SkippedFrameCount` | `Interlocked.Increment` | `Volatile.Read` in getter |
| `_ErrorCount` | `Interlocked.Increment` | `Volatile.Read` in getter |

### 12.4 Statistics Properties

```csharp
public long ReadFrameCount => Volatile.Read(ref _ReadFrameCount);
public long SkippedFrameCount => Volatile.Read(ref _SkippedFrameCount);
public long ErrorCount => Volatile.Read(ref _ErrorCount);
public bool HasErrors => Volatile.Read(ref _ErrorCount) > 0;
```

---

## 13. Thread Safety

### 13.1 Threading Model

| Method | Threading | Synchronisation |
|--------|-----------|-----------------|
| `Start()` | Single caller | None needed |
| `NextFrame()` | Single-threaded | None needed (sequential iteration) |
| `FrameById()` | **Multi-threaded** | Required (see 13.2) |
| `Dispose()` | Single caller | Idempotent via `_Disposed` flag |
| `IsRunning` | Any thread | `Volatile.Read` |
| Statistics | Any thread | `Volatile.Read` / `Interlocked` |

### 13.2 Volatile Fields (Mandatory)

All cross-thread observable fields must use `Volatile.Read`/`Volatile.Write`:

```csharp
private bool _Started;
private bool _Disposed;
private bool _Aborted;
private long _ReadFrameCount;
private long _SkippedFrameCount;
private long _ErrorCount;
```

**Rules:**
- **Write** lifecycle flags with `Volatile.Write(ref _Flag, value)`
- **Read** lifecycle flags with `Volatile.Read(ref _Flag)`
- **Increment** counters with `Interlocked.Increment(ref _Counter)`
- **Read** counters with `Volatile.Read(ref _Counter)`
- **Never** mix direct assignment/reads with Volatile — be symmetric

```csharp
// ✅ CORRECT — symmetric Volatile usage
Volatile.Write(ref _Aborted, true);          // write
if (Volatile.Read(ref _Aborted)) return null; // read

// ❌ WRONG — asymmetric (write without Volatile, read with Volatile)
_Aborted = true;                              // write — broken!
if (Volatile.Read(ref _Aborted)) return null; // read
```

### 13.3 IsRunning Property

```csharp
public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);
```

---

## 14. Scan Modes (Full vs Lazy)

### 14.1 Full Scan (`ScanMode.Full`)

- File is scanned completely in the factory method (`Open`)
- Full frame index is built before `Start()` returns
- All interfaces are discovered and registered in `Start()`
- `EstimatedFrameCount` returns exact count immediately

### 14.2 Lazy Scan (`ScanMode.Lazy`)

- Factory method only parses the file header
- Frame index grows incrementally during `NextFrame()`
- Interfaces may be registered during `NextFrame()` as they are discovered
- `EstimatedFrameCount` may be `null` until scan completes

### 14.3 Implementation Pattern

```csharp
public Frame? NextFrame()
{
    // ... guards ...

    if (_Scanner is not null)
    {
        // Lazy mode: advance scanner, index frame, register interface if new
        return NextFrameFromScanner();
    }
    else
    {
        // Full mode: read from pre-built index
        return NextFrameFromIndex();
    }
}
```

---

## 15. Dispose Pattern

### 15.1 Standard Pattern

```csharp
public void Dispose()
{
    // Idempotent: only dispose once
    if (Volatile.Read(ref _Disposed))
        return;

    Volatile.Write(ref _Disposed, true);

    // Release unmanaged resources
    _MmapPool?.Dispose();
    _FileStream?.Dispose();
    _Scanner?.Dispose();

    // Suppress finalizer (no destructor needed if only managed/OS resources)
    GC.SuppressFinalize(this);
}
```

### 15.2 Stream Sources: leaveOpen

```csharp
public void Dispose()
{
    if (Volatile.Read(ref _Disposed))
        return;

    Volatile.Write(ref _Disposed, true);

    if (!_LeaveOpen)
        _Stream?.Dispose();
}
```

### 15.3 ObjectDisposedException Guards

**Every public method** must check disposal:

```csharp
ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);
```

---

## 16. Checklist for New Sources

### Source Class (XxxSource)

- [ ] Implements `IRandomAccessFrameSource`, `IErrorTolerantFrameSource`
- [ ] Private constructor, public static factory methods (`Open`, `FromData`)
- [ ] Factory validates arguments (`ArgumentNullException`, `FileNotFoundException`)
- [ ] Factory parses file header and selects backend
- [ ] `Start()` registers interfaces, sets `_SourceId`, `_Registry`, `Volatile.Write(ref _Started, true)`
- [ ] `NextFrame()` checks `_Disposed`, `_Started`, `_Aborted` with `Volatile.Read`
- [ ] `FrameById()` is thread-safe (pre-registered interfaces, locked backend access)
- [ ] `HandleSkip()` follows standard pattern (Interlocked counters, Volatile abort flag)
- [ ] `Dispose()` is idempotent via `Volatile.Write(ref _Disposed, true)`
- [ ] Counters use `Interlocked.Increment`, getters use `Volatile.Read`
- [ ] `IsRunning` uses `Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed)`
- [ ] All lifecycle flags (`_Started`, `_Disposed`, `_Aborted`) use symmetric `Volatile`

### Stream Source Class (XxxStreamSource)

- [ ] Implements `IFrameSource`, `IErrorTolerantFrameSource`
- [ ] Public constructor or `FromStream` factory
- [ ] `leaveOpen` parameter for stream ownership
- [ ] `Start()` sets `_SourceId`, `_Registry`, `Volatile.Write(ref _Started, true)`
- [ ] `NextFrame()` checks `_Disposed`, `_Started` with `Volatile.Read`
- [ ] Lazy interface registration (no lock needed — single-threaded)
- [ ] `Dispose()` respects `_LeaveOpen` flag
- [ ] Same HandleSkip and counter patterns as file source

### Options Class (XxxSourceOptions)

- [ ] Implements `IFileSourceOptions`
- [ ] All properties use `init` setters (immutable after construction)
- [ ] `ScanMode` defaults to `ScanMode.Lazy` (or read-only `Full` if lazy not supported)
- [ ] `PreloadBudget` defaults to `256 * 1024 * 1024`
- [ ] `ErrorTolerance` defaults to `ErrorToleranceMode.Tolerant`
- [ ] Optional `UiName` property

### General

- [ ] XML documentation on all public members
- [ ] Copyright header on all files
- [ ] Unit tests for factory, lifecycle, error tolerance, and edge cases
- [ ] Integration tests in `NetworkInspector.Sources.Tests`
- [ ] README.md updated with new source in the table
- [ ] Scanner uses windowed I/O (see §11.4): holds backend reference, `_Offset` is `long`, no whole-file span passed in
- [ ] No `fileSize > int.MaxValue` guard in `Open()` — large files supported by design
- [ ] `MaxBlockReadSize` (or equivalent) constant applied to all block-level `GetScanSpan`/`GetSpan` calls

---

## Appendix A: Complete Minimal Source Template

```csharp
// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Xxx;

/// <summary>
/// Random-access reader for XXX format files.
/// <para>This type is not thread-safe for <see cref="NextFrame"/>.
/// <see cref="FrameById"/> is thread-safe and can be called concurrently.</para>
/// </summary>
public sealed class XxxSource : IRandomAccessFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    private FrameSourceId _SourceId;
    private FrameInterfaceRegistry _Registry;
    private bool _Started;
    private bool _Disposed;
    private bool _Aborted;
    private long _ReadFrameCount;
    private long _SkippedFrameCount;
    private long _ErrorCount;
    private int _NextFrameIndex;

    private readonly Dictionary<(LinkType, int), FrameInterfaceId> _InterfaceMap = new();
    private readonly string _UiName;

    #endregion

    #region Properties

    /// <inheritdoc />
    public string UiName => _UiName;

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    public int? EstimatedFrameCount => _Index.Count;

    /// <inheritdoc />
    public bool IsFrameCountTruncated => false;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);

    /// <inheritdoc />
    public long ReadFrameCount => Volatile.Read(ref _ReadFrameCount);

    /// <inheritdoc />
    public long SkippedFrameCount => Volatile.Read(ref _SkippedFrameCount);

    /// <inheritdoc />
    public long ErrorCount => Volatile.Read(ref _ErrorCount);

    /// <inheritdoc />
    public bool HasErrors => Volatile.Read(ref _ErrorCount) > 0;

    /// <inheritdoc />
    public ErrorToleranceMode ErrorTolerance { get; set; }

    /// <inheritdoc />
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    #endregion

    #region Constructors

    private XxxSource(string uiName, ErrorToleranceMode errorTolerance /*, index, backend */)
    {
        _UiName = uiName;
        ErrorTolerance = errorTolerance;
        // Store index, backend, etc.
    }

    #endregion

    #region Factory Methods

    /// <summary>Opens an XXX file for random-access reading.</summary>
    public static XxxSource Open(string path, XxxSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        XxxSourceOptions opts = options ?? new();
        string uiName = opts.UiName ?? Path.GetFileName(path);

        // Parse header, select backend, build index
        // ...

        return new XxxSource(uiName, opts.ErrorTolerance /*, index, backend */);
    }

    /// <summary>Creates a source from in-memory data.</summary>
    public static XxxSource FromData(byte[] data, string uiName, XxxSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(uiName);

        XxxSourceOptions opts = options ?? new();
        // ...
        return new XxxSource(uiName, opts.ErrorTolerance /*, index, backend */);
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _SourceId = sourceId;
        _Registry = registry;
        _NextFrameIndex = 0;

        // Pre-register all interfaces discovered during scan
        foreach ((LinkType lt, int ch) in _DiscoveredInterfaces)
        {
            FrameInterfaceId id = registry.Register(new FrameInterfaceInfo(lt, ch, sourceId));
            _InterfaceMap[(lt, ch)] = id;
        }

        Volatile.Write(ref _Started, true);
    }

    /// <inheritdoc />
    public Frame? NextFrame()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
            throw new InvalidOperationException($"{UiName} has not been started.");

        if (Volatile.Read(ref _Aborted))
            return null;

        if (_NextFrameIndex >= _Index.Count)
            return null;

        // Read frame from backend
        int index = _NextFrameIndex++;
        // ...

        Interlocked.Increment(ref _ReadFrameCount);
        return frame;
    }

    /// <inheritdoc />
    public Frame? FrameById(FrameId id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (id.Value < 0 || id.Value >= _Index.Count)
            return null;

        // Thread-safe read from backend
        // ...

        return frame;
    }

    #endregion

    #region Error Handling

    private void HandleSkip(int frameIndex, long fileOffset, FrameReadErrorKind kind, string message)
    {
        Interlocked.Increment(ref _ErrorCount);
        Interlocked.Increment(ref _SkippedFrameCount);

        FrameSkipped?.Invoke(this, new FrameReadErrorEventArgs
        {
            FrameIndex = frameIndex,
            FileOffset = fileOffset,
            Kind = kind,
            Message = message,
        });

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Aborted, true);
        }
    }

    #endregion

    #region Dispose

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
            return;

        Volatile.Write(ref _Disposed, true);

        // Release resources
        // _Backend?.Dispose();
    }

    #endregion
}
```

## Appendix B: Minimal Stream Source Template

```csharp
// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Xxx;

/// <summary>
/// Forward-only streaming reader for XXX format.
/// <para>This type is not thread-safe. All methods must be called from a single thread.</para>
/// </summary>
public sealed class XxxStreamSource : IFrameSource, IErrorTolerantFrameSource
{
    #region Fields

    private FrameSourceId _SourceId;
    private FrameInterfaceRegistry _Registry;
    private bool _Started;
    private bool _Disposed;
    private bool _Exhausted;
    private long _ReadFrameCount;
    private long _SkippedFrameCount;
    private long _ErrorCount;
    private int _NextFrameIndex;

    private readonly Stream _Stream;
    private readonly bool _LeaveOpen;
    private readonly string _UiName;
    private readonly Dictionary<(LinkType, int), FrameInterfaceId> _InterfaceMap = new();

    #endregion

    #region Properties

    /// <inheritdoc />
    public string UiName => _UiName;

    /// <inheritdoc />
    public string? Description => null;

    /// <inheritdoc />
    public int? EstimatedFrameCount => null;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _Started) && !Volatile.Read(ref _Disposed);

    /// <inheritdoc />
    public long ReadFrameCount => Volatile.Read(ref _ReadFrameCount);

    /// <inheritdoc />
    public long SkippedFrameCount => Volatile.Read(ref _SkippedFrameCount);

    /// <inheritdoc />
    public long ErrorCount => Volatile.Read(ref _ErrorCount);

    /// <inheritdoc />
    public bool HasErrors => Volatile.Read(ref _ErrorCount) > 0;

    /// <inheritdoc />
    public ErrorToleranceMode ErrorTolerance { get; set; }

    /// <inheritdoc />
    public event EventHandler<FrameReadErrorEventArgs>? FrameSkipped;

    #endregion

    #region Constructors

    private XxxStreamSource(Stream stream, string uiName, bool leaveOpen, ErrorToleranceMode errorTolerance)
    {
        _Stream = stream;
        _UiName = uiName;
        _LeaveOpen = leaveOpen;
        ErrorTolerance = errorTolerance;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a streaming reader from the given stream.</summary>
    public static XxxStreamSource FromStream(Stream stream, string uiName, bool leaveOpen = false,
        XxxSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(uiName);

        XxxSourceOptions opts = options ?? new();
        return new XxxStreamSource(stream, uiName, leaveOpen, opts.ErrorTolerance);
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        _SourceId = sourceId;
        _Registry = registry;
        _NextFrameIndex = 0;

        Volatile.Write(ref _Started, true);
    }

    /// <inheritdoc />
    public Frame? NextFrame()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed), this);

        if (!Volatile.Read(ref _Started))
            throw new InvalidOperationException($"{UiName} has not been started.");

        if (Volatile.Read(ref _Exhausted))
            return null;

        // Read next frame from stream
        // Register interface lazily if new (busType, channel) seen
        // ...

        Interlocked.Increment(ref _ReadFrameCount);
        return frame;
    }

    #endregion

    #region Error Handling

    private void HandleSkip(int frameIndex, long fileOffset, FrameReadErrorKind kind, string message)
    {
        Interlocked.Increment(ref _ErrorCount);
        Interlocked.Increment(ref _SkippedFrameCount);

        FrameSkipped?.Invoke(this, new FrameReadErrorEventArgs
        {
            FrameIndex = frameIndex,
            FileOffset = fileOffset,
            Kind = kind,
            Message = message,
        });

        if (ErrorTolerance == ErrorToleranceMode.Strict)
        {
            Volatile.Write(ref _Exhausted, true);
        }
    }

    #endregion

    #region Dispose

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
            return;

        Volatile.Write(ref _Disposed, true);

        if (!_LeaveOpen)
            _Stream?.Dispose();
    }

    #endregion
}
```
