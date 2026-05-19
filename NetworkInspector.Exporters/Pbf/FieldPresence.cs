// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Dynamic bitmap for tracking field presence per block.
/// Replaces <see cref="HashSet{T}"/> with a dense bit array for
/// cache-friendly field ID lookups.
/// </summary>
internal sealed class FieldPresence
{
    private readonly byte[] _Bitmap;
    private int _UsedBytes; // tracks highest byte index with a set bit

    /// <summary>
    /// Creates a new field presence bitmap sized for the given maximum field ID.
    /// </summary>
    /// <param name="maxFieldId">The highest field ID value expected.</param>
    internal FieldPresence(int maxFieldId)
    {
        _Bitmap = new byte[(maxFieldId + 7) / 8];
    }

    /// <summary>
    /// Marks a field as present. Returns <c>true</c> if the field was previously unset
    /// (first occurrence in this block).
    /// <para>
    /// <b>Out-of-range behaviour:</b> If <paramref name="fieldIdValue"/> is
    /// negative, an <see cref="ArgumentOutOfRangeException"/> is thrown; negative IDs
    /// indicate a protocol bug and must not be silently ignored.
    /// If <paramref name="fieldIdValue"/> is non-negative but exceeds the <c>maxFieldId</c>
    /// passed to the constructor, the field is treated as always-new (<c>true</c> is
    /// returned without modifying the bitmap). This is a deliberate degradation: for
    /// fields with IDs outside the pre-configured range, metadata deduplication is
    /// disabled but correctness of the output stream is preserved. Callers that require
    /// deduplication for high-ID fields must pass a higher <c>maxFieldId</c> when
    /// constructing <see cref="FieldPresence"/>.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldIdValue"/> is negative.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Mark(int fieldIdValue)
    {
        // Negative field IDs are a protocol-implementation bug; fail fast.
        if (fieldIdValue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldIdValue),
                fieldIdValue, "Field ID must be non-negative.");
        }

        int byteIndex = fieldIdValue >> 3;        // / 8
        int bitIndex = fieldIdValue & 0x07;       // % 8

        // Bounds check — if fieldIdValue exceeds our bitmap, treat as always new
        if (byteIndex >= _Bitmap.Length)
        {
            return true;
        }

        byte mask = (byte)(1 << bitIndex);
        bool wasSet = (_Bitmap[byteIndex] & mask) != 0;
        _Bitmap[byteIndex] |= mask;
        if (byteIndex >= _UsedBytes)
        {
            _UsedBytes = byteIndex + 1;
        }
        return !wasSet;
    }

    /// <summary>Clears all set bits (only zeros the used portion for speed).</summary>
    internal void Clear()
    {
        _Bitmap.AsSpan(0, _UsedBytes).Clear();
        _UsedBytes = 0;
    }

    /// <summary>Returns the used portion of the bitmap as a read-only span.</summary>
    internal ReadOnlySpan<byte> AsBytes() => _Bitmap.AsSpan(0, _UsedBytes);

    /// <summary>
    /// OR-merges this bitmap into the target span. Fields present in this
    /// bitmap will be marked present in the target as well.
    /// </summary>
    internal void MergeInto(Span<byte> target)
    {
        ReadOnlySpan<byte> source = AsBytes();
        int length = Math.Min(source.Length, target.Length);
        for (int i = 0; i < length; i++)
        {
            target[i] |= source[i];
        }
    }
}
