// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Defines how field values are stored in a <see cref="ValueCacheSeries"/>.
/// The user selects the mode explicitly — no auto-detection.
/// </summary>
public enum ValueCacheStorageMode : byte
{
    #region Enum Values

    /// <summary>
    /// Lossless, type-specific storage using the native array type
    /// for the field's <see cref="FieldType"/>. Supports all cacheable FieldTypes.
    /// </summary>
    Native = 0,

    /// <summary>
    /// Lossy single-precision float storage. Eligible only for
    /// <see cref="FieldType.U64"/>, <see cref="FieldType.I64"/>,
    /// <see cref="FieldType.F64"/>, <see cref="FieldType.Timestamp"/>.
    /// <para>
    /// For Timestamp: stores a <see langword="double"/> base timestamp (first entry, seconds
    /// since epoch) plus <see langword="float"/>[] deltas (seconds relative to that base).
    /// Reconstruct via base + delta.
    /// </para>
    /// </summary>
    CompactFloat = 1,

    // ── Signed compact modes (for I64 only) ──────────────────

    /// <summary>
    /// Compact 8-bit signed storage (<see langword="sbyte"/>[]). Values clamped to −128..127.
    /// Eligible only for <see cref="FieldType.I64"/>.
    /// </summary>
    CompactInt8 = 2,

    /// <summary>
    /// Compact 16-bit signed storage (<see langword="short"/>[]). Values clamped to −32768..32767.
    /// Eligible only for <see cref="FieldType.I64"/>.
    /// </summary>
    CompactInt16 = 3,

    /// <summary>
    /// Compact 32-bit signed storage (<see langword="int"/>[]). Values clamped to int.MinValue..int.MaxValue.
    /// Eligible only for <see cref="FieldType.I64"/>.
    /// </summary>
    CompactInt32 = 4,

    // ── Unsigned compact modes (for U64 only) ────────────────

    /// <summary>
    /// Compact 8-bit unsigned storage (<see langword="byte"/>[]). Values clamped to 0..255.
    /// Eligible only for <see cref="FieldType.U64"/>.
    /// </summary>
    CompactUInt8 = 5,

    /// <summary>
    /// Compact 16-bit unsigned storage (<see langword="ushort"/>[]). Values clamped to 0..65535.
    /// Eligible only for <see cref="FieldType.U64"/>.
    /// </summary>
    CompactUInt16 = 6,

    /// <summary>
    /// Compact 32-bit unsigned storage (<see langword="uint"/>[]). Values clamped to 0..2^32−1.
    /// Eligible only for <see cref="FieldType.U64"/>.
    /// </summary>
    CompactUInt32 = 7,

    #endregion
}
