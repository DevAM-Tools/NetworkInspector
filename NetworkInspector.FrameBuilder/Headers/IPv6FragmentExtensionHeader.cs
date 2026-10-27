// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IPv6 Fragment extension header per RFC 8200 §4.5: NextHeader(1) +
/// Reserved(1) + FragmentOffset(13 bits) | Res(2 bits) | M(1 bit) +
/// Identification(4).  Fixed 8-byte layout.
/// </summary>
internal readonly struct IPv6FragmentExtensionHeader
{
    /// <summary>Size of this extension header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>Next-header field identifying the upper-layer protocol.</summary>
    internal byte NextHeader
    {
        get; init;
    }

    /// <summary>Reserved byte; always emitted as zero.</summary>
    internal byte Reserved
    {
        get; init;
    }

    /// <summary>
    /// FragmentOffset (13 bits high) + Res (2 bits) + M flag (low bit), packed
    /// into one big-endian 16-bit word.
    /// </summary>
    internal ushort FragmentOffsetAndFlags
    {
        get; init;
    }

    /// <summary>Identification field shared by all fragments of one datagram.</summary>
    internal uint Identification
    {
        get; init;
    }

    /// <summary>Writes the 8 header bytes in network order; <see langword="false"/> and 0 when the destination is shorter.</summary>
    /// <remarks>
    /// Replaces a generated writer: one length check, then fixed-offset stores on an exact-size slice, so the JIT drops the
    /// per-store bounds checks. Layers call it directly (no interface dispatch). Writes nothing when it fails.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWrite(Span<byte> destination, out int bytesWritten)
    {
        // One length check up front; a short destination is an expected failure, not an exception.
        if (destination.Length < Size)
        {
            bytesWritten = 0;
            return false;
        }

        // Exact-size slice: the fixed-offset stores below need no further bounds checks after JIT.
        Span<byte> d = destination[..Size];
        d[0] = NextHeader;                                                    // 0
        d[1] = Reserved;                                                      // 1
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], FragmentOffsetAndFlags); // 2..4
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], Identification);        // 4..8
        bytesWritten = Size;
        return true;
    }
}
