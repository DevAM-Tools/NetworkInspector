// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP protocol parsing (RFC 793).
/// Verifies ports, flags, sequence numbers, and tshark cross-validation.
/// </summary>
internal sealed class TcpBasicTests
{
    /// <summary>Creates an Ethernet + IPv4 + TCP frame with known values.</summary>
    private static byte[] _BuildTcpFrame(
        ushort srcPort = 49152,
        ushort dstPort = 80,
        uint seqNum = 1000,
        uint ackNum = 0,
        byte flags = TcpFlags.Syn)
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
        EthernetLayer eth = new(dstMac, srcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002)); // 10.0.0.1 → 10.0.0.2
        TcpLayer tcp = new(srcPort, dstPort, seqNum: seqNum, ackNum: ackNum, flags: flags);
        byte[] payload = [0x48, 0x45, 0x4C, 0x4C, 0x4F]; // "HELLO"

        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task Parse_Tcp_SourcePort()
    {
        byte[] frame = _BuildTcpFrame(srcPort: 49152);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.srcport", 49152).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Tcp_DestinationPort()
    {
        byte[] frame = _BuildTcpFrame(dstPort: 443);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.dstport", 443).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Tcp_SequenceNumber()
    {
        byte[] frame = _BuildTcpFrame(seqNum: 123456);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.seq", 123456).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Tcp_SynFlag_Set()
    {
        byte[] frame = _BuildTcpFrame(flags: TcpFlags.Syn);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.flags.syn", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.flags.ack", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.flags.fin", false).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Tcp_SynAckFlags()
    {
        byte[] frame = _BuildTcpFrame(flags: TcpFlags.SynAck, ackNum: 5000);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.flags.syn", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "tcp.flags.ack", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.ack", 5000).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Tcp_HeaderLength()
    {
        byte[] frame = _BuildTcpFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Standard TCP header without options: 20 bytes
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tcp.hdr_len", 20).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_TruncatedTcp_DoesNotCrash()
    {
        // Valid Ethernet + IPv4 headers, but TCP header truncated to 6 bytes
        byte[] dstMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        byte[] srcMac = [0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB];
        byte[] truncated = [
            .. dstMac, .. srcMac,
            0x08, 0x00, // EtherType: IPv4
            // Minimal valid IPv4 header (20 bytes) pointing to TCP
            0x45, 0x00, 0x00, 0x1A, // version/IHL, DSCP/ECN, total length = 26
            0x00, 0x00, 0x40, 0x00, // id, flags (DF), fragment offset
            0x40, 0x06, 0x00, 0x00, // TTL=64, protocol=6 (TCP), checksum placeholder
            0x0A, 0x00, 0x00, 0x01, // src IP: 10.0.0.1
            0x0A, 0x00, 0x00, 0x02, // dst IP: 10.0.0.2
            // Truncated TCP: only 6 bytes instead of minimum 20
            0xC0, 0x00, 0x00, 0x50, 0x00, 0x00,
        ];
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(truncated);
        using (stack)
        {
            await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThanOrEqualTo(1); // materialize: false — current materialized count only
        }
    }

    [Test]
    public async Task TsharkCrossValidation_SourcePort()
    {
        byte[] frame = _BuildTcpFrame(srcPort: 49152);
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "tcp.srcport");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("49152");
    }

    [Test]
    public async Task TsharkCrossValidation_DestinationPort()
    {
        byte[] frame = _BuildTcpFrame(dstPort: 443);
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "tcp.dstport");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("443");
    }

    [Test]
    public async Task TsharkCrossValidation_SequenceNumber()
    {
        byte[] frame = _BuildTcpFrame(seqNum: 123456);
        string? tsharkValue = TsharkVerifier.GetFieldValue(frame, "tcp.seq_raw");
        if (tsharkValue is null)
        {
            return;
        }
        await Assert.That(tsharkValue).IsEqualTo("123456");
    }

    [Test]
    public async Task Parse_Tcp_PortField_ContainsBothEndpoints()
    {
        // tcp.port is a metadata-only alias group ({ tcp.srcport, tcp.dstport }); no tcp.port
        // field is appended to the parse tree. The protocol table name tcp.port (TCP demux)
        // lives in an independent namespace from this alias.
        byte[] frame = _BuildTcpFrame(srcPort: 49152, dstPort: 443);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(stack.GetFieldId("tcp.port")).IsNull()
                .Because("tcp.port is an alias name and must never resolve via GetFieldId");

            FieldAliasGroupId? aliasId = stack.GetFieldAliasGroupId("tcp.port");
            await Assert.That(aliasId).IsNotNull().Because("tcp.port alias group must be registered");

            FieldAliasGroupInfo? aliasInfo = stack.GetFieldAliasGroup(aliasId!.Value);
            await Assert.That(aliasInfo).IsNotNull();
            await Assert.That(aliasInfo!.MemberCount).IsEqualTo(2);

            FieldId srcId = stack.GetFieldId("tcp.srcport")!.Value;
            FieldId dstId = stack.GetFieldId("tcp.dstport")!.Value;
            FieldId[] members = aliasInfo.Members.ToArray();
            await Assert.That(members.Contains(srcId)).IsTrue();
            await Assert.That(members.Contains(dstId)).IsTrue();

            List<ulong> found = [];
            foreach (FieldId memberId in members)
            {
                FieldLookupCookie cookie = FieldLookupCookie.Start;
                while (packet.TryGetNextFieldValue(memberId, ref cookie, out FieldValue value, materialize: true)) // materialize: true — need complete field tree for assertion
                {
                    bool ok = value.Data.TryGetAsU64(out ulong port);
                    await Assert.That(ok).IsTrue().Because("alias member values must be U64");
                    found.Add(port);
                }
            }

            await Assert.That(found.Count).IsEqualTo(2);
            await Assert.That(found.Contains(49152UL)).IsTrue();
            await Assert.That(found.Contains(443UL)).IsTrue();
        }
    }
}
