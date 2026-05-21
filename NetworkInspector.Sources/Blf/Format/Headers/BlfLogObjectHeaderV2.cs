// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 2 (header_type = 2) — 24 bytes.
/// Matches Vector/Wireshark <c>blf_logobjectheader2_t</c> from <c>wiretap/blf.h</c>:
/// <code>
/// typedef struct blf_logobjectheader2 {
///     uint32_t flags;              // [0..4]
///     uint8_t  timestamp_status;   // [4]
///     uint8_t  res1;               // [5]
///     uint16_t object_version;     // [6..8]
///     uint64_t object_timestamp;   // [8..16]
///     uint64_t original_timestamp; // [16..24]
/// } blf_logobjectheader2_t;
/// </code>
/// </summary>
[BinaryParsable]
internal readonly partial struct BlfLogObjectHeaderV2
{
    /// <summary>
    /// Flags field. Lower nibble (bits 0–3) defines timestamp resolution:
    /// 1 = 10 µs units, 2 = nanosecond units.
    /// </summary>
    public U32LE Flags
    {
        get; init;
    }

    /// <summary>
    /// Combined <c>uint8 timestamp_status</c> (low byte) and <c>uint8 res1</c> (high byte).
    /// Vector spec lists these as two adjacent bytes; we read them as one little-endian
    /// 16-bit field because ZeroAlloc has no <c>U8</c> wrapper. The low byte holds
    /// the BLF_TS_STATUS_* flags; the high byte is reserved and must be zero.
    /// </summary>
    public U16LE TimestampStatusAndReserved
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

    /// <summary>Original/unmodified timestamp (before any offset corrections).</summary>
    public U64LE OriginalTimestamp
    {
        get; init;
    }
}
