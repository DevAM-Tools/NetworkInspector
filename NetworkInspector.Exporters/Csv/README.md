<!-- Copyright © 2026 DevAM. All rights reserved. -->

# CSV Exporter

Packet-level CSV exporter for spreadsheet and tabular processing workflows.

## What This Is

The `CsvExporter` serializes parsed packets through `IPacketListener` into configurable CSV rows.

## Why Use It

- Friendly for analysts working in spreadsheets and SQL-style import pipelines.
- Custom column selection for compact task-specific exports.
- Built-in CSV quoting and escaping for robust machine parsing.

## Quick Start

```csharp
using NetworkInspector.Exporters.Csv;

using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("packets.csv")
    .WithDefaultColumns()
    .Build();

foreach (Packet packet in packets)
{
    if (!exporter.OnPacket(packet))
    {
        break;
    }
}

exporter.OnFinish();
```

## Common Tasks

### Use Custom Delimiter And Header

```csharp
using NetworkInspector.Exporters.Csv;

using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("packets.tsv")
    .WithDelimiter('\t')
    .WithHeader(true)
    .Build();
```

### Select Custom Columns

```csharp
using NetworkInspector.Exporters.Csv;

using CsvExporter exporter = CsvExporter.CreateBuilder()
    .ToFile("packets.csv")
    .WithColumn(CsvColumnKind.PacketNumber)
    .WithColumn(CsvColumnKind.Timestamp)
    .WithFieldColumn("ip.src", ipSourceFieldId, "SourceIP")
    .WithFieldColumn("ip.dst", ipDestFieldId, "DestinationIP")
    .Build();
```

## Builder Options

| Method | Purpose |
| --- | --- |
| `ToFile(path)` / `ToStream(stream)` / `ToStdout()` | Select output target |
| `WithUiName(name)` / `WithDescription(text)` | Set user-facing metadata |
| `WithBom(write)` | Enable or disable UTF-8 BOM |
| `WithDelimiter(delimiter)` | Set ASCII delimiter character |
| `WithHeader(write)` | Enable or disable header row |
| `WithColumn(kind, header?)` | Add built-in column |
| `WithFieldColumn(name, fieldId, header?)` | Add field-driven custom column |
| `WithDefaultColumns()` | Add `No.`, `Time`, `Info`, `Length` |
| `WithTargetPacketCount(count)` | Stop after N packets (`0` = unlimited) |
| `WithCancellationToken(token)` | Enable cooperative cancellation |

## Limits And Thread-Safety Notes

- Not thread-safe; call `OnPacket()`/`OnFinish()` sequentially.
- Delimiter must be valid ASCII and not a control/quote character.
- Treat CSV output as potentially sensitive if packet text contains secrets.

## Links

- [Exporters hub](../README.md)
- [CSV source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Exporters/Csv)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Exporters)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../../LICENSE)
