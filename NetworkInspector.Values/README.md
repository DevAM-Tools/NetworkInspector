<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Values

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Values?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Values)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Values?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Values)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Small, fast value types for network identifiers and time: `MacAddress`, `IPv4Address`, `IPv6Address`, `Eui64`,
`Uuid`, and `Timestamp`. They are the value types of the whole NetworkInspector stack and work just as well on
their own.

## Highlights

- **Value semantics.** Every type is a `readonly record struct`: no allocation, equality and ordering built in
  (`IEquatable<T>`, `IComparable<T>`, `<`, `>`, `<=`, `>=`).
- **Allocation-free formatting and parsing.** `ISpanFormattable`, `IUtf8SpanFormattable`, and `ISpanParsable<T>`
  let you format into and parse from spans without strings.
- **Stable text forms** for logs and exports: RFC 5952 IPv6, colon-separated MAC, ISO-8601 timestamps with
  nanoseconds.
- **Address classification:** broadcast, multicast, private, loopback, link-local, IPv4-mapped, and more.
- **Nanosecond timestamps** with `TimeSpan` arithmetic.
- **Standalone.** No dependency on the rest of NetworkInspector.

## Install

```bash
dotnet add package NetworkInspector.Values
```

## Quick Start

```csharp
using NetworkInspector.Values;

MacAddress mac = MacAddress.FromBytes([0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E]);
IPv4Address ip4 = IPv4Address.FromBytes([192, 168, 1, 10]);
if (!IPv6Address.TryParse("2001:db8::1", out IPv6Address ip6))
{
    throw new FormatException("Invalid IPv6 address.");
}

Timestamp ts = Timestamp.FromSecs(1_700_000_000);

Console.WriteLine(mac); // 00:1A:2B:3C:4D:5E
Console.WriteLine(ip4); // 192.168.1.10
Console.WriteLine(ip6); // 2001:DB8::1
Console.WriteLine(ts);  // 2023-11-14T22:13:20.000000000Z
```

## Types

| Type | Text form | Create from |
| --- | --- | --- |
| `MacAddress` | `00:1A:2B:3C:4D:5E` | `FromBytes`, `new(ulong)` (lower 48 bits), `TryParse` |
| `IPv4Address` | `192.168.1.10` | `FromBytes`, `new(uint)` (big-endian value), `TryParse` |
| `IPv6Address` | `2001:DB8::1` (RFC 5952) | `FromBytes`, `new(ulong high, ulong low)`, `TryParse` |
| `Eui64` | `00:11:22:33:44:55:66:77` | `FromBytes`, `TryParse` |
| `Uuid` | `01020304-0506-0708-090A-0B0C0D0E0F10` | `FromBytes`, `TryParse` |
| `Timestamp` | `2023-11-14T22:13:20.000000000Z` | `FromNanos`, `FromMicros`, `FromMillis`, `FromSecs`, `Now` |

Text output uses uppercase hex; `TryParse` accepts either case. `Parse` is available through the generic
`IParsable<T>` / `ISpanParsable<T>` interfaces; call `TryParse` directly for untrusted text.

## Common Tasks

### Classify Addresses

```csharp
if (ip4.IsPrivate || ip4.IsLoopback)
{
    Console.WriteLine("internal traffic");
}
```

### Format Without Allocating

```csharp
Span<char> buffer = stackalloc char[64];
if (ip6.TryFormat(buffer, out int written, default, null))
{
    Console.Out.Write(buffer[..written]);
}
```

### Use As Keys

The types are equatable and comparable values, so they work as dictionary keys, in sorted sets, and in
`ORDER BY`-style reports without custom comparers.

## Limits And Thread-Safety Notes

- Immutable values: safe to share across threads.
- `Timestamp.FromSecs` and the other factories throw `OverflowException` outside the representable range.
- Validate external text with `TryParse` instead of catching exceptions.

## Learn More

- [Core](../NetworkInspector.Core/README.md) · [FrameBuilder](../NetworkInspector.FrameBuilder/README.md) ·
  [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
