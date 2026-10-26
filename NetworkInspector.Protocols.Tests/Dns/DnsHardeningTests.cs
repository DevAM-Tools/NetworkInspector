// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for DNS parsing of crafted input: the summary texts, the NSEC type bit map limits and the agreement between the
/// eager index groups and the lazily built field tree for truncated messages.
/// </summary>
internal sealed class DnsHardeningTests
{
    private const ushort _TypeNsec = 47;

    #region Summary texts

    [Test]
    public async Task PacketInfoAndContainerTitleShowTheTransactionIdWithASingleHexPrefix()
    {
        byte[] frame = DnsPayloadBuilder.WrapUdp(DnsPayloadBuilder.BuildQuery(0x1234, "example.com", DnsPayloadBuilder.Type.A));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("DNS Query 0x1234");
            await ProtocolTestHelper.AssertDisplayText(
                stack, packet, "dns", "Domain Name System (query), Transaction ID: 0x1234").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ResponseSummaryShowsTheTransactionIdWithASingleHexPrefix()
    {
        byte[] dns = DnsPayloadBuilder.BuildResponseSingleRR(0xABCD, "example.com", DnsPayloadBuilder.Type.A, [10, 0, 0, 1], 60);
        byte[] frame = DnsPayloadBuilder.WrapUdp(dns);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await Assert.That(packet.Info).IsEqualTo("DNS Response 0xabcd");
            await ProtocolTestHelper.AssertDisplayText(
                stack, packet, "dns", "Domain Name System (response), Transaction ID: 0xabcd").ConfigureAwait(false);
        }
    }

    #endregion

    #region NSEC type bit maps

    /// <summary>
    /// Builds a response holding one NSEC record whose RDATA is the root name followed by the given type bit map blocks.
    /// </summary>
    private static byte[] _NsecFrame(params byte[][] blocks)
    {
        List<byte> rdata = [0x00]; // next domain name: the root
        foreach (byte[] block in blocks)
        {
            rdata.AddRange(block);
        }

        byte[] dns = DnsPayloadBuilder.BuildResponseSingleRR(1, "example.com", _TypeNsec, rdata.ToArray(), 60);
        return DnsPayloadBuilder.WrapUdp(dns);
    }

    private static byte[] _Block(byte window, int length, byte fill)
    {
        byte[] block = new byte[2 + length];
        block[0] = window;
        block[1] = (byte)length;
        Array.Fill(block, fill, 2, length);
        return block;
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)255)]
    public async Task NsecBitMapLongerThan32BytesEndsTheTypeList(byte window)
    {
        // First block is valid (type A); the second declares 33 bytes, one above the RFC 4034 maximum of 32.
        byte[] frame = _NsecFrame([0x00, 0x01, 0x40], _Block(window, 33, 0xFF));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.nsec.type_bitmap", "A").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task NsecBitMapOf32BytesIsDecodedCompletely()
    {
        // The last bit of the 32nd byte is type 255 (ANY): the maximum legal bit map must reach it.
        byte[] block = _Block(0, 32, 0x00);
        block[2 + 31] = 0x01;
        byte[] frame = _NsecFrame(block);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "dns.nsec.type_bitmap", "ANY").ConfigureAwait(false);
        }
    }

    #endregion

    #region Truncated records

    /// <summary>
    /// An answer whose RDLENGTH (100) runs past the message but whose declared RDATA bytes contain a complete OPT record.
    /// The additional section declares one record. The tree and the index must both stop at the truncated answer.
    /// </summary>
    private static byte[] _TruncatedAnswerWithEmbeddedOptFrame()
    {
        List<byte> dns = [];
        byte[] header = new byte[DnsPayloadBuilder.HeaderSize];
        DnsPayloadBuilder.WriteHeader(header, id: 7, flags: 0x8180, qdCount: 0, anCount: 1, nsCount: 0, arCount: 1);
        dns.AddRange(header);

        // Answer: root owner name, type A, class IN, TTL 0, RDLENGTH 100 (more than the message holds).
        dns.AddRange([0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x64]);

        // The bytes that follow are a well-formed OPT record: root name, type 41, class 4096, TTL 0, RDLENGTH 0.
        dns.AddRange([0x00, 0x00, 0x29, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        return DnsPayloadBuilder.WrapUdp(dns.ToArray());
    }

    [Test]
    public async Task IndexGroupsMatchTheTreeWhenAnAnswerIsTruncated()
    {
        byte[] frame = _TruncatedAnswerWithEmbeddedOptFrame();

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }

    [Test]
    public async Task TruncatedAnswerEndsTheWalkSoLaterSectionsShowNoRecords()
    {
        byte[] frame = _TruncatedAnswerWithEmbeddedOptFrame();

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dns.opt.udp_payload_size").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "dns.resp.name").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task IndexGroupsMatchTheTreeWhenAQuestionIsTruncated()
    {
        // One question whose name is complete but whose type and class are cut off after two bytes, followed by nothing.
        List<byte> dns = [];
        byte[] header = new byte[DnsPayloadBuilder.HeaderSize];
        DnsPayloadBuilder.WriteHeader(header, id: 9, flags: 0x0100, qdCount: 1, anCount: 1, nsCount: 0, arCount: 0);
        dns.AddRange(header);
        dns.AddRange(DnsPayloadBuilder.EncodeName("a.b"));
        dns.AddRange([0x00, 0x01]);

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(DnsPayloadBuilder.WrapUdp(dns.ToArray())).ConfigureAwait(false);
    }

    #endregion
}
