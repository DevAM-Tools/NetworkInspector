<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# NetworkInspector.Generators

> **Note:** This generator is bundled with `NetworkInspector.Core`. It activates automatically
> when you add the `NetworkInspector.Core` NuGet package — no separate installation required.

Roslyn incremental source generator for the NetworkInspector protocol framework.
Processes `[Protocol]`-annotated protocol classes and generates all registration boilerplate:
field IDs, dispatch tables, settings, index groups, lifecycle hooks, and public constants.

---

## Table of Contents

1. [What It Generates](#what-it-generates)
2. [Supported Attributes](#supported-attributes)
3. [Generated Members Reference](#generated-members-reference)
4. [Hook Contract: RegisterFieldsCustom vs OnStartCustom](#hook-contract)
5. [Complete Example](#complete-example)
6. [Diagnostics](#diagnostics)

---

## What It Generates

For each `partial class` decorated with `[Protocol]`, the generator emits a second partial class file
containing:

| Member | Kind | Visibility | Description |
|--------|------|-----------|-------------|
| `ProtocolName` | `const string` | `public` | Stable machine-readable protocol name |
| `ProtocolUiName` | `const string` | `public` | Human-readable display name |
| `TableName*` | `const string` | `public` | One constant per `[ProtocolTable*]` field — the contract name for cross-protocol table references |
| `IndexGroup*` | `const string` | `private` | One constant per distinct `IndexGroup` value used across field attributes |
| `_ProtocolId` | `ProtocolId` field | `private` | Assigned in `RegisterFields`; available in all three custom hooks |
| `_*GroupId` | `IndexGroupId` field | `private` | One field per distinct index group |
| `RegisterFields(IStackBuilder, ProtocolId)` | method | `public` | Full registration: fields, tables, settings, dispatch entries, setting load, then `RegisterFieldsCustom` |
| `OnStart(Stack)` | method | `public` | Calls `OnStartCustom` |
| `OnShutdown(Stack)` | method | `public` | Calls `OnShutdownCustom` |
| `RegisterFieldsCustom(IStackBuilder, ProtocolId)` | `partial void` | — | Hook: called at the end of `RegisterFields` |
| `OnStartCustom(Stack)` | `partial void` | — | Hook: called from `OnStart` after the stack is frozen |
| `OnShutdownCustom(Stack)` | `partial void` | — | Hook: called from `OnShutdown` |
| Property accessors `*TableId` | property | `public` | One read-only property per `[ProtocolTable*]` field — exposes the table ID for other protocols |

> **Field ID fields (`_*FieldId`) are NOT generated.** Authors declare them by hand with the
> appropriate `[*Field]` attribute. The generator only assigns the runtime `FieldId` values to those
> fields inside `RegisterFields`. Add XML doc comments to these fields in your own source file.

---

## Supported Attributes

### Protocol Marker

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[Protocol(name, uiName)]` | `class` | Marks a class as a protocol. Triggers code generation. Optional: `Description`. |

### Field Registration Attributes

All field attributes target `FieldId` **fields** (not properties). Each accepts `name` (machine-readable),
`uiName` (display), and optional `IndexGroup` and `Description` named parameters.

| Attribute | `FieldType` | Description |
|-----------|-------------|-------------|
| `[NoneField]` | `None` | Grouping node — holds child fields, no own value |
| `[BoolField]` | `Bool` | Boolean |
| `[I64Field]` | `I64` | Signed 64-bit integer |
| `[U64Field]` | `U64` | Unsigned 64-bit integer |
| `[F64Field]` | `F64` | 64-bit floating-point |
| `[StringField]` | `String` | UTF-8 string |
| `[BytesField]` | `Bytes` | Raw byte sequence |
| `[MacField]` | `MacAddress` | 48-bit MAC address |
| `[IPv4Field]` | `IPv4Address` | 32-bit IPv4 address |
| `[IPv6Field]` | `IPv6Address` | 128-bit IPv6 address |
| `[Eui64Field]` | `Eui64` | 64-bit EUI-64 identifier |
| `[UuidField]` | `Uuid` | 128-bit UUID |
| `[TimestampField]` | `Timestamp` | Nanosecond-precision timestamp |

### Protocol Dispatch Table Attributes

All table attributes target `ProtocolTableId` **fields**. They accept `name` (machine-readable),
`uiName` (display), and optional `Description`.
The generator emits a `public const string TableName<PascalName>` constant and a public read-only
property for each table. **Other protocols must reference the table by this constant, not the raw
string**, to maintain a stable cross-protocol contract.

| Attribute | Key type | Description |
|-----------|----------|-------------|
| `[ProtocolTableU64]` | `ulong` | Dispatch by 64-bit integer (e.g., EtherType, IP protocol number) |
| `[ProtocolTableString]` | `string` | Dispatch by text key |
| `[ProtocolTableBytes]` | `byte[]` | Dispatch by binary key (e.g., magic signatures) |
| `[ProtocolTableBool]` | `bool` | Binary branching |
| `[ProtocolTableAny]` | — | Catch-all; one parser handles all remaining data |

### External Table Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[UsesTable(name)]` | `ProtocolTableId` field | Resolves an external table ID at build time via `WhenProtocolTableRegistered`. Use this when your protocol needs to register into a table owned by another protocol. |

### Dispatch Registration Attributes (class-level)

These attributes register the protocol into a dispatch table. Multiple attributes are allowed.
Use the owning protocol's `TableName*` constant for the `table` argument.

| Attribute | Key type | Description |
|-----------|----------|-------------|
| `[RegisterAtTable(table, key)]` | `ulong` | Register at a U64 key |
| `[RegisterAtStringTable(table, key)]` | `string` | Register at a string key |
| `[RegisterAtBoolTable(table, key)]` | `bool` | Register at a bool key |
| `[RegisterAtBytesTable(table, params byte[] key)]` | `byte[]` | Register at a byte-sequence key |
| `[RegisterAtAnyTable(table)]` | — | Register as catch-all |

### Setting Attributes

All setting attributes target **fields** of the matching CLR type and accept `name`, `uiName`,
`groupName`, and optional `Description`. The generator registers the setting in `RegisterFields`
and loads its value into the backing field before calling `RegisterFieldsCustom`.

| Attribute | Field type | Extra properties |
|-----------|-----------|-----------------|
| `[BoolSetting]` | `bool` | `Default` |
| `[StringSetting]` | `string` | `Default` |
| `[F64Setting]` | `double` | `Default`, `Min`, `Max` (use `double.NaN` to omit) |
| `[U64Setting]` | `ulong` | `Default`, `HasMin`+`Min`, `HasMax`+`Max` |
| `[I64Setting]` | `long` | `Default`, `HasMin`+`Min`, `HasMax`+`Max` |
| `[BytesSetting]` | `byte[]` | `DefaultHex` — even-length uppercase hex string, e.g. `"0102AABB"` |
| `[EnumSetting]` | `ulong` | `Default`, `AllowedValues` — semicolon-delimited `Name=Value` pairs, e.g. `"Off=0;Low=1;High=2"` |

---

## Generated Members Reference

### `ProtocolName` and `ProtocolUiName`

```csharp
public const string ProtocolName   = "eth";
public const string ProtocolUiName = "Ethernet";
```

`ProtocolName` is the stable machine-readable identifier for the protocol. It is guaranteed never to
change for a given protocol implementation; cross-protocol code (e.g., `ParseResult.ProtocolName`
comparisons, filter expressions) must reference this constant rather than a raw string literal to
avoid silent breakage if the name ever needs updating.

`ProtocolUiName` is the human-readable display name for UI surfaces. It is **not** a stability
contract and may change across releases as display requirements evolve. Do not use it for any
machine-readable comparison.

### `TableName*` Constants

```csharp
public const string TableNameEthType = "eth.type";
```

Every `[ProtocolTable*]` field generates one `public const string TableName<PascalName>` constant.
These constants define the **cross-protocol dispatch contract**: another protocol that registers into
or resolves this table must use the constant (not a raw string literal) to guarantee consistency if
the name ever changes. Example:

```csharp
// In EthernetProtocol (owner):
[ProtocolTableU64("eth.type", "EtherType")]
private ProtocolTableId _EtherTypeTableId;
// → generator emits: public const string TableNameEthType = "eth.type";
//                    public ProtocolTableId EtherTypeTableId => _EtherTypeTableId;

// In IPv4Protocol (consumer):
[RegisterAtTable(EthernetProtocol.TableNameEthType, 0x0800)]
public sealed partial class IPv4Protocol : IProtocol { ... }
```

### `_ProtocolId` Field

```csharp
private ProtocolId _ProtocolId;
```

Assigned in `RegisterFields` after the protocol is registered. Available in all three custom
partial hooks (`RegisterFieldsCustom`, `OnStartCustom`, `OnShutdownCustom`). Typically used
for `RecordProtocolPresence(_ProtocolId)` calls inside `Parse`.

### Field ID Fields (`_*FieldId`)

These fields are **not generated**. Authors declare them by hand:

```csharp
[U64Field("eth.type", "EtherType")]
private FieldId _EtherTypeFieldId;
```

The generator reads the attributes and emits code that assigns the runtime `FieldId` values inside
`RegisterFields`. Add XML doc comments to these fields in your own source file; the generator does
not add documentation to user-declared fields.

---

## Hook Contract

The generator emits three partial methods that authors can optionally implement in the hand-written
partial class file. Understanding which hook to use is important for correctness:

```
RegisterFields(builder, protocolId)       — called while stack is being built (not yet frozen)
│
├── … all field/table/setting registrations …
├── … setting values loaded into backing fields …
│
└── RegisterFieldsCustom(builder, protocolId)   ← your hook here
    │
    │  Stack frozen here. builder.Build() completes.
    │
OnStart(stack)                             — called after stack is frozen
└── OnStartCustom(stack)                   ← your hook here

OnShutdown(stack)                          — called on session teardown
└── OnShutdownCustom(stack)                ← your hook here
```

### `RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)`

Use this hook for everything that only requires the **builder** (unfrozen stack):

- Loading a JSON/XML config file and registering additional dispatch entries based on its content
- Building lookup dictionaries keyed by runtime `FieldId` values (they are assigned before this hook is called)
- Registering additional `WhenProtocolTableRegistered` callbacks for dynamic dispatch
- Resolving cross-protocol field IDs via `builder.WhenFieldRegistered`

Setting values are guaranteed to be loaded into backing fields **before** this hook is called.

### `OnStartCustom(Stack stack)`

Use this hook **only** for setup that requires the **frozen, fully-built stack**:

- Pre-binding `ParseDelegate` caches (e.g., `stack.ResolveParseDelegate(...)`)
- Any lookup that requires the complete, immutable protocol registry

Do **not** register new fields, tables, or settings here — the stack is frozen and will throw.
Do **not** load config files here — that belongs in `RegisterFieldsCustom`.

### `OnShutdownCustom(Stack stack)`

Use this hook for cleanup: releasing resources, flushing caches, or logging final statistics.

---

## Complete Example

```csharp
// Hand-written partial class (MyProtocol.cs)

namespace MyNamespace;

/// <summary>Example protocol demonstrating all generator features.</summary>
[Protocol("myproto", "My Protocol", Description = "Example (RFC 9999)")]
[RegisterAtTable(EthernetProtocol.TableNameEthType, 0xABCD)]
public sealed partial class MyProtocol : IProtocol
{
    // ── Field ID fields (hand-written; generator assigns runtime IDs in RegisterFields) ───────

    /// <summary>Root grouping field for all MyProtocol fields.</summary>
    [NoneField("myproto", "My Protocol", IndexGroup = "myproto")]
    private FieldId _RootFieldId;

    /// <summary>Version field.</summary>
    [U64Field("myproto.version", "Version", IndexGroup = "myproto")]
    private FieldId _VersionFieldId;

    /// <summary>Payload bytes.</summary>
    [BytesField("myproto.data", "Data")]
    private FieldId _DataFieldId;

    // ── Protocol table fields (hand-written; generator emits TableName* constant + property) ──

    /// <summary>Sub-protocol dispatch table keyed by MyProto type code.</summary>
    [ProtocolTableU64("myproto.type", "MyProto type")]
    private ProtocolTableId _TypeTableId;

    // ── External table reference (generator emits WhenProtocolTableRegistered) ───────────────

    /// <summary>Reference to the IPv4 protocol table owned by IPv4Protocol.</summary>
    [UsesTable("ip.proto")]
    private ProtocolTableId _IpProtoTableId;

    // ── Settings (hand-written; generator registers + loads before RegisterFieldsCustom) ──────

    /// <summary>Whether to emit verbose diagnostics.</summary>
    [BoolSetting("myproto.verbose", "Verbose", "MyProtocol", Default = false)]
    private bool _Verbose;

    /// <summary>Maximum payload size (bytes) to accept.</summary>
    [U64Setting("myproto.max_size", "Max payload size", "MyProtocol",
        Default = 1500, HasMax = true, Max = 65535)]
    private ulong _MaxPayloadSize;

    /// <summary>Log level (0=Off, 1=Errors, 2=All).</summary>
    [EnumSetting("myproto.loglevel", "Log level", "MyProtocol",
        Default = 0, AllowedValues = "Off=0;Errors=1;All=2")]
    private ulong _LogLevel;

    // ── Runtime state populated in RegisterFieldsCustom ───────────────────────────────────────

    private ParseDelegate? _FastSubParser;

    // ── Custom hooks ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called at the end of RegisterFields. Setting values are already loaded.
    /// Use for config-driven setup and dispatch registration.
    /// </summary>
    partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId)
    {
        // Setting values are already populated; use them to drive registration.
        if (_Verbose)
        {
            // Register an additional dispatch entry based on runtime config.
            builder.WhenProtocolTableRegistered("myproto.type", id =>
            {
                // id is the runtime ProtocolTableId for "myproto.type"
            });
        }
    }

    /// <summary>
    /// Called after the stack is frozen. Use only for setup requiring the complete stack.
    /// </summary>
    partial void OnStartCustom(Stack stack)
    {
        // Pre-bind a ParseDelegate to avoid per-packet lookup overhead.
        _FastSubParser = stack.ResolveParseDelegate("someprotocol");
    }

    partial void OnShutdownCustom(Stack stack)
    {
        _FastSubParser = null;
    }

    // ── IProtocol implementation ──────────────────────────────────────────────────────────────

    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, Stack stack)
    {
        parentField.RecordProtocolPresence(_ProtocolId);   // _ProtocolId is generated
        // ... parse logic ...
        return ParseResult.Ok;
    }
}
```

The generator emits a second partial class file with (abbreviated):

```csharp
// <auto-generated/>

namespace MyNamespace;

[GeneratedCode("NetworkInspector.Generators.ProtocolGenerator", "1.0.0")]
[ExcludeFromCodeCoverage]
partial class MyProtocol
{
    /// <summary>
    /// Machine-readable protocol name constant.
    /// <remarks>This constant is the stable identity of the protocol. Cross-protocol code and
    /// filter expressions must reference this constant (not a raw string) so that any future
    /// rename is caught at compile time.</remarks>
    /// </summary>
    public const string ProtocolName = "myproto";

    /// <summary>
    /// Human-readable display name.
    /// <remarks>This value is intended for UI surfaces only and is not a stability contract.
    /// Do not use it in machine-readable comparisons or persisted data.</remarks>
    /// </summary>
    public const string ProtocolUiName = "My Protocol";

    /// <summary>
    /// Dispatch table name constant for "myproto.type".
    /// <remarks>This constant is the cross-protocol contract for this dispatch table.
    /// Other protocols registering into this table must reference this constant, not the raw
    /// string, to ensure consistency.</remarks>
    /// </summary>
    public const string TableNameMyprotoType = "myproto.type";

    private const string IndexGroupMyproto = "myproto";  // index group constant

    public string Name    => ProtocolName;
    public string UiName  => ProtocolUiName;
    public string Description => "Example (RFC 9999)";

#pragma warning disable CS0414
    private ProtocolId _ProtocolId;       // assigned in RegisterFields
#pragma warning restore CS0414

    private IndexGroupId _MyprotoGroupId; // index group ID

    public void RegisterFields(IStackBuilder builder, ProtocolId protocolId) { ... }
    public void OnStart(Stack stack)    { OnStartCustom(stack); }
    public void OnShutdown(Stack stack) { OnShutdownCustom(stack); }

    public ProtocolTableId TypeTableId => _TypeTableId;  // accessor for dispatch table

    partial void RegisterFieldsCustom(IStackBuilder builder, ProtocolId protocolId);
    partial void OnStartCustom(Stack stack);
    partial void OnShutdownCustom(Stack stack);
}
```

---

## Diagnostics

The generator emits the following diagnostics. All are errors unless noted.

| ID | Severity | Condition |
|----|----------|-----------|
| `NIGEN001` | Error | Duplicate field name within the same protocol class |
| `NIGEN002` | Error | Duplicate setting name within the same protocol class |
| `NIGEN003` | Error | Duplicate dispatch table name within the same protocol class |
| `NIGEN004` | Error | Invalid character in an index group or table name (only `[a-zA-Z0-9._]` allowed; hyphens are not allowed) |
| `NIGEN005` | Error | Non-numeric value in an `[EnumSetting]` `AllowedValues` pair |
| `NIGEN006` | Error | Protocol class is generic (type parameters are not allowed) |
| `NIGEN007` | Error | Protocol class is nested inside another type |
| `NIGEN008` | Error | Protocol class is in the global namespace (must be in a named namespace) |
| `NIGEN009` | Error | Invalid boolean dispatch key type (target table is not a Bool table) |
| `NIGEN010` | Warning | Unknown field attribute type — field registered as `FieldType.None` |
| `NIGEN011` | Error | Invalid hex string in `[BytesSetting].DefaultHex` (must be even-length, uppercase hex) |
| `NIGEN012` | Error | Protocol class is not declared `partial` (the generator emits a companion partial class that would not compile without it) |
| `NIGEN013` | Warning | Attribute payload incomplete — missing required positional arguments; the attribute was skipped |

---

## License

[MIT License](../LICENSE) — © DevAM
