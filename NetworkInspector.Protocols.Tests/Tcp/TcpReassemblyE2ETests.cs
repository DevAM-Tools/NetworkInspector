// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// End-to-end integration tests for TCP stream reassembly through the real protocol stack.
/// Verifies that the TcpReassemblyEngine correctly buffers TCP segments and dispatches
/// the complete PDU to the registered sub-protocol (DNS in these tests).
///
/// DNS-over-TCP uses a 2-byte big-endian length prefix before each DNS message (RFC 1035 §4.2.2).
/// These tests deliberately split DNS messages across two TCP segments to exercise the
/// reassembly engine's buffering logic.
/// </summary>
internal sealed class TcpReassemblyE2ETests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2
    private const ushort _ClientPort = 52000;
    private const ushort _ServerPort = 53;   // DNS-over-TCP port

    #endregion

    #region DNS message builders

    /// <summary>
    /// Builds a minimal DNS/TCP PDU: 2-byte length prefix + DNS header + question.
    /// DNS query for "a.b." of type A, class IN, with the given transaction ID.
    /// Returns the full 25-byte buffer: [len_hi, len_lo, dns_12_bytes, qname_5_bytes, qtype_2_bytes, qclass_2_bytes].
    /// </summary>
    private static byte[] _BuildDnsTcpPdu(ushort txId = 0x1234)
    {
        // DNS message is 12 (header) + 5 (QNAME "a.b.") + 2 (QTYPE) + 2 (QCLASS) = 21 bytes
        // TCP length prefix = 0x00 0x15
        byte[] pdu = new byte[2 + 21];
        Span<byte> s = pdu;

        // TCP length prefix (big-endian, value = 21)
        BinaryPrimitives.WriteUInt16BigEndian(s, 21);

        // DNS header
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], txId);    // Transaction ID
        s[4] = 0x01;
        s[5] = 0x00;                               // Flags: standard query, RD=1
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 1);       // QDCOUNT = 1
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], 0);       // ANCOUNT = 0
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], 0);      // NSCOUNT = 0
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], 0);      // ARCOUNT = 0

        // QNAME: \x01a\x01b\x00  ("a.b.")
        s[14] = 1;
        s[15] = (byte)'a';
        s[16] = 1;
        s[17] = (byte)'b';
        s[18] = 0;                          // root label (null terminator)

        // QTYPE = 1 (A), QCLASS = 1 (IN)
        BinaryPrimitives.WriteUInt16BigEndian(s[19..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(s[21..], 1);

        return pdu;
    }

    #endregion

    #region TCP frame helpers

    private static byte[] _BuildTcpFrame(
        IPv4Address srcIp, IPv4Address dstIp,
        ushort srcPort, ushort dstPort,
        uint seqNum, uint ackNum,
        byte flags,
        ReadOnlySpan<byte> payload = default)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ipLayer = new(srcIp, dstIp);
        TcpLayer tcpLayer = new(srcPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags);
        return FrameStack.Start(eth).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _ClientFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
        => _BuildTcpFrame(_ClientIp, _ServerIp, _ClientPort, _ServerPort, seq, ack, flags, payload);

    private static byte[] _ServerFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
        => _BuildTcpFrame(_ServerIp, _ClientIp, _ServerPort, _ClientPort, seq, ack, flags, payload);

    #endregion

    #region Single-segment reassembly

    [Test]
    public async Task Reassembly_DnsOverTcp_SingleSegment_DnsIdPresent()
    {
        // Single TCP segment carries the complete DNS PDU.
        // Reassembly should extract the full PDU and dispatch to DNS.
        // DNS field dns.id must be present after reassembly.
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            uint cIsn = 1000, sIsn = 2000;

            // 3-way handshake
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
            ProtocolTestHelper.ParseFrame(stack, _ServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

            // Single DNS PDU in one TCP segment
            byte[] pdu = _BuildDnsTcpPdu(txId: 0x1234);
            Packet dataPacket = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, pdu),
                3, Timestamp.FromMillis(3));

            await ProtocolTestHelper.AssertU64Field(stack, dataPacket, "dns.id", 0x1234).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Reassembly_DnsOverTcp_SingleSegment_DnsFlagsPresent()
    {
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            uint cIsn = 3000, sIsn = 4000;

            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
            ProtocolTestHelper.ParseFrame(stack, _ServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

            byte[] pdu = _BuildDnsTcpPdu(txId: 0x5678);
            Packet dataPacket = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, pdu),
                3, Timestamp.FromMillis(3));

            // Flags = 0x0100 (standard query, RD=1)
            await ProtocolTestHelper.AssertU64Field(stack, dataPacket, "dns.flags", 0x0100).ConfigureAwait(false);
        }
    }

    #endregion

    #region Split-segment reassembly

    [Test]
    public async Task Reassembly_DnsOverTcp_TwoSegments_FirstSegmentNoFields()
    {
        // Send only the first 8 bytes (length prefix + part of DNS header).
        // DNS must NOT be present after the first segment (PDU not complete).
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            uint cIsn = 5000, sIsn = 6000;

            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
            ProtocolTestHelper.ParseFrame(stack, _ServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

            // First 8 bytes of the 23-byte PDU
            byte[] fullPdu = _BuildDnsTcpPdu(txId: 0xABCD);
            ReadOnlySpan<byte> seg1 = fullPdu.AsSpan(0, 8);

            Packet firstPacket = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, seg1),
                3, Timestamp.FromMillis(3));

            // DNS must NOT be resolved yet
            FieldId? dnsId = stack.GetFieldId("dns.id");
            if (dnsId.HasValue)
            {
                bool found = firstPacket.TryGetFieldValue(dnsId.Value, out _, materialize: true); // materialize: true — need complete field tree for assertion
                await Assert.That(found).IsFalse()
                    .Because("DNS id must not be present until reassembly completes");
            }
        }
    }

    [Test]
    public async Task Reassembly_DnsOverTcp_TwoSegments_SecondSegmentDnsIdPresent()
    {
        // Split DNS-over-TCP PDU across two TCP segments.
        // After the second segment, the PDU is complete and DNS must be dispatched.
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            uint cIsn = 7000, sIsn = 8000;

            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
            ProtocolTestHelper.ParseFrame(stack, _ServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

            byte[] fullPdu = _BuildDnsTcpPdu(txId: 0xDEAD);

            // Segment 1: first 8 bytes
            ReadOnlySpan<byte> seg1 = fullPdu.AsSpan(0, 8);
            ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, seg1),
                3, Timestamp.FromMillis(3));

            // Segment 2: remaining 15 bytes — completes the PDU
            ReadOnlySpan<byte> seg2 = fullPdu.AsSpan(8);
            Packet secondPacket = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1 + (uint)seg1.Length, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, seg2),
                4, Timestamp.FromMillis(4));

            // DNS must be dispatched when reassembly completes
            await ProtocolTestHelper.AssertU64Field(stack, secondPacket, "dns.id", 0xDEAD).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Reassembly_DnsOverTcp_TwoSegments_QueryNamePresent()
    {
        // Verify that the DNS query name is present after reassembly.
        Stack stack = ProtocolTestHelper.BuildStack();
        using (stack)
        {
            uint cIsn = 9000, sIsn = 10000;

            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
            ProtocolTestHelper.ParseFrame(stack, _ServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
            ProtocolTestHelper.ParseFrame(stack, _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

            byte[] fullPdu = _BuildDnsTcpPdu(txId: 0xBEEF);

            // Segment 1: 12 bytes
            ReadOnlySpan<byte> seg1 = fullPdu.AsSpan(0, 12);
            ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, seg1),
                3, Timestamp.FromMillis(3));

            // Segment 2: remaining 11 bytes
            ReadOnlySpan<byte> seg2 = fullPdu.AsSpan(12);
            Packet secondPacket = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(cIsn + 1 + (uint)seg1.Length, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, seg2),
                4, Timestamp.FromMillis(4));

            // DNS query name "a.b" must be present
            await ProtocolTestHelper.AssertStringField(stack, secondPacket, "dns.qry.name", "a.b").ConfigureAwait(false);
        }
    }

    #endregion

    #region Bound PDU + stateful inner parser

    private const ushort _ProbePort = 65000;

    /// <summary>
    /// Stateful inner parser whose only job is to call <see cref="Packet.GetEffectLayerKey"/>.
    /// Unbound TCP PDUs throw and surface as <c>packet.error</c>.
    /// </summary>
    private sealed class LayerKeyProbeProtocol : IProtocol
    {
        public string Name => "layerkey.probe";
        public string UiName => "Layer Key Probe";

        /// <summary>Layer key from the most recent <see cref="Parse"/> call.</summary>
        public int LastLayerKey { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            LastLayerKey = parentField.Packet.GetEffectLayerKey(data);
            return data.Length;
        }
    }

    private static Stack _BuildProbeStack(out LayerKeyProbeProtocol probe)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        probe = new();
        builder.RegisterProtocol(probe, static (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(TcpProtocol.PortTableName, _ProbePort, id);
            b.RegisterStreamReassemblyConfig(id, new StreamReassemblyConfig
            {
                BoundaryDetector = new LengthPrefixDetector(
                    lengthOffset: 0,
                    lengthSize: 2,
                    bigEndian: true,
                    lengthIncludesHeader: false,
                    headerSize: 2),
            });
        });
        return builder.Build();
    }

    private static byte[] _ProbeClientFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
        => _BuildTcpFrame(_ClientIp, _ServerIp, _ClientPort, _ProbePort, seq, ack, flags, payload);

    private static byte[] _ProbeServerFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
        => _BuildTcpFrame(_ServerIp, _ClientIp, _ProbePort, _ClientPort, seq, ack, flags, payload);

    [Test]
    public async Task Reassembly_StatefulInner_BoundPdu_RedissectHasNoPacketError()
    {
        using Stack stack = _BuildProbeStack(out _);
        uint cIsn = 9000, sIsn = 10000;
        byte[] fullPdu = [0x00, 0x04, 0x01, 0x02, 0x03, 0x04];

        byte[][] frames =
        [
            _ProbeClientFrame(cIsn, 0, TcpFlags.Syn),
            _ProbeServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck),
            _ProbeClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack),
            _ProbeClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, fullPdu.AsSpan(0, 3)),
            _ProbeClientFrame(cIsn + 1 + 3, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, fullPdu.AsSpan(3)),
        ];

        Packet[] firstParsed = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            firstParsed[i] = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromSecs(i));
        }

        Packet completing = firstParsed[^1];
        await ProtocolTestHelper.AssertFieldNotPresent(stack, completing, "packet.error").ConfigureAwait(false);

        Packet reparse = ProtocolTestHelper.ParseFrame(stack, frames[^1], frames.Length - 1, Timestamp.FromSecs(frames.Length - 1));
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await PacketFieldComparer.AssertFieldIdentical(stack, completing, reparse).ConfigureAwait(false);
    }

    [Test]
    public async Task Reassembly_StatefulInner_SingleSegmentPdu_RedissectLayerKeyMatchesIngest()
    {
        using Stack stack = _BuildProbeStack(out LayerKeyProbeProtocol probe);
        uint cIsn = 9000, sIsn = 10000;
        byte[] fullPdu = [0x00, 0x04, 0x01, 0x02, 0x03, 0x04];

        byte[][] frames =
        [
            _ProbeClientFrame(cIsn, 0, TcpFlags.Syn),
            _ProbeServerFrame(sIsn, cIsn + 1, TcpFlags.SynAck),
            _ProbeClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Ack),
            _ProbeClientFrame(cIsn + 1, sIsn + 1, TcpFlags.Psh | TcpFlags.Ack, fullPdu),
        ];

        Packet[] firstParsed = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            firstParsed[i] = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromSecs(i));
        }

        Packet completing = firstParsed[^1];
        await ProtocolTestHelper.AssertFieldNotPresent(stack, completing, "packet.error").ConfigureAwait(false);
        int ingestKey = probe.LastLayerKey;

        Packet reparse = ProtocolTestHelper.ParseFrame(
            stack, frames[^1], frames.Length - 1, Timestamp.FromSecs(frames.Length - 1));
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        int replayKey = probe.LastLayerKey;

        await Assert.That(replayKey).IsEqualTo(ingestKey);
    }

    #endregion
}
