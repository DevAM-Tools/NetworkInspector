// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// Bitmap for tracking field presence per block. A dense bit array (rather than a <see cref="HashSet{T}"/>) gives
/// cache-friendly field ID lookups: one bit per field ID, no hashing, no per-entry allocation, and a block-to-block
/// <see cref="Clear"/> that only touches the bytes that were set.
/// <para>
/// The bitmap layout (bit <c>id % 8</c> of byte <c>id / 8</c>, least-significant bit first) is also the layout of the
/// PBF trailer's global field bitmap, which is why <see cref="MergeInto"/> can combine per-block bitmaps with a
/// plain byte-wise OR.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe; owned by one block builder and so by one thread at a time. The caller's thread marks
/// fields while it fills the block; in <see cref="ExportWriteMode.Background"/> the writer thread then reads it through
/// <see cref="AsBytes"/> or <see cref="MergeInto"/> and clears it. The hand-off of <see cref="BatchWriterPipeline{TBatch}"/> is the
/// memory barrier between the two (see <see cref="IExportBatch"/>); no member may keep state that belongs to one specific thread.
/// </para>
/// </summary>
internal sealed class FieldPresence
{
    #region Presence bitmap

    /// <summary>The bitmap: bit <c>id % 8</c> of byte <c>id / 8</c> is set when the field id was marked.</summary>
    private readonly byte[] _Bitmap;

    /// <summary>
    /// Number of leading bytes of <see cref="_Bitmap"/> that may contain a set bit. Lets <see cref="Clear"/> and
    /// <see cref="AsBytes"/> work on a short prefix instead of the whole array (4 KiB for the 32 768 IDs the PBF exporter tracks)
    /// when only low field IDs occur.
    /// </summary>
    private int _UsedBytes;

    /// <summary>
    /// Creates a new field presence bitmap sized for the given number of field IDs.
    /// </summary>
    /// <param name="maxFieldId">
    /// Exclusive upper bound of the tracked field IDs, rounded up to a multiple of 8: IDs <c>0</c> to
    /// <c>maxFieldId - 1</c> are tracked. From <c>0</c> (tracks nothing) to <c>int.MaxValue - 7</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxFieldId"/> is negative or so large that the size rounded up to whole bytes overflows.
    /// </exception>
    internal FieldPresence(int maxFieldId)
    {
        // Limit: the bitmap is sized once from a trusted constant of the exporter (32 768 ids), never from capture data; a negative
        // or overflowing size is a bug in the caller. Constant check, not configurable. Posture: secure by default. A wrapped
        // size would otherwise surface as an unrelated OverflowException or a negative array length.
        ArgumentOutOfRangeException.ThrowIfNegative(maxFieldId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFieldId, int.MaxValue - 7);
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
    /// <param name="fieldIdValue">The field ID to mark.</param>
    /// <returns><see langword="true"/> when the field was not marked before (or is outside the tracked range).</returns>
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

        // Bounds check — if fieldIdValue exceeds our bitmap, treat as always new. The bitmap is deliberately not
        // grown: its size bounds the memory used per block and the size of the trailer bitmap (a field ID near
        // int.MaxValue would otherwise ask for a 256 MiB array). The cost is that such IDs are absent from the
        // merged trailer bitmap.
        if (byteIndex >= _Bitmap.Length)
        {
            return true;
        }

        byte mask = (byte)(1 << bitIndex);
        bool wasSet = (_Bitmap[byteIndex] & mask) != 0;
        _Bitmap[byteIndex] |= mask;

        // Keep the used-prefix length current so Clear and AsBytes never miss a set bit.
        if (byteIndex >= _UsedBytes)
        {
            _UsedBytes = byteIndex + 1;
        }
        return !wasSet;
    }

    /// <summary>
    /// Clears the bit of a field that <see cref="Mark"/> reported as new, so the field counts as unseen again. Used to undo
    /// the marks of a packet that was not added to the block. Ids outside the tracked range have no bit and are ignored.
    /// The used-prefix length is left as it is (a trailing zero byte is harmless).
    /// </summary>
    /// <param name="fieldIdValue">The field ID to unmark; a negative value is ignored.</param>
    internal void Unmark(int fieldIdValue)
    {
        int byteIndex = fieldIdValue >> 3;
        if (fieldIdValue < 0 || byteIndex >= _Bitmap.Length)
        {
            return;
        }

        _Bitmap[byteIndex] &= (byte)~(1 << (fieldIdValue & 0x07));
    }

    /// <summary>Clears all set bits (only zeros the used portion for speed).</summary>
    internal void Clear()
    {
        _Bitmap.AsSpan(0, _UsedBytes).Clear();
        _UsedBytes = 0;
    }

    /// <summary>
    /// Returns the used portion of the bitmap (trailing all-zero bytes omitted) as a read-only span. The span aliases
    /// the internal array and is invalidated by <see cref="Mark"/> and <see cref="Clear"/>.
    /// </summary>
    internal ReadOnlySpan<byte> AsBytes() => _Bitmap.AsSpan(0, _UsedBytes);

    /// <summary>
    /// OR-merges this bitmap into the target span. Fields present in this
    /// bitmap will be marked present in the target as well.
    /// </summary>
    /// <param name="target">The accumulating bitmap; bytes beyond its length are not merged.</param>
    internal void MergeInto(Span<byte> target)
    {
        // Only the used prefix is visited, and a shorter target silently truncates the merge (it is sized by the
        // exporter to the same field ID limit, so nothing is lost in practice).
        ReadOnlySpan<byte> source = AsBytes();
        int length = Math.Min(source.Length, target.Length);
        for (int i = 0; i < length; i++)
        {
            target[i] |= source[i];
        }
    }

    #endregion
}
