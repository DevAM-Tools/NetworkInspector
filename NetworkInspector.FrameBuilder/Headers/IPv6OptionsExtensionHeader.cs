// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// Generic IPv6 extension header with the common option-style layout
/// shared by Hop-by-Hop, Destination Options, and Routing per RFC 8200 §4:
/// NextHeader(1) + HdrExtLen(1, in 8-octet units excluding the first) + Data(6).
/// </summary>
/// <remarks>
/// This minimal 8-byte form encodes <c>HdrExtLen = 0</c> and pads the data
/// area with PadN options (per RFC 8200 §4.2).  Larger extension headers
/// require a different layer that emits TLV options; out of scope for the
/// initial M5a layer set.
/// </remarks>
[BinaryWritable]
internal readonly partial struct IPv6OptionsExtensionHeader
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
}
