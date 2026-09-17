// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 3 (header_type = 3) — 16 bytes.
/// Layout (little-endian):
/// <code>
///   [0..4)  flags (u32; low nibble = timestamp resolution: 1 = 10 µs, 2 = 1 ns)
///   [4..6)  static size (u16)
///   [6..8)  object version (u16)
///   [8..16) object timestamp (u64)
/// </code>
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfLogObjectHeaderV3
{
    /// <summary>
    /// Flags field. Lower nibble (bits 0–3) defines timestamp resolution:
    /// 1 = 10 µs units, 2 = nanosecond units.
    /// </summary>
    public U32LE Flags
    {
        get; init;
    }

    /// <summary>Static size of the object (V3 specific).</summary>
    public U16LE StaticSize
    {
        get; init;
    }

    /// <summary>Version of the object structure.</summary>
    public U16LE ObjectVersion
    {
        get; init;
    }

    /// <summary>Raw timestamp. Resolution determined by lower 4 bits of <see cref="Flags"/>.</summary>
    public U64LE Timestamp
    {
        get; init;
    }
}
