// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// IPv6 Fragment extension header per RFC 8200 §4.5: NextHeader(1) +
/// Reserved(1) + FragmentOffset(13 bits) | Res(2 bits) | M(1 bit) +
/// Identification(4).  Fixed 8-byte layout.
/// </summary>
[BinaryWritable]
internal readonly partial struct IPv6FragmentExtensionHeader
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
    internal U16BE FragmentOffsetAndFlags
    {
        get; init;
    }

    /// <summary>Identification field shared by all fragments of one datagram.</summary>
    internal U32BE Identification
    {
        get; init;
    }
}
