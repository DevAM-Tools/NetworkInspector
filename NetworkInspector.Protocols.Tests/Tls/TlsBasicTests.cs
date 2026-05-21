// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// TLS happy-path tests: record layer, ClientHello / ServerHello, Alert and the
/// most common extensions (SNI, ALPN, supported_versions).
/// </summary>
internal sealed class TlsBasicTests
{
    private static byte[] MakeRandom32(byte seed)
    {
        byte[] r = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            r[i] = (byte)(seed + i);
        }
        return r;
    }

    [Test]
    public async Task Parse_RecordHeader_AppData()
    {
        byte[] body = [0xDE, 0xAD, 0xBE, 0xEF];
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData, TlsRecordLayer.Tls12, body);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.record.content_type", 23).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.record.version", 0x0303).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.record.length", (ulong)body.Length).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClientHello_Sni()
    {
        byte[] sni = TlsRecordLayer.BuildExtension(
            TlsExtensionType.ServerName,
            TlsRecordLayer.BuildSniExtensionBody("example.com"));
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12,
            MakeRandom32(0x10),
            sessionId: [],
            cipherSuites: [0x1301, 0x1302],
            compressionMethods: [0x00],
            extensionsConcatenated: sni);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ClientHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(
            TlsContentType.Handshake, TlsRecordLayer.Tls10, hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.handshake.type", 1).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "tls.handshake.extensions.server_name", "example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClientHello_Alpn()
    {
        byte[] alpn = TlsRecordLayer.BuildExtension(
            TlsExtensionType.Alpn,
            TlsRecordLayer.BuildAlpnExtensionBody("h2", "http/1.1"));
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, MakeRandom32(0x20), [],
            [0x1301], [0x00], alpn);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ClientHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Handshake, TlsRecordLayer.Tls10, hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "tls.handshake.extensions.alpn_str", "h2").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClientHello_SupportedVersions_Tls13()
    {
        byte[] sv = TlsRecordLayer.BuildExtension(
            TlsExtensionType.SupportedVersions,
            TlsRecordLayer.BuildSupportedVersionsExtensionBody(TlsRecordLayer.Tls13, TlsRecordLayer.Tls12));
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, MakeRandom32(0x30), [],
            [0x1301], [0x00], sv);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ClientHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Handshake, TlsRecordLayer.Tls10, hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.handshake.extensions.supported_version", TlsRecordLayer.Tls13).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ServerHello_CipherSuite()
    {
        byte[] body = TlsRecordLayer.BuildServerHelloBody(
            TlsRecordLayer.Tls12, MakeRandom32(0x40), [],
            cipherSuite: 0x1301, compressionMethod: 0, extensionsConcatenated: []);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ServerHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Handshake, TlsRecordLayer.Tls12, hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.handshake.type", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.handshake.ciphersuite", 0x1301).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_AlertRecord()
    {
        byte[] alert = TlsRecordLayer.BuildAlertBody(level: 2, description: 50);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Alert, TlsRecordLayer.Tls12, alert);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.alert.level", 2).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.alert.description", 50).ConfigureAwait(false);
        }
    }
}
