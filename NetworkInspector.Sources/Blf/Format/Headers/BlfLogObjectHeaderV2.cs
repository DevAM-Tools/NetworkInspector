// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 2 (header_type = 2) — 24 bytes.
/// Layout (little-endian):
/// <code>
///   [0..4)   flags (u32; low nibble = timestamp resolution: 1 = 10 µs, 2 = 1 ns)
///   [4]      timestamp status (u8)
///   [5]      reserved (u8)
///   [6..8)   object version (u16)
///   [8..16)  object timestamp (u64)
///   [16..24) original timestamp (u64)
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
    /// Combined timestamp-status (low byte) and reserved (high byte).
    /// Vector lists these as two adjacent bytes; we read them as one little-endian
    /// 16-bit field because ZeroAlloc has no <c>U8</c> wrapper. The low byte holds
    /// timestamp-status flags (original timestamp valid, software timestamp,
    /// protocol-specific); the high byte is reserved and must be zero.
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
