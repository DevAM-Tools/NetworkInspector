// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Generic IPv6 extension header with the common option-style layout
/// shared by Hop-by-Hop, Destination Options, and Routing per RFC 8200 §4:
/// NextHeader(1) + HdrExtLen(1, in 8-octet units excluding the first) + Data(6).
/// </summary>
/// <remarks>
/// This minimal 8-byte form encodes <c>HdrExtLen = 0</c> and pads the data
/// area with PadN options (per RFC 8200 §4.2).  Larger option blocks need the
/// variable-size <see cref="IPv6HopByHopLayer"/> constructor that takes option bytes.
/// </remarks>
internal readonly struct IPv6OptionsExtensionHeader
{
    /// <summary>Size of this minimal extension header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>Next-header field identifying the following protocol.</summary>
    internal byte NextHeader
    {
        get; init;
    }

    /// <summary>Extension header length in 8-octet units, NOT counting the first 8 octets.</summary>
    internal byte HdrExtLen
    {
        get; init;
    }

    /// <summary>Data byte 0 (PadN option type = 0x01 placeholder).</summary>
    internal byte Data0
    {
        get; init;
    }

    /// <summary>Data byte 1 (PadN option length = 4).</summary>
    internal byte Data1
    {
        get; init;
    }

    /// <summary>Data byte 2 (zero pad).</summary>
    internal byte Data2
    {
        get; init;
    }

    /// <summary>Data byte 3 (zero pad).</summary>
    internal byte Data3
    {
        get; init;
    }

    /// <summary>Data byte 4 (zero pad).</summary>
    internal byte Data4
    {
        get; init;
    }

    /// <summary>Data byte 5 (zero pad).</summary>
    internal byte Data5
    {
        get; init;
    }

    /// <summary>Writes the 8 header bytes; <see langword="false"/> and 0 when the destination is shorter.</summary>
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
        d[0] = NextHeader;  // 0
        d[1] = HdrExtLen;   // 1
        d[2] = Data0;       // 2
        d[3] = Data1;       // 3
        d[4] = Data2;       // 4
        d[5] = Data3;       // 5
        d[6] = Data4;       // 6
        d[7] = Data5;       // 7
        bytesWritten = Size;
        return true;
    }
}
