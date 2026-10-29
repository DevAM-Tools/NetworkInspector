// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// Content-based detection of SOME/IP, registered on the heuristic tables of UDP and TCP. It is tried after no port parser consumed
/// a payload and answers whether the payload could start with a SOME/IP header.
/// <para>
/// The test is deliberately cheap and free of side effects, as the heuristic table requires: 16 bytes and a plausible header
/// (length of at least 8, protocol version 1, a defined base type). Whether a plausible payload is <em>accepted</em> is not decided
/// here: the table calls <c>Test</c> without any packet context, so the decision (the user's switch, a learned port, a decrypted
/// hand-off) is taken by <see cref="SomeIpProtocol"/> when it parses, where the packet is known and the decision can be recorded
/// and replayed. A payload that the protocol declines is simply not consumed, and the transport keeps showing it.
/// </para>
/// <para>
/// <b>Datagrams must hold their first message:</b> on UDP a message is never split across datagrams, so a datagram whose first
/// header declares more bytes than the datagram holds is not SOME/IP (a stream, by contrast, delivers the rest in the next
/// segment, and its framing checks the declared length itself). The extra rule removes most random matches of the 16-byte test on
/// unrelated datagrams. A SOME/IP datagram that a capture limit cut is still decoded on its port; only the guess on an unknown port
/// is stricter.
/// </para>
/// <para><b>Thread-safety:</b> immutable after construction; stateless.</para>
/// </summary>
/// <param name="protocolId">The SOME/IP protocol this parser identifies.</param>
/// <param name="requireWholeFirstMessage">Whether the declared length of the first message must fit into the payload (UDP).</param>
internal sealed class SomeIpHeuristicParser(ProtocolId protocolId, bool requireWholeFirstMessage = false) : IHeuristicParser
{
    /// <inheritdoc/>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <inheritdoc/>
    public string Name => "someip.heuristic";

    /// <inheritdoc/>
    public string UiName => "SOME/IP Heuristic";

    /// <inheritdoc/>
    public string? Description => "Detects SOME/IP by a plausible message header";

    /// <inheritdoc/>
    public bool Test(ReadOnlyMemory<byte> data)
    {
        // A header needs all 16 bytes; a shorter payload cannot be judged and is not claimed.
        if (data.Length < SomeIpHeader.Size)
        {
            return false;
        }

        SomeIpHeader header = SomeIpHeader.Parse(data.Span);
        if (!header.IsPlausible)
        {
            return false;
        }

        // The length counts from the client id, so the first message ends 8 bytes plus the length from the start. Formed in long: a
        // hostile 0xFFFFFFFF must not wrap into a small number that passes.
        return !requireWholeFirstMessage || SomeIpHeader.LengthPrefixSize + (long)header.Length <= data.Length;
    }
}

/// <summary>
/// Tells a DTLS record from a SOME/IP message on a port that carries both. Mixed fleets run cleartext and DTLS-protected SOME/IP on
/// the same ports; the two are told apart by their first bytes, and the choice is made only when the user switched
/// <c>someip.detect_dtls</c> on.
/// <para>
/// <b>DTLS record header (13 bytes):</b> content type (1), version (2), epoch (2), sequence number (6), length (2). A content type
/// from 20 to 63 is a record of the protocol (20 to 25 are defined, the rest is reserved), the version of a DTLS record is 0xFEFC to
/// 0xFEFF (DTLS 1.0 is 0xFEFF, 1.2 and the legacy field of 1.3 are 0xFEFD), and the length must fit into the datagram. A datagram may
/// carry several records (a handshake flight), so the first record has to fit and nothing is required of what follows.
/// </para>
/// <para><b>Why SOME/IP wins a tie:</b> a datagram that is a plausible SOME/IP message stays SOME/IP, because on a SOME/IP port a
/// false DTLS classification would hide a real message, while a false SOME/IP classification only shows a header with a diagnostic.</para>
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpDtlsProbe
{
    /// <summary>Size of a DTLS record header in bytes (format fact: 1 + 2 + 2 + 6 + 2).</summary>
    private const int _RecordHeaderSize = 13;

    /// <summary>First content type of the range a record may use (change_cipher_spec).</summary>
    private const byte _FirstContentType = 20;

    /// <summary>Last content type of the range (the reserved values up to 63 are accepted: the detection is generous, the DTLS parser strict).</summary>
    private const byte _LastContentType = 63;

    /// <summary>Whether the datagram starts with a plausible DTLS record.</summary>
    /// <param name="data">The datagram payload.</param>
    internal static bool LooksLikeDtls(ReadOnlySpan<byte> data)
    {
        if (data.Length < _RecordHeaderSize)
        {
            return false;
        }

        byte contentType = data[0];
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[11..]);
        return contentType is >= _FirstContentType and <= _LastContentType
            && version is >= 0xFEFC and <= 0xFEFF
            && _RecordHeaderSize + length <= data.Length;
    }

    /// <summary>Whether the datagram starts with a plausible SOME/IP message that fits into it.</summary>
    /// <param name="data">The datagram payload.</param>
    internal static bool LooksLikeSomeIp(ReadOnlySpan<byte> data)
    {
        if (data.Length < SomeIpHeader.Size)
        {
            return false;
        }

        SomeIpHeader header = SomeIpHeader.Parse(data);
        return header.IsPlausible && SomeIpHeader.LengthPrefixSize + (long)header.Length <= data.Length;
    }
}
