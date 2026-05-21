// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 3 (header_type = 3) — 16 bytes.
/// Matches Vector/Wireshark <c>blf_logobjectheader3_t</c> from <c>wiretap/blf.h</c>:
/// <code>
/// typedef struct blf_logobjectheader3 {
///     uint32_t flags;            // [0..4]
///     uint16_t static_size;      // [4..6]
///     uint16_t object_version;   // [6..8]
///     uint64_t object_timestamp; // [8..16]
/// } blf_logobjectheader3_t;
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
