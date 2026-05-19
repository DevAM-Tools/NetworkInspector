// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Headers;

/// <summary>
/// ICMPv4 echo request/reply header (8 bytes).
/// Layout per RFC 792: Type(1), Code(1), Checksum(2), Identifier(2), SequenceNumber(2).
/// </summary>
/// <remarks>Checksum is left at 0 and patched by <c>IcmpV4EchoLayer</c> <c>FixPhase.InnerChecksum</c>.</remarks>
[BinaryWritable]
internal readonly partial struct IcmpV4Header
{
    /// <summary>Size of the ICMP header in bytes.</summary>
    internal const int Size = 8;

    /// <summary>ICMP type (8 = Echo Request, 0 = Echo Reply).</summary>
    internal byte Type
    {
        get; init;
    }

    /// <summary>ICMP code (0 for echo).</summary>
    internal byte Code
    {
        get; init;
    }

    /// <summary>Checksum over ICMP header + data. Set to 0 for fixup.</summary>
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

    /// <summary>Creates an ICMP Echo Request header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IcmpV4Header EchoRequest(ushort identifier = 1, ushort sequenceNumber = 1) =>
        new()
        {
            Type = 8,
            Code = 0,
            Checksum = (ushort)0,
            Identifier = identifier,
            SequenceNumber = sequenceNumber
        };

    /// <summary>Creates an ICMP Echo Reply header.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static IcmpV4Header EchoReply(ushort identifier = 1, ushort sequenceNumber = 1) =>
        new()
        {
            Type = 0,
            Code = 0,
            Checksum = (ushort)0,
            Identifier = identifier,
            SequenceNumber = sequenceNumber
        };
}
