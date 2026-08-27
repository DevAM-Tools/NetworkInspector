<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Generators

> This generator is bundled with `NetworkInspector.Core`. You normally install only `NetworkInspector.Core`.

Roslyn source generation support for declarative protocol authoring.

## What This Is

`NetworkInspector.Generators` generates protocol registration boilerplate from attributes on partial protocol classes.

It helps reduce manual code for:

- field registration,
- table registration,
- settings registration,
- protocol metadata constants.

## Why It Stands Out

- Keeps protocol declarations concise and readable.
- Reduces repetitive registration code.
- Improves consistency of protocol definitions across teams.

## Install

Install Core; generator support is included automatically:

```bash
dotnet add package NetworkInspector.Core
```

`NetworkInspector.Core` delivers generator support without additional package references:

- `NetworkInspector.Generators` is bundled directly in Core analyzer assets.
- `ZeroAlloc` (including `ZeroAlloc.Generator`) flows transitively through NetworkInspector library packages.

## Quick Start

```csharp
[Protocol("eth", "Ethernet")]
public sealed partial class EthernetProtocol : IProtocol
{
    [MacField("eth.src", "Source")]
    private FieldId _SrcFieldId;

    [MacField("eth.dst", "Destination")]
    private FieldId _DstFieldId;

    [ProtocolTableU64("eth.type", "EtherType")]
    private ProtocolTableId _EtherTypeTableId;

    public ParseResult Parse(
        in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        return 0;
    }
}
```

The generator emits `public string Name` / `UiName` / `Description` and `public void OnStart` / `OnShutdown` as `IProtocol` members, plus registration members. It does not emit `Parse`.

## Common Tasks

### Declare Protocol Fields

Annotate `FieldId` fields with the appropriate field attributes.

### Declare Dispatch Tables

Use protocol table attributes for typed dispatch.

### Add Runtime Hooks

Use partial hook methods (`RegisterFieldsCustom`, `OnStartCustom`, `OnShutdownCustom`) for additional runtime setup.

## Diagnostics

Generator diagnostics help catch invalid attribute usage and malformed protocol declarations early in the build.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package (bundled with Core)](https://www.nuget.org/packages/NetworkInspector.Core)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Generators)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
