<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Generators

[![Bundled with NetworkInspector.Core][core-badge]][core]
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

[core-badge]: https://img.shields.io/nuget/v/NetworkInspector.Core?logo=nuget&label=bundled%20with%20Core
[core]: https://www.nuget.org/packages/NetworkInspector.Core

The Roslyn source generator behind NetworkInspector dissectors. You declare fields, tables, and settings with
attributes on a `partial` protocol class; the generator writes the registration code, the metadata members, and
the lifecycle plumbing. You write only `Parse`.

It is not a separate package: it ships inside `NetworkInspector.Core` and runs automatically in every project that
references Core.

## Highlights

- **Declarations instead of boilerplate.** One attribute per field, table, or setting replaces the manual
  `RegisterField`, `RegisterProtocolTable`, and settings code.
- **Mistakes fail the build.** Duplicate names, invalid names, and malformed declarations are compiler errors
  (`NIGEN001` …), not runtime surprises.
- **Same mechanism as the built-ins.** All 29 standard dissectors in `NetworkInspector.Protocols` are written this
  way.
- **Incremental and zero-reflection.** Generated code is plain C#; nothing is discovered at runtime.

## Install

```bash
dotnet add package NetworkInspector.Core
dotnet add package NetworkInspector.Protocols
```

The generator comes with Core. The attributes (`[Protocol]`, `[U64Field]`, …) live in
`NetworkInspector.Protocols.Attributes`, so a project that declares protocols also references
`NetworkInspector.Protocols`.

## Quick Start

A complete dissector for a toy protocol on UDP port 7777 that carries a 2-byte counter:

```csharp
using System.Buffers.Binary;
using NetworkInspector.Core;
using NetworkInspector.Core.Errors;
using NetworkInspector.Core.Fields;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Protocols;
using NetworkInspector.Protocols;
using NetworkInspector.Protocols.Attributes;

namespace MyCompany.Dissectors;

[Protocol("hello", "Hello Protocol", Description = "Example dissector")]
[RegisterAtTable(UdpProtocol.PortTableName, 7777)]
public sealed partial class HelloProtocol : IProtocol
{
    [BytesField("hello", "Hello")]
    private FieldId _ProtocolFieldId;

    [U64Field("hello.counter", "Counter")]
    private FieldId _CounterFieldId;

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < 2)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, 2, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        ushort counter = BinaryPrimitives.ReadUInt16BigEndian(data.Span);

        MutField container = parentField.Append(_ProtocolFieldId, FieldValue.NewBytes(data[..2]));
        container.Append(_CounterFieldId, FieldValue.NewU64(counter));
        return 2; // bytes consumed
    }
}
```

Register it after the standard protocols:

```csharp
StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
builder.RegisterStandardProtocols();

HelloProtocol hello = new();
ProtocolId helloId = builder.RegisterProtocol(hello);
hello.RegisterFields(builder, helloId); // generated

using Stack stack = builder.Build();
```

A UDP datagram to port 7777 now produces the fields `hello` and `hello.counter`, which filters, exporters, and
value caches can use like any built-in field. For lazy fields, dispatch to sub-protocols, settings, and stateful
parsing, follow the [Protocol guide](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md).

## What The Generator Emits

| Member | Purpose |
| --- | --- |
| `ProtocolName`, `ProtocolUiName` | `public const string` from `[Protocol]` |
| `Name`, `UiName`, `Description` | `IProtocol` members (`Description` only when set) |
| `_ProtocolId` | The id assigned at registration |
| `RegisterFields(IStackBuilder, ProtocolId)` | Registers fields, tables, settings, and `[RegisterAtTable]` dispatch |
| `OnStart(Stack)`, `OnShutdown(Stack)` | `IProtocol` lifecycle members |
| Table-name constants | `public const string` for declared tables |

It does not emit `Parse` and does not call `RegisterProtocol`; the caller registers the instance, as shown above.

Optional partial hooks for custom setup:

```csharp
partial void _RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId);
partial void _OnStartCustom(Stack stack);
partial void OnShutdownCustom(Stack stack);
```

## Diagnostics

| Id | Severity | Meaning |
| --- | --- | --- |
| NIGEN001 | Error | Duplicate field name |
| NIGEN002 | Error | Duplicate setting name |
| NIGEN003 | Error | Duplicate dispatch table name |
| NIGEN004 | Error | Invalid character in an index group or table name |
| NIGEN005 | Error | Non-numeric `EnumSetting` value |
| NIGEN006 | Error | Generic protocol class |
| NIGEN007 | Error | Nested protocol class |
| NIGEN008 | Error | Protocol class in the global namespace |
| NIGEN009 | Error | Invalid bool dispatch key type |
| NIGEN010 | Warning | Unknown field attribute, mapped to `FieldType.None` |
| NIGEN011 | Error | Invalid `BytesSetting.DefaultHex` |
| NIGEN012 | Error | Protocol class is not `partial` |
| NIGEN013 | Warning | Incomplete attribute payload; entry skipped |
| NIGEN014 | Error | Null element in an array-setting `Default` |
| NIGEN015 | Warning | `ZA.Lazy` passed to a `MutField` custom-text append |

## Learn More

- [Protocol guide](../NetworkInspector.Protocols/PROTOCOL_GUIDE.md): attributes, fields, tables, settings, lazy
  fields, dispatch
- [Protocols](../NetworkInspector.Protocols/README.md) · [Core](../NetworkInspector.Core/README.md) ·
  [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
