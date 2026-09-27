// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 1 (header_type = 1) — 16 bytes.
/// Layout (little-endian):
/// <code>
///   [0..4)  flags (u32; the whole word is 1 for 10 µs or 2 for 1 ns)
///   [4..6)  client index (u16)
///   [6..8)  object version (u16)
///   [8..16) object timestamp (u64; units from flags)
/// </code>
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfLogObjectHeaderV1
{
    /// <summary>
    /// Flags word. The whole value must be 1 (10 µs units) or 2 (nanoseconds).
    /// Any other word, including a value whose low nibble is 1 or 2, yields timestamp 0.
    /// </summary>
    public U32LE Flags
    {
        get; init;
    }

    /// <summary>Client/channel index.</summary>
    public U16LE ClientIndex
    {
        get; init;
    }

    /// <summary>Version of the object structure.</summary>
    public U16LE ObjectVersion
    {
        get; init;
    }

    /// <summary>Raw timestamp. Units come from the whole <see cref="Flags"/> word (1 = 10 µs, 2 = 1 ns).</summary>
    public U64LE Timestamp
    {
        get; init;
    }
}
