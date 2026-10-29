// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// The IPv4 or IPv6 header that encloses an ICMP or ICMPv6 message, read from the bytes the IP container stores as its
/// value. It answers two questions that the lazy IP fields cannot answer without being materialized: was the message cut
/// by the capture length, and what is the hop limit.
/// <para>
/// <b>Why the container bytes and not fields:</b> the IP parsers keep total length, payload length, hop limit and time to
/// live in lazy fields (only addresses and the protocol number are eager), and a lazy parse must not materialize a
/// sibling's fields. The container value, in contrast, is always there: the IPv4 parser stores the header bytes, the IPv6
/// parser the fixed header plus the extension headers it walked. Reading two or three bytes from that slice costs a
/// bounds check. The lookup is for the lazy populator, where the field tree exists. The eager parse must not use it: the
/// mode that builds no field tree has no container, and the index has to be the same in both modes, so the eager hop limit
/// check reads the per-thread IPv6 cache instead (<c>IPv6Protocol.TryGetCachedHopLimit</c>).
/// </para>
/// <para>
/// Thread safety: an immutable value; the lookup methods only read the field tree of the packet they are given.
/// </para>
/// </summary>
internal readonly struct EnclosingIpHeader
{
    /// <summary>Size of the fixed IPv6 header; the stored slice is at least this long and longer by the extension headers.</summary>
    private const int _Ipv6FixedHeaderSize = 40;

    /// <summary>Smallest IPv4 header; the stored slice is at least this long.</summary>
    private const int _Ipv4MinHeaderSize = 20;

    private EnclosingIpHeader(bool isIPv6, ReadOnlyMemory<byte> header)
    {
        IsIPv6 = isIPv6;
        Header = header;
    }

    /// <summary>Whether the header is IPv6 (otherwise IPv4).</summary>
    internal bool IsIPv6
    {
        get;
    }

    /// <summary>The stored header bytes: the IPv4 header, or the IPv6 fixed header with its walked extension headers.</summary>
    internal ReadOnlyMemory<byte> Header
    {
        get;
    }

    /// <summary>
    /// The hop limit of an IPv6 header. Meaningful only when <see cref="IsIPv6"/> is true; the fixed header is always
    /// present, so the read is in range.
    /// </summary>
    internal byte HopLimit => Header.Span[7];

    /// <summary>
    /// Tells whether the message was cut by the capture length: the enclosing header declares more payload than the
    /// message that was handed to the parser holds.
    /// </summary>
    /// <param name="messageLength">Length of the ICMP or ICMPv6 message the parser received.</param>
    /// <returns>
    /// True when the declared payload is longer than the message. False when it fits, and also when the header declares
    /// nothing usable (an IPv6 payload length of 0 means a jumbo payload or no payload; an IPv4 total length below the
    /// header length is malformed): an unknown length is no evidence of a cut.
    /// </returns>
    /// <remarks>
    /// A datagram that was reassembled from fragments is longer than the fragment header declares, so it never reads as
    /// cut. Ethernet padding makes the buffer longer than the declared payload, which reads as complete as well.
    /// </remarks>
    internal bool IsTruncated(int messageLength)
    {
        ReadOnlySpan<byte> header = Header.Span;
        if (IsIPv6)
        {
            // Payload length (bytes 4-5) counts the extension headers; the stored slice holds them, so what the upper layer
            // should have is the payload length minus the extension bytes.
            int payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
            int upperLayerLength = payloadLength - (header.Length - _Ipv6FixedHeaderSize);
            return payloadLength != 0
                && upperLayerLength > messageLength;
        }

        // Total length (bytes 2-3) counts the header; the stored slice is exactly the header.
        int totalLength = BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);
        return totalLength > header.Length
            && totalLength - header.Length > messageLength;
    }

    /// <summary>
    /// Finds the innermost IP header before <paramref name="start"/> by walking previous siblings. Used by the lazy
    /// populator, where the ICMP container exists; the same walk the checksum lookup uses for the addresses.
    /// </summary>
    /// <param name="start">The ICMP container.</param>
    /// <param name="ipContainerFieldId">Id of the IPv4 container; invalid when IPv4 is not part of the stack.</param>
    /// <param name="ipv6ContainerFieldId">Id of the IPv6 container; invalid when IPv6 is not part of the stack.</param>
    /// <param name="header">The header when the method returns true.</param>
    internal static bool TryFindPrevious(
        in MutField start, FieldId ipContainerFieldId, FieldId ipv6ContainerFieldId, out EnclosingIpHeader header)
    {
        header = default;
        MutField current = start;
        while (current.TryGetPrev(out current))
        {
            // The first IP container met while walking backwards is the innermost one.
            if (_TryRead(in current, ipContainerFieldId, ipv6ContainerFieldId, out header))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads the header bytes of <paramref name="field"/> when it is an IPv4 or IPv6 container with a complete header.</summary>
    private static bool _TryRead(
        in MutField field, FieldId ipContainerFieldId, FieldId ipv6ContainerFieldId, out EnclosingIpHeader header)
    {
        header = default;
        bool isIPv4 = ipContainerFieldId.IsValid && field.FieldId == ipContainerFieldId;
        bool isIPv6 = ipv6ContainerFieldId.IsValid && field.FieldId == ipv6ContainerFieldId;
        if (!isIPv4 && !isIPv6)
        {
            return false;
        }

        // The IP parsers validated the header before they appended the container; the length test only guards against a
        // foreign container value, so a short slice is "not found" instead of an out-of-range read.
        int minimumLength = isIPv6
            ? _Ipv6FixedHeaderSize
            : _Ipv4MinHeaderSize;
        if (!field.Value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> bytes)
            || bytes.Length < minimumLength)
        {
            return false;
        }

        header = new EnclosingIpHeader(isIPv6, bytes);
        return true;
    }
}
