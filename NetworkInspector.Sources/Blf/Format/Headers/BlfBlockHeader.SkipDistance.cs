// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format.Headers;

internal readonly partial struct BlfBlockHeader
{
    #region Public API

    /// <summary>
    /// Parses a file-level block header and computes how far the reader advances past the object:
    /// <c>max(max(16, ObjectLength), HeaderSize)</c>. The single rule set of the scanner's outer loop and of the
    /// random-access re-read of raw objects, so both accept exactly the same objects.
    /// </summary>
    /// <param name="data">Bytes starting at the <c>LOBJ</c> magic; at least 16 are needed.</param>
    /// <param name="header">The parsed header when the method returns <see langword="true"/>.</param>
    /// <param name="skipDistance">Bytes from the object start to the next object (without alignment padding).</param>
    /// <returns>
    /// False when the header cannot be read, the signature is not <c>LOBJ</c>, the header claims fewer than 16 bytes,
    /// <c>object_length</c> is 0 (a reader would loop at one offset), or the skip exceeds
    /// <see cref="BlfConstants.MaxBlockReadSize"/> (untrusted lengths must not force an unbounded read).
    /// </returns>
    internal static bool TryGetSkipDistance(ReadOnlySpan<byte> data, out BlfBlockHeader header, out int skipDistance)
    {
        skipDistance = 0;
        if (!TryParse(data, out header, out _)
            || header.Signature.Value != BlfConstants.ObjectMagic
            || header.HeaderSize.Value < BlfConstants.BlockHeaderSize
            || header.ObjectLength.Value == 0)
        {
            return false;
        }

        long distance = Math.Max(Math.Max((long)header.HeaderSize.Value, BlfConstants.BlockHeaderSize), header.ObjectLength.Value);
        if (distance > BlfConstants.MaxBlockReadSize)
        {
            return false;
        }

        skipDistance = (int)distance;
        return true;
    }

    #endregion
}
