<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# AscExporter — ASC (Vector CANalyzer) Exporter

The `AscExporter` writes captured frames to a **Vector CANalyzer ASCII log** (`.asc`) file.
The format is a human-readable text log compatible with CANoe, can-utils (`candump`/`canplayer`),
and the NetworkInspector ASC source reader.

---

## Format Overview

An ASC file is a plain-text file with one frame per line. The header identifies the numeric
base and timestamp mode; the data section is wrapped in a `Begin Triggerblock` / `End TriggerBlock`
block.

### Output Settings

```
base hex  timestamps absolute
```

- **Numeric base**: always `hex` (uppercase, no `0x` prefix)
- **Timestamps**: absolute seconds relative to the first frame, 6 decimal places (1 µs resolution)
- **Line endings**: `\r\n` (Windows-style, matching CANoe output)

---

## Supported Frame Types

| LinkType | Format | Notes |
|---|---|---|
| `CanSocketcan` / `Can20B` | CAN classic or CAN FD | FD detected from FDF flag (byte 5, bit 2) |
| `Lin` | LIN message | Channel from interface properties, checksum type defaults to `enhanced` |
| `Flexray` | FlexRay message | Physical channel from DLT_FLEXRAY header byte 0 |
| `Ethernet`, `CanXl`, others | — | Skipped; counted in `SkippedCount` |

---

## Line Formats

### CAN Classic

```
{timestamp} {channel} {id}[x] Rx d|r {dlc} [{data bytes}]
```

Examples:
```
1.234567 1 123 Rx d 4 01 02 03 04
1.235000 1 1FFFFFFFx Rx d 8 DE AD BE EF 00 11 22 33
1.236000 1 456 Rx r 4
```

Field details:
- `timestamp`: decimal seconds, 6 decimal places
- `channel`: decimal integer
- `id`: 3-char uppercase hex for standard frames (11-bit); 8-char uppercase hex + `x` suffix for extended frames (29-bit)
- Direction: always `Rx`
- Type: `d` for data frames, `r` for remote frames (RTR)
- `dlc`: decimal Data Length Code (0–8)
- Data bytes: 2-char uppercase hex each, space-separated; absent for remote frames

### CAN FD

```
{timestamp} CANFD {channel} Rx {id}[x] {brs} {esi} {dlc} {dlen} [{data bytes}]
```

Example:
```
2.000000 CANFD 1 Rx 456 1 0 8 8 01 02 03 04 05 06 07 08
```

Field details:
- `brs`: `0` or `1` (decimal) — Bit Rate Switch
- `esi`: `0` or `1` (decimal) — Error State Indicator
- `dlc`: decimal FD DLC code (0–15)
- `dlen`: decimal actual byte count (0–64)

### LIN

```
{timestamp} L{channel} {frameId} Rx {dlc} [{data bytes}] checksum = {checksum} CSM = enhanced
```

Example:
```
3.000000 L1 3F Rx 3 11 22 33 checksum = 7A CSM = enhanced
```

Field details:
- Channel: decimal integer prefixed with `L` (e.g., `L1`)
- `frameId`: 2-char uppercase hex (6-bit ID, 0x00–0x3F)
- `dlc`: decimal data byte count
- `checksum`: 2-char uppercase hex
- Checksum method: always `enhanced` (LIN 2.x standard; not stored in the frame binary)

### FlexRay

```
{timestamp} Fr {channel} V9 {frameId} {payloadWords} {cycle} 0 {headerCrc} x {dlen} [{data bytes}]
```

Example:
```
4.000000 Fr 1 V9 000A 5 3 0 ABCD x 10 DE AD BE EF 01 02 03 04 05 06
```

Field details:
- `channel`: decimal integer (raw from DLT_FLEXRAY header byte 0)
- `frameId`: 4-char uppercase hex (11-bit slot ID)
- `payloadWords`: decimal count of 16-bit words (ceiling of `dlen / 2`)
- `cycle`: decimal cycle counter (0–63)
- NM flag: always `0` (not available from the frame binary)
- `headerCrc`: 4-char uppercase hex
- `x`: literal identifier placeholder token
- `dlen`: decimal data byte count (0–254)

---

## File Structure

```
date Mon Jan  1 10:00:00.000 2024
base hex  timestamps absolute
no internal events logged
Begin Triggerblock Mon Jan  1 10:00:00.000 2024
0.000000 1 123 Rx d 4 01 02 03 04
0.001000 CANFD 1 Rx 456 0 0 8 8 DE AD BE EF 01 02 03 04
0.002000 L1 3F Rx 3 11 22 33 checksum = 7A CSM = enhanced
0.003000 Fr 1 V9 000A 2 5 0 1234 x 4 AB CD EF 00
End TriggerBlock
```

---

## Builder Options

| Method | Default | Description |
|---|---|---|
| `ToFile(string path)` | *required* | Write to file (lazy; created on first frame) |
| `ToStream(Stream stream)` | *required* | Write to existing stream (caller owns it) |
| `ToStdout()` | *required* | Write to standard output |
| `WithUiName(string)` | `"ASC Exporter"` | Display name in UI and logs |
| `WithDescription(string)` | `null` | Optional description |
| `WithCancellationToken(CancellationToken)` | none | Token to abort the export |
| `WithTargetFrameCount(long)` | `0` (unlimited) | Stop after N frames |

`Build()` throws `InvalidOperationException` when no output target was set.

---

## Usage Example

```csharp
using AscExporter exporter = AscExporter.CreateBuilder()
    .ToFile("capture.asc")
    .WithUiName("CAN capture")
    .WithTargetFrameCount(10_000)
    .Build();

foreach (Frame frame in source)
{
    if (!exporter.OnFrame(frame))
    {
        break;
    }
}

exporter.OnFinish();
Console.WriteLine($"Written: {exporter.WrittenCount}, Skipped: {exporter.SkippedCount}");
```

---

## Channel Resolution

For CAN and LIN frames the channel number is resolved from the frame's interface property bag:

1. `"asc.channel"` (set by `AscSource` / `AscStreamSource`) — exact round-trip
2. `FrameInterfacePropertyKeys.BlfChannel` (`"blf.channel"`) — BLF → ASC export
3. Default: `1`

For FlexRay frames the physical channel comes from DLT_FLEXRAY header byte 0 (as stored by
the FlexRay source), not from the interface property bag.

---

## Limitations

- **No Ethernet support**: Ethernet frames are skipped with `UnsupportedType`.
- **No CAN XL support**: CAN XL frames are skipped with `UnsupportedType`.
- **Direction**: always written as `Rx`; Tx/Rx metadata is not present in the `Frame` struct.
- **LIN checksum type**: always written as `enhanced`; the classic/enhanced distinction is
  not stored in the BLF-derived binary format.
- **FlexRay NM flag**: always `0`; not available from the DLT_FLEXRAY binary.
- **Timestamp resolution**: 6 decimal places (1 µs); sub-microsecond precision is lost.
