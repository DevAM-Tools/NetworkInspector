// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// ICMPv6 echo request/reply header (8 bytes).
/// Layout per RFC 4443: Type(1), Code(1), Checksum(2), Identifier(2), SequenceNumber(2).
/// </summary>
/// <remarks>Checksum includes IPv6 pseudo-header; left at 0 for the layer's
/// <see cref="FixPhase.InnerChecksum"/>
/// post-fix phase to patch.</remarks>
[BinaryWritable]
internal readonly partial struct IcmpV6Header
{
    /// <summary>Size of the ICMPv6 header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>ICMPv6 type (128 = Echo Request, 129 = Echo Reply).</summary>
    internal byte Type
    {
        get; init;
    }

    /// <summary>ICMPv6 code (0 for echo).</summary>
    internal byte Code
    {
        get; init;
    }

    /// <summary>Checksum (includes IPv6 pseudo-header). Set to 0 for fixup.</summary>
    internal U16BE Checksum
    {
        get; init;
    }

    /// <summary>Echo identifier.</summary>
    internal U16BE Identifier
    {
        get; init;
    }

    /// <summary>Echo sequence number.</summary>
    internal U16BE SequenceNumber
    {
        get; init;
    }

    /// <summary>Creates an ICMPv6 Echo Request header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IcmpV6Header EchoRequest(ushort identifier = 1, ushort sequenceNumber = 1) =>
        new()
        {
            Type = 128,
            Code = 0,
            Checksum = (ushort)0,
            Identifier = identifier,
            SequenceNumber = sequenceNumber
        };

    /// <summary>Creates an ICMPv6 Echo Reply header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IcmpV6Header EchoReply(ushort identifier = 1, ushort sequenceNumber = 1) =>
        new()
        {
            Type = 129,
            Code = 0,
            Checksum = (ushort)0,
            Identifier = identifier,
            SequenceNumber = sequenceNumber
        };
}
