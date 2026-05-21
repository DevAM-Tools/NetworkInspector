<!-- Copyright © 2026 DevAM. All rights reserved. -->

# NetworkInspector.Values

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Values)](https://www.nuget.org/packages/NetworkInspector.Values)

Typed address and timestamp primitives for NetworkInspector and related .NET networking workflows.

## What This Is

`NetworkInspector.Values` provides lightweight value types commonly needed in packet pipelines:

- `MacAddress`
- `IPv4Address`
- `IPv6Address`
- `Eui64`
- `Uuid`
- `Timestamp`

These types are designed for predictable formatting and comparison behavior in parsing and export code.

## Why It Stands Out

- Strong typing for common network identifiers.
- Consistent formatting output for logs and exports.
- Easy to reuse outside of the full NetworkInspector stack.

## Install

```bash
dotnet add package NetworkInspector.Values
```

## Quick Start

```csharp
using NetworkInspector.Values;

MacAddress mac = new(0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E);
IPv4Address ip4 = new(192, 168, 1, 10);
IPv6Address ip6 = IPv6Address.Parse("2001:db8::1");
Timestamp ts = Timestamp.FromSecs(1_700_000_000);

Console.WriteLine(mac); // 00:1a:2b:3c:4d:5e
Console.WriteLine(ip4); // 192.168.1.10
Console.WriteLine(ip6); // 2001:db8::1
Console.WriteLine(ts);
```

## Common Tasks

### Store Typed Addresses

Use value types in domain objects instead of raw strings to reduce parsing/formatting ambiguity.

### Compare And Sort

`IEquatable<T>` and comparison support make these values practical for indexing and reporting workflows.

### Export Stable Text

Use built-in string formatting for CSV/JSON/text output without custom converters for each primitive.

## Limits And Thread-Safety Notes

- These structs are value-centric and suitable for concurrent read usage.
- Validate external input before parsing string representations.

## Links

- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector)
- [NuGet package](https://www.nuget.org/packages/NetworkInspector.Values)
- [Source folder](https://github.com/DevAM-Tools/NetworkInspector/tree/main/NetworkInspector.Values)
- [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
