<!-- Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information. -->

# Protocols in a Plugin

This chapter of the [plugin guide](guide.md) shows how a plugin teaches the stack a new protocol: the protocol class,
its fields, how traffic reaches it (a port or another dispatch table, a heuristic parser), and post-parsers that
annotate every frame. Everything here runs inside a stack contribution:

```csharp
builder.AddStackContribution(static stack => MyRegistration.Register(stack));
```

The protocols inside NetworkInspector use source-generated registration; the
[protocol guide](../../NetworkInspector.Protocols/PROTOCOL_GUIDE.md) describes that form, the threading model, and
cross-protocol references in depth. This chapter shows the hand-written form, which needs nothing but Core and is what
the [sample plugin](../../samples/Sample.Protocol/README.md) uses.

## 1. The protocol class

A protocol implements `IProtocol` (namespace `NetworkInspector.Core.Protocols`):

| Member | Purpose |
| --- | --- |
| `string Name` | Machine name, also the prefix of the field names (`sample`) |
| `string UiName` | Display name (`Sample protocol`) |
| `string? Description` | One sentence; default `null` |
| `ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)` | Decodes the bytes handed to the protocol |
| `void OnStart(Stack stack)`, `void OnShutdown(Stack stack)` | Optional hooks after the stack is built and when it shuts down |

`Parse` returns a `ParseResult`. An `int` converts to the number of consumed bytes; a `ParseError` converts to a
failure:

| Factory | When |
| --- | --- |
| `ParseError.InsufficientDataWithInfo(protocol, needed, available)` | Fewer bytes than the header needs |
| `ParseError.InvalidData(protocol, message)` | The bytes cannot be this protocol (wrong magic, impossible value) |
| `ParseError.Custom(…)` | Any other decoding problem with your own text |

A failure is recorded on the packet (the packet still exists, with the fields decoded so far) and is visible to
filters, exports, and `ni export -f text`.

```csharp
public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
{
    // Check the length before any read: capture bytes are untrusted input.
    ReadOnlySpan<byte> span = data.Span;
    if (span.Length < SampleWire.PayloadLength)
    {
        return ParseError.InsufficientDataWithInfo("sample", SampleWire.PayloadLength, (ulong)span.Length);
    }

    // A port registration cannot tell this payload from other traffic on the port: check the magic.
    if (!span[..4].SequenceEqual("NIS1"u8))
    {
        return ParseError.InvalidData("sample", "Magic is not NIS1.");
    }

    ulong counter = BinaryPrimitives.ReadUInt64BigEndian(span[4..]);
    context.RecordProtocolPresence(_ProtocolId);

    MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);
    container.Append(_CounterFieldId, FieldValue.NewU64(counter));

    // Formatted only when somebody reads the info: nothing is allocated per packet for it.
    container.SetPacketInfo(LazyString.FormatLazy(counter,
        static c => string.Create(CultureInfo.InvariantCulture, $"Sample counter={c}")));
    return SampleWire.PayloadLength;
}
```

### Threading and state

One protocol instance serves every packet of its stack, and packets may be re-parsed on several threads at the same
time (a session re-reads packets on demand). Therefore:

- **Keep `Parse` stateless.** Field ids are written once while the stack is built and only read afterwards. Everything
  else comes from `data` and `context`.
- **First parse and re-parse decode the same bytes the same way.** A re-parse must produce exactly the fields of the
  first parse. A protocol that needs state across packets (reassembly, learned ports) follows the first-parse versus
  re-parse rules of the protocol guide, section 2.
- **Create a new instance in every stack contribution call.** The callback runs for every stack; two stacks never share
  a protocol object.
- **Record presence.** `context.RecordProtocolPresence(protocolId)` lets the packet index answer filters such as
  `sample` without parsing the packet again.

## 2. Fields

Register the fields right after the protocol, in the same contribution, and keep their ids in the protocol instance:

```csharp
ProtocolId id = stack.RegisterProtocol(protocol);
FieldId container = stack.RegisterField(id, "sample", "Sample", FieldType.None, "Container of the sample payload.");
FieldId counter = stack.RegisterField(id, "sample.counter", "Counter", FieldType.U64, "Frame counter.");
```

| `FieldType` | `FieldValue` factory |
| --- | --- |
| `None` (a container with children only) | `FieldValue.None` |
| `U64`, `I64`, `F64`, `Bool` | `NewU64`, `NewI64`, `NewF64`, `NewBool` |
| `String`, `Bytes` | `NewString`, `NewBytes` |
| `MacAddress`, `IPv4Address`, `IPv6Address`, `Eui64`, `Uuid`, `Timestamp` | `NewMacAddress`, `NewIPv4`, `NewIPv6`, `NewEui64`, `NewUuid`, `NewTimestamp` |

- **Names are global.** Every field name is unique in the stack. Use the protocol name as the prefix
  (`sample.counter`), lower case, dots between levels, underscores inside a level.
- **The name is the filter name.** `sample.counter > 100`, `sample` (presence), and CSV `fields=sample.counter` work
  as soon as the field is registered.
- **Append in tree order.** `parentField.Append` adds a child to the field the parent protocol handed you; append to the
  returned `MutField` to nest further.
