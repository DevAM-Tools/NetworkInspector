<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.Values

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Values)](https://www.nuget.org/packages/NetworkInspector.Values)

Strongly-typed network address and timestamp value types for the NetworkInspector framework.
All types are `readonly struct` and designed for zero-allocation use in hot paths.
They integrate with [ZeroAlloc](https://www.nuget.org/packages/ZeroAlloc) for
heap-free string formatting.

## Value Types

| Type | Size | Description |
|------|------|-------------|
| `MacAddress` | 6 bytes | 48-bit IEEE 802 MAC address (EUI-48). Formatted as `00:1a:2b:3c:4d:5e`. |
| `IPv4Address` | 4 bytes | 32-bit IPv4 address. Formatted in dotted-decimal notation (`192.168.1.1`). |
| `IPv6Address` | 16 bytes | 128-bit IPv6 address. Formatted per RFC 5952 with `::` compression. |
| `Eui64` | 8 bytes | 64-bit EUI-64 identifier. Used in IPv6 interface IDs and IEEE 802.15.4. |
| `Uuid` | 16 bytes | 128-bit UUID. Formatted as `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`. |
| `Timestamp` | 8 bytes | Nanosecond-precision UNIX timestamp. Formatted as ISO 8601 with nanosecond resolution. |

## Usage

```csharp
MacAddress mac  = new MacAddress(0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E);
IPv4Address ip4 = new IPv4Address(192, 168, 1, 1);
IPv6Address ip6 = IPv6Address.Parse("2001:db8::1");
Timestamp   ts  = Timestamp.FromSecs(1_700_000_000);

Console.WriteLine(mac);  // 00:1a:2b:3c:4d:5e
Console.WriteLine(ip4);  // 192.168.1.1
Console.WriteLine(ip6);  // 2001:db8::1
Console.WriteLine(ts);   // ISO 8601 with nanosecond precision
```

All types implement `IEquatable<T>`, `IComparable<T>`, and `IFormattable`.
`Timestamp` supports arithmetic operators for duration math.

## Installation

```
dotnet add package NetworkInspector.Values
```

## License

[MIT License](../LICENSE) — © DevAM
