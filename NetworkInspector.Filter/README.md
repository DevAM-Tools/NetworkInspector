<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# NetworkInspector.Filter

[![NuGet](https://img.shields.io/nuget/v/NetworkInspector.Filter?logo=nuget&label=NuGet)](https://www.nuget.org/packages/NetworkInspector.Filter)
[![Downloads](https://img.shields.io/nuget/dt/NetworkInspector.Filter?logo=nuget&label=downloads)](https://www.nuget.org/packages/NetworkInspector.Filter)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](../LICENSE)

Wireshark-style display filters for NetworkInspector packets: `udp.dstport == 53`, `ip.src in {10.0.0.1, 10.0.0.2}`,
`tcp && !tls`. Expressions are compiled once to native code and checked against the protocol stack, so a typo is a
compile error with a "did you mean" hint, not a silent non-match.

## Highlights

- **Familiar syntax.** Protocol presence, comparisons, `and` / `or` / `not`, sets, ranges, `contains`, `matches`,
  byte slices, `len()`, IPv4/IPv6/MAC literals, field aliases (`eth.addr`).
- **Compiled.** Expressions become .NET expression trees compiled by the JIT; evaluation short-circuits.
- **Fast over large captures.** With a `PacketIndex`, a filter prunes the candidate packets with presence bitmaps
  before it evaluates a single packet.
- **Beyond Wireshark.** Subtree scopes (`$udp { udp.dstport == 53 }`) bind conditions to one protocol instance;
  `flank(...)` detects value edges across packets within a time or packet window.
- **Works without a field tree.** A `FilterObserver` evaluates during a skip parse, for high-throughput pipelines.
- **Editor support.** `Filter.GetCompletions` returns completions at the caret; errors carry position, length, and a
  suggestion.
- **Safe on untrusted expressions.** Bounded nesting (64) and term count (1024), linear-time regex where possible
  and a timeout otherwise, errors as values instead of exceptions.
- **Used everywhere.** Session listeners and both `ni` CLI commands (`--filter`) use this language.

## Install

```bash
dotnet add package NetworkInspector.Filter
dotnet add package NetworkInspector.Protocols
```

## Quick Start

```csharp
using NetworkInspector.Core;
using NetworkInspector.Core.Ids;
using NetworkInspector.Core.Settings;
using NetworkInspector.Filter;
using NetworkInspector.Filter.Errors;
using NetworkInspector.Protocols;
using NetworkInspector.Values;

StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
builder.RegisterStandardProtocols();
using Stack stack = builder.Build();

// Compile once against the stack: unknown names fail here, with a suggestion.
if (!Filter.TryCompile("udp.dstport == 53 && ip.ttl > 32", stack, out Filter? filter, out FilterError? error))
{
    Console.Error.WriteLine(error); // [Kind] at position (length n): message
    return;
}

// Ethernet / IPv4 192.168.1.1 -> 192.168.1.2 / UDP 12345 -> 53
byte[] raw = Convert.FromHexString(
    "001122334455" + "66778899AABB" + "0800" +
    "4500001C" + "12344000" + "4011A549" + "C0A80101" + "C0A80102" +
    "30390035" + "00080000");
Frame frame = Frame.Create(new FrameId(0), Timestamp.FromSecs(0), raw,
    LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

ParseOptions options = new(); // filters evaluate the field tree
if (!Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure)
    || packet is null)
{
    throw new InvalidOperationException(failure.ToString());
}

// false = no verdict (for example a runtime error), not "no match".
if (!filter.TryIsMatch(packet, out bool matched, out FilterError? evalError))
{
    throw new InvalidOperationException(evalError.ToString());
}

Console.WriteLine(matched); // True
```

Inside a namespace that starts with `NetworkInspector`, the name `Filter` refers to the namespace; use an alias
such as `using PacketFilter = NetworkInspector.Filter.Filter;`.

## Language At A Glance

| Expression | Meaning |
| --- | --- |
| `tcp`, `!dns` | Protocol present / absent |
| `ip.src == 10.0.0.1` | Comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) |
| `tcp.port in {80, 443}`, `udp.port in 1024..65535` | Set and range membership |
| `http.host contains "example"` | Ordinal substring |
| `http.user_agent matches "(?i)curl"` | .NET regular expression |
| `udp.payload[0:2] == 00:2a`, `len(udp.payload) > 100` | Byte slices and lengths |
| `eth.addr == FF:FF:FF:FF:FF:FF` | Alias: any member field matches |
| `$udp { udp.dstport == 53 }` | Subtree scope: one UDP instance satisfies all conditions |
| `flank(can.id, changed, within: 10ms)` | Stateful edge detection across packets |

A repeated field matches if any occurrence matches. Keywords are case-insensitive; `and`, `or`, `not` work as well
as `&&`, `||`, `!`. Full grammar and semantics: [`FILTER_GUIDE.md`](FILTER_GUIDE.md).

## Common Tasks

### Filter Many Packets With The Presence Index

Record a `PacketIndex` during parsing and pass it to the filter; packets that cannot match are rejected from the
index without evaluating the tree:

```csharp
using NetworkInspector.Core.Index;

PacketIndex index = new(stack);
ParseOptions indexed = new(index: index);
// ... Packet.TryParse(..., in indexed, ...) for every frame ...
filter.TryIsMatch(packet, index, out bool matched, out FilterError? evalError);
```

`TryBuildCandidates(index, out RoaringBitmap? candidates)` returns the whole candidate set at once.

### Offer Completions In An Editor

```csharp
FilterCompletions completions = Filter.GetCompletions("udp.dst", caretPosition: 7, stack);
foreach (FilterCompletionItem item in completions.Items)
{
    Console.WriteLine($"{item.Text} ({item.Kind})"); // udp.dstport (Field)
}
```

`ReplaceStart` and `ReplaceLength` give the span to replace. Completion is thread-safe and caches one name catalog
per stack.

### Evaluate During A Skip Parse

`Filter.Compile(expression, stack, in FilterObserverOptions options)` returns a `FilterObserver`. Pass it as a
parse observer with `FieldTreeMode.Skip` and read `IsMatch` after the parse (`TryReadMatch` for `flank`). Observers
reject subtree scopes and protocol presence without a container field (`NeedsFieldTree`).

## Errors

`FilterError` has `Kind` (`FilterErrorKind`), `Message`, `Position`, `Length`, and `Suggestion`. `ToString()`
prints `[Kind] at P (length L): Message`.

- Compile errors: `LexerError`, `SyntaxError`, `UnknownField` (with a suggestion), `TypeMismatch`, `InvalidValue`,
  `UnsupportedFeature`, `StackRequired`, `NeedsFieldTree`.
- Evaluation errors poison the filter (fail closed) until `ResetState()` or `TryDerive(newStack, …)`.
- An empty expression compiles to `Filter.AlwaysMatch` and needs no stack.

## Limits And Thread-Safety Notes

- A `Filter` instance is single-threaded; compile one per thread or derive copies with `TryDerive`.
  `Filter.AlwaysMatch` is immutable and shared.
- Results are cached per `PacketId` by default. Turn the cache off with
  `FilterCompileOptions.EnableMatchCache = false` for forward-only pipelines that never revisit a packet.
- Stateful filters (`flank`) require ascending packet ids and do not use index pruning.
- Expressions nest at most 64 levels and hold at most 1024 terms; `matches` patterns that need backtracking run
  with `RegexTimeout` (default 1 s).
- Runtime code generation: not compatible with NativeAOT or trimming.

## Learn More

- [Filter guide](FILTER_GUIDE.md): grammar, types, aliases, scopes, `flank`, observers, completion, errors
- [Sessions](../NetworkInspector.Sessions/README.md) (per-listener filters) · [CLI](../NetworkInspector.CLI/README.md)
  (`--filter`) · [Core](../NetworkInspector.Core/README.md) · [Root overview](../README.md)
- [GitHub repository](https://github.com/DevAM-Tools/NetworkInspector) ·
  [Issue tracker](https://github.com/DevAM-Tools/NetworkInspector/issues)

## License

[MIT License](../LICENSE)
