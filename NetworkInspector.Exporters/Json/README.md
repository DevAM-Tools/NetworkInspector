<!-- Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root. -->

# JSON Exporter

The JSON exporter serializes parsed packets as a JSON array. It implements `IPacketListener` and supports three output formats, cancellation, and same-as-previous field deduplication.

## Output Formats

### Compact (`JsonExportFormat.Compact`)

Minimal output using short 2-character keys and same-as-previous value deduplication. Designed for machine consumption and minimum file size.

**Packet-level keys:**

| Key  | Meaning            | Type    | Always Present |
|------|--------------------|---------|----------------|
| `ID` | Packet ID          | integer | yes            |
| `TS` | Timestamp (nanos)  | integer | yes            |
| `IN` | Info string        | string  | no (omitted when same-as-previous) |
| `SF` | Same-as-previous flags | integer | no (omitted when 0) |
| `CH` | Children (fields)  | array   | no (omitted when empty) |

**Field-level keys:**

| Key  | Meaning                  | Type    | Always Present |
|------|--------------------------|---------|----------------|
| `FI` | Field ID                 | integer | yes            |
| `NA` | Field name               | string  | first occurrence only |
| `UI` | UI display name          | string  | first occurrence only |
| `TY` | Field type               | integer | first occurrence only |
| `VA` | Value                    | varies  | no (omitted when same-as-previous) |
| `CR` | Custom representation    | string  | no (omitted when same-as-previous) |
| `CT` | Custom text              | string  | no (omitted when same-as-previous) |
| `SF` | Same-as-previous flags   | integer | no (omitted when 0) |
| `CH` | Children (sub-fields)    | array   | no (omitted when empty) |

**Packet-level same-as-previous flags** (the `SF` key on a packet object):

| Bit | Constant             | Value  | Meaning                              |
|-----|----------------------|--------|--------------------------------------|
| 0   | `PACKET_SAME_INFO`   | `0x01` | Packet info string same as previous packet |

**Field-level same-as-previous flags** (the `SF` key on a field object):

| Bit | Constant                          | Value  | Meaning                              |
|-----|-----------------------------------|--------|--------------------------------------|
| 0   | `FIELD_SAME_VALUE`                | `0x01` | Field value same as previous packet  |
| 1   | `FIELD_SAME_CUSTOM_REPRESENTATION`| `0x02` | Custom representation same as previous |
| 2   | `FIELD_SAME_CUSTOM_TEXT`          | `0x04` | Custom text same as previous packet  |

**Field-info deduplication:** `NA`, `UI`, and `TY` are written only on the first occurrence of each `FI` value across the entire file. Subsequent occurrences omit these keys since the consumer can look them up by field ID.

**Example (compact):**
```json
[
{"ID":1,"TS":1711612345000000000,"IN":"UDP 192.168.1.1:5000 → 10.0.0.1:8080","CH":[{"FI":1,"NA":"eth","UI":"Ethernet","TY":0,"CH":[{"FI":2,"NA":"eth.dst","UI":"Destination","TY":4,"VA":"ff:ff:ff:ff:ff:ff"},{"FI":3,"NA":"eth.src","UI":"Source","TY":4,"VA":"00:11:22:33:44:55"}]}]},

{"ID":2,"TS":1711612345000001000,"SF":1,"CH":[{"FI":1,"CH":[{"FI":2,"SF":1},{"FI":3,"SF":1}]}]}
]
```

### Pretty (`JsonExportFormat.Pretty`)

Human-readable output with full keys and 2-space indentation. No deduplication.

**Packet-level keys:**

| Key          | Meaning            | Type    |
|--------------|--------------------|---------|
| `id`         | Packet ID          | integer |
| `timestamp`  | Timestamp (nanos)  | integer |
| `info`       | Info string        | string  |
| `fields`     | Children (fields)  | array   |

**Field-level keys:**

| Key           | Meaning                  | Type    |
|---------------|--------------------------|---------|
| `field_id`    | Field ID                 | integer |
| `name`        | Field name               | string  |
| `ui_name`     | UI display name          | string  |
| `type`        | Field type               | integer |
| `value`               | Value                    | varies  |
| `custom_representation` | Custom representation  | string  |
| `custom_text`         | Custom text              | string  |
| `children`            | Children (sub-fields)    | array   |

**Example (pretty):**
```json
[
  {
    "id": 1,
    "timestamp": 1711612345000000000,
    "info": "UDP 192.168.1.1:5000 → 10.0.0.1:8080",
    "fields": [
      {
        "field_id": 1,
        "name": "eth",
        "ui_name": "Ethernet",
        "type": 0,
        "children": [
          {
            "field_id": 2,
            "name": "eth.dst",
            "ui_name": "Destination",
            "type": 4,
            "value": "ff:ff:ff:ff:ff:ff"
          }
        ]
      }
    ]
  }
]
```

### Array (`JsonExportFormat.Array`)

Flat JSON objects (no indentation) with full keys. Same keys as Pretty format. Designed for line-oriented consumers or streaming JSON arrays.

**Example (array):**
```json
[
{"id":1,"timestamp":1711612345000000000,"info":"UDP ...","fields":[{"field_id":1,"name":"eth","ui_name":"Ethernet","type":0,"children":[...]}]},
{"id":2,"timestamp":1711612345000001000,"info":"UDP ...","fields":[...]}
]
```

## Field Value Types

Values are serialized differently depending on the field type:

| Field Type   | Type ID | JSON Representation      | Example              |
|--------------|---------|--------------------------|----------------------|
| None         | 0       | (omitted)                | —                    |
| U64          | 1       | integer                  | `443`                |
| Bool         | 2       | boolean                  | `true`               |
| MacAddress   | 4       | string (colon-separated) | `"00:11:22:33:44:55"`|
| IPv4Address  | 5       | string (dotted decimal)  | `"192.168.1.1"`      |
| Bytes        | 6       | string (hex-encoded)     | `"deadbeef"`         |
| Str          | 7       | string                   | `"example.com"`      |

## File Structure

```
[\n
  <packet>,\n\n
  <packet>,\n\n
  ...
  <packet>\n
]\n
```

- The root element is always a JSON array
- Packets are separated by `,\n\n`
- Empty exports produce `[\n]\n` (valid empty array)
- All strings are JSON-escaped (including control characters, `"`, `\`)
- SIMD-accelerated string escaping (AVX2/SSE2 with scalar fallback)

## Builder Options

| Method               | Description                              | Default          |
|----------------------|------------------------------------------|------------------|
| `.ToFile(path)`        | Write to file with 4 MiB buffer          | required         |
| `.ToStream(stream)`    | Write to existing stream                 | required         |
| `.ToStdout()`          | Write to standard output                 | required         |
| `.WithUiName(name)`    | Display name shown in UI and logs        | `"JSON Exporter"` |
| `.WithDescription(d)`  | Optional description                     | `null`           |
| `.Format(format)`    | Output format                            | `Compact`        |
| `.FlushPerPacket(b)` | Flush after each packet                  | `false`          |
| `.WithTargetPacketCount(n)` | Auto-stop after N packets          | 0 (unlimited)    |
| `.WithCancellationToken(t)` | Cooperative cancellation          | `CancellationToken.None` |

## Thread Safety

Not thread-safe. `OnPacket()` and `OnFinish()` must be called sequentially from a single thread. Callers are responsible for synchronization if used from multiple threads. Statistics are valid to read after `OnFinish()` returns.