- **Write a description.** It is shown in field listings and completions.

## 3. How traffic reaches the protocol

### 3.1 Dispatch tables

A dispatch table maps a key of one protocol to the next protocol: UDP uses the destination and source port, Ethernet
the EtherType. A plugin registers its protocol under a key of an existing table:

```csharp
// Deferred: registers at once when the table exists, and as soon as it appears otherwise.
stack.WhenProtocolTableRegistered("udp.port", table => stack.RegisterParserInU64Table(table, 40000, id));

// Immediate: the table must exist already, or the call throws.
stack.RegisterParserInU64TableByName("tcp.port", 40000, id);
```

Use the deferred form for tables of other protocols: the plugin then works whatever the registration order is, and a
stack without that table simply has no registration instead of failing the plugin. Tables of the standard protocols
include `eth.type`, `vlan.etype`, `ip.proto`, `ipv6.nxt`, `udp.port`, `tcp.port`, and `can.id`; the source of each
protocol names the tables it owns.

| Key type | Registration |
| --- | --- |
| Unsigned number (ports, EtherTypes, ids) | `RegisterParserInU64Table`, `RegisterParserInU64TableByName` |
| Text | `RegisterParserInStringTable`, `…ByName` |
| Bytes | `RegisterParserInBytesTable`, `…ByName` |
| Boolean | `RegisterParserInBoolTable`, `…ByName` |
| Any key (a catch-all entry) | `RegisterParserInAnyTable`, `…ByName` |

A protocol that dispatches to further protocols registers its own table with `RegisterProtocolTable(name, uiName,
keyType, description)`; other plugins can then register under it.

### 3.2 Heuristic parsers

When no table entry matches, TCP and UDP ask their heuristic tables `tcp.heuristic` and `udp.heuristic`. A heuristic
parser looks at the payload and says whether it is its protocol:

```csharp
internal sealed class SampleHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    public ProtocolId ProtocolId { get; } = protocolId;
    public string Name => "sample.heuristic";
    public string UiName => "Sample NIS1 magic";
    public string? Description => "Detects the NIS1 magic in TCP payload on any port.";

    // Cheap and certain: a complete payload that starts with the magic.
    public bool Test(ReadOnlyMemory<byte> data) =>
        data.Length >= SampleWire.PayloadLength && data.Span[..4].SequenceEqual("NIS1"u8);
}
```

```csharp
if (stack.GetHeuristicProtocolTableId("tcp.heuristic") is HeuristicProtocolTableId table)
{
    stack.RegisterHeuristicParser(table, new SampleHeuristicParser(id));
}
```

- Port registrations keep priority: a heuristic parser only sees payloads that no table entry consumed.
- `Test` runs for every candidate payload of every packet. Keep it fast, allocation-free, and strict: a false positive
  steals traffic from the protocols after it.
- A protocol can own a heuristic table for its own payload with `RegisterHeuristicProtocolTable`.

### 3.3 Post-parsers

A post-parser runs after the main parse of every packet and sees the whole frame. It adds root-level fields without
consuming anything, so it can annotate traffic that no protocol of the plugin dispatched:

```csharp
SamplePostParser postParser = new();
ProtocolId postId = stack.RegisterProtocol(postParser);
postParser.RegisterFields(stack, postId);
stack.RegisterPostParser(postId, priority: 0, description: "Marks frames that carry the NIS1 magic.");
```

Its `Parse` returns `0` (nothing consumed). Post-parsers run in ascending `priority` (lower values first, equal values in
registration order); keep them as cheap as heuristic tests, because they run for every packet.

### 3.4 Stream reassembly

A protocol over TCP whose messages span segments registers a reassembly configuration with
`RegisterStreamReassemblyConfig(protocolId, config)`; TCP then hands the protocol whole messages. The protocol guide
describes the configuration and the limits it needs.

## 4. Protocol settings

Settings that change how a protocol decodes (a port list, a switch) are registered inside the stack contribution with
`stack.SettingsRegistrar`, the way the built-in protocols register theirs:

```csharp
stack.SettingsRegistrar.RegisterBoolSetting("sample.strict", "Strict magic", "sample", true,
    "Refuse payloads whose magic is not NIS1.");
```

They belong to the profile of the run, so `ni settings set sample.strict false --profile CAN` changes them per profile;
how a protocol reads its values is described in the protocol guide, section 4 ("Runtime Settings"). Settings that belong
to the plugin as a whole use `AddSettings` instead (see [guide section 5](guide.md#5-options-and-settings)).

## 5. Checklist

- [ ] `Parse` checks every length before it reads and never allocates memory sized by a value from the wire.
- [ ] `Parse` keeps no state across calls, or follows the first-parse versus re-parse rules.
- [ ] The contribution creates new protocol objects on every call.
- [ ] Protocol, field, table, and setting names carry the protocol name as prefix.
- [ ] Foreign tables are joined with `WhenProtocolTableRegistered` or after a `Get…TableId` check.
- [ ] Heuristic tests and post-parsers are cheap and strict.
- [ ] The packet info is formatted lazily.
