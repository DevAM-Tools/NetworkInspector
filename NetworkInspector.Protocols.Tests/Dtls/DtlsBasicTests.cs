// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// DTLS happy-path tests: record header (epoch + 48-bit sequence number) and
/// fragmented handshake header.
/// </summary>
internal sealed class DtlsBasicTests
{
    [Test]
    public async Task Parse_DtlsRecord_AppData()
    {
        byte[] body = [0x01, 0x02, 0x03];
        DtlsRecordLayer dtls = DtlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData,
            version: DtlsRecordLayer.Dtls12,
            epoch: 1,
            sequenceNumber48: 0x000000000042,
            body: body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 443);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dtls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.content_type", 23).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.epoch", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.sequence_number", 0x42).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.record.length", (ulong)body.Length).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_DtlsHandshake_ClientHelloHeader()
    {
        // Reuse the TLS ClientHello body builder — DTLS reuses the same body layout.
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            DtlsRecordLayer.Dtls12, new byte[32], [], [0xC02F], [0x00], []);
        byte[] hs = DtlsRecordLayer.BuildHandshakeMessage(
            TlsHandshakeType.ClientHello, msgSeq: 7, body: body);
        DtlsRecordLayer dtls = DtlsRecordLayer.BuildRecord(
            TlsContentType.Handshake, version: DtlsRecordLayer.Dtls12, epoch: 0, sequenceNumber48: 1, body: hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 443);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(udp).Then(dtls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.handshake.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.handshake.message_seq", 7).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "dtls.handshake.fragment_offset", 0).ConfigureAwait(false);
        }
    }
}
