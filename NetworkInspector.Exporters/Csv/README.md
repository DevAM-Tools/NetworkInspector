<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# CSV Exporter

[![NuGet][nuget-badge]][nuget]

[nuget-badge]: https://img.shields.io/nuget/v/NetworkInspector.Exporters?logo=nuget&label=NetworkInspector.Exporters
[nuget]: https://www.nuget.org/packages/NetworkInspector.Exporters

`CsvExporter` writes one row per parsed packet, ready for Excel, LibreOffice, pandas, or any SQL import. It is a
packet exporter (`IPacketListener`): packets must be parsed with a field tree (`FieldTreeMode.Build`, the default).

## Highlights

- **Pick your columns:** packet number, time, info, frame length, plus any protocol field by name.
- **Correct quoting.** Values that contain the delimiter, a quote, CR, or LF are quoted and escaped.
- **Safe to open in a spreadsheet.** Text cells that start with `=`, `+`, `-`, `@`, TAB, or CR get a leading
  apostrophe (CSV injection protection, on by default, see below).
- **Any ASCII delimiter**, so TSV and semicolon-separated files for European spreadsheet locales work too.
- **Spreadsheet friendly by default:** UTF-8 BOM, header row, CRLF line endings.

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Exporters.Csv;

// packets: parsed packets with a field tree, see the end-to-end example in the Exporters README.
static void WriteCsv(IEnumerable<Packet> packets)
{
    using CsvExporter exporter = CsvExporter.CreateBuilder()
        .ToFile("packets.csv")
        .Build(); // no columns configured: No., Time, Info, Length

    foreach (Packet packet in packets)
    {
        if (!exporter.OnPacket(packet))
        {
            break;
        }
    }

    exporter.OnFinish();
}
```

## Common Tasks

### Select Protocol Fields As Columns

Resolve field ids once from the stack, then add them as columns. Only the first occurrence of a field in a
packet is written.

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Exporters.Csv;

static CsvExporter CreateAddressExporter(Stack stack)
{
    FieldId ipSrc = stack.GetFieldId("ip.src") ?? throw new InvalidOperationException("ip.src is not registered");
    FieldId ipDst = stack.GetFieldId("ip.dst") ?? throw new InvalidOperationException("ip.dst is not registered");

    return CsvExporter.CreateBuilder()
        .ToFile("addresses.csv")
        .WithColumn(CsvColumnKind.PacketNumber)
        .WithColumn(CsvColumnKind.Timestamp)
        .WithFieldColumn("ip.src", ipSrc, "SourceIP")
        .WithFieldColumn("ip.dst", ipDst, "DestinationIP")
        .Build();
}
```

### Write TSV Without A BOM

```csharp
using NetworkInspector.Exporters.Csv;

using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("packets.tsv")
    .WithDelimiter('\t')
    .WithBom(false)
    .Build();
```

## Builder Options

| Method | Purpose | Default |
| --- | --- | --- |
| `ToFile` / `ToStream` / `ToStdout` | Output target | required |
| `WithUiName` / `WithDescription` | User-facing metadata | — |
| `WithBom(bool)` | UTF-8 byte order mark | `true` |
| `WithDelimiter(char)` | Column delimiter | `,` |
| `WithHeader(bool)` | Header row | `true` |
| `WithFormulaNeutralization(bool)` | Prefix text cells that start with a formula character with `'` | `true` |
| `WithColumn(kind, header?)` | Built-in column (`PacketNumber`, `Timestamp`, `Info`, `FrameLength`) | — |
| `WithFieldColumn(name, fieldId, header?)` | Column from a protocol field | — |
| `WithDefaultColumns()` | `No.`, `Time`, `Info`, `Length` | used when none set |
| `WithTargetPacketCount(int)` | Stop after N packets (`0` = no limit) | 0 |
| `WithCancellationToken` | Cooperative cancellation | none |

`name` in `WithFieldColumn` is only the default header; the lookup uses the `FieldId`.

## Formula Neutralization (CSV Injection)

Capture text is untrusted: a DNS name or an info line can start with `=`, `+`, `-`, or `@`, and a spreadsheet reads
such a cell as a formula when the file is opened. By default the exporter writes a single quote in front of any text
cell whose first character is `=`, `+`, `-`, `@`, TAB, or CR (the quote goes inside the CSV quotes when the cell is
quoted). Example: the text `=SUM(A1,A2)` is written as `"'=SUM(A1,A2)"`.

- **What is changed:** the `Info` column and field columns whose value is a string. Numbers, timestamps, addresses,
  byte values, and the header row are written as they are, so a negative number keeps its minus sign.
- **Posture:** secure by default. Turn it off with `WithFormulaNeutralization(false)` when a program reads the file
  and must see the exact text (the added quote is data for it).
- **Residual risk:** with the option off, any text cell can run as a formula in a spreadsheet. With it on, a formula
  that starts after leading white space is not covered, and tools that do not treat the quote as a text marker show it.

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()` and `OnFinish()` from one thread.
- The delimiter must be ASCII (≤ 0x7F) and must not be NUL, CR, LF, or `"`.
- Packets parsed with `FieldTreeMode.Skip` are skipped.
- No file is created until the first packet arrives.
- CSV is available from the library only; the `ni` CLI has no CSV format.
- Treat CSV output as sensitive when packet text can contain secrets.

## Links

- [Exporters overview](../README.md)
- [Exporter guide](../EXPORTER_GUIDE.md)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
