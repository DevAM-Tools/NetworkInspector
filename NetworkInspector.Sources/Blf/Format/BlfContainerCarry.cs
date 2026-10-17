// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>How the unread tail of a decompressed container is treated.</summary>
internal enum BlfContainerTailKind
{
    /// <summary>Nothing is left.</summary>
    Empty,

    /// <summary>Only alignment zeros: discarded silently.</summary>
    Padding,

    /// <summary>Zeros, then a <c>LOBJ</c> magic or a prefix of it, with a plausible length: carried into the next blob.</summary>
    PartialObject,

    /// <summary>Anything else: discarded and counted as a truncated object.</summary>
    Garbage,
}

/// <summary>
/// Container-tail classification and carry stitching shared by <see cref="BlfIncrementalScanner"/> and
/// <see cref="BlfStreamSource"/>. A defective writer may split one inner object across two containers; the tail of
/// the first container is carried and prepended to the next decompressed blob. Both readers use these rules, so they
/// accept and reject the same files.
/// </summary>
/// <remarks>Stateless; all members are thread-safe.</remarks>
internal static class BlfContainerCarry
{
    #region Public API

    /// <summary>Classifies the bytes after the last complete object of a container.</summary>
    internal static BlfContainerTailKind ClassifyTail(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty)
        {
            return BlfContainerTailKind.Empty;
        }

        // Vectorised: index of the first non-zero byte; -1 means the tail is all padding.
        int firstData = tail.IndexOfAnyExcept((byte)0);
        if (firstData < 0)
        {
            return BlfContainerTailKind.Padding;
        }

        ReadOnlySpan<byte> candidate = tail[firstData..];
        int n = Math.Min(candidate.Length, BlfConstants.ObjectMagicBytes.Length);
        if (!candidate[..n].SequenceEqual(BlfConstants.ObjectMagicBytes[..n]) || _DeclaredObjectLengthExceedsCap(candidate))
        {
            return BlfContainerTailKind.Garbage;
        }

        return BlfContainerTailKind.PartialObject;
    }

    /// <summary>True when every byte is zero (alignment padding). Vectorised.</summary>
    internal static bool IsAllZeros(ReadOnlySpan<byte> data) => !data.ContainsAnyExcept((byte)0);

    /// <summary>
    /// Prepends <paramref name="carry"/> to <paramref name="decoded"/>. Returns false (and leaves
    /// <paramref name="stitched"/> = <paramref name="decoded"/>) when the stitched size would exceed
    /// <paramref name="maxUncompressedSize"/> (0 = no limit) or the array limit; the caller then drops the carry.
    /// </summary>
    internal static bool TryStitch(byte[] carry, byte[] decoded, long maxUncompressedSize, out byte[] stitched)
    {
        long total = (long)carry.Length + decoded.Length;
        if (total > Array.MaxLength || (maxUncompressedSize > 0 && total > maxUncompressedSize))
        {
            stitched = decoded;
            return false;
        }

        // Both parts are copied over the whole array, so it needs no zeroing pass.
        stitched = GC.AllocateUninitializedArray<byte>((int)total);
        carry.CopyTo(stitched, 0);
        decoded.CopyTo(stitched, carry.Length);
        return true;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// True when <paramref name="candidate"/> (starting at the magic) is a parseable <c>LOBJ</c> header whose skip
    /// distance exceeds <see cref="BlfConstants.MaxBlockReadSize"/>. Those tails are corrupt, not split objects.
    /// </summary>
    private static bool _DeclaredObjectLengthExceedsCap(ReadOnlySpan<byte> candidate)
    {
        if (candidate.Length < BlfConstants.BlockHeaderSize)
        {
            return false;
        }

        if (!BlfBlockHeader.TryParse(candidate, out BlfBlockHeader header, out _)
            || header.Signature.Value != BlfConstants.ObjectMagic)
        {
            return false;
        }

        long skip = Math.Max(
            Math.Max((long)BlfConstants.BlockHeaderSize, header.ObjectLength.Value),
            header.HeaderSize.Value);
        return skip > BlfConstants.MaxBlockReadSize;
    }

    #endregion
}
