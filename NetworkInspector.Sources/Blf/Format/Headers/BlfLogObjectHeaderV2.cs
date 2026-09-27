// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

/// <summary>
/// BLF Log Object Header — Version 2 (header_type = 2) — 24 bytes.
/// Layout (little-endian):
/// <code>
///   [0..4)   flags (u32; the whole word is 1 for 10 µs or 2 for 1 ns)
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
    /// Flags word. The whole value must be 1 (10 µs units) or 2 (nanoseconds).
    /// Any other word, including a value whose low nibble is 1 or 2, yields timestamp 0.
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

    /// <summary>Raw timestamp. Units come from the whole <see cref="Flags"/> word (1 = 10 µs, 2 = 1 ns).</summary>
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
