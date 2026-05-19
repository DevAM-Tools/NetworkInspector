// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Cross-validation tests for TLS parsing against tshark.
/// </summary>
internal sealed class TlsTsharkTests
{
    private static byte[] BuildClientHelloWithSni(string host)
    {
        byte[] sni = TlsRecordLayer.BuildExtension(
            TlsExtensionType.ServerName,
            TlsRecordLayer.BuildSniExtensionBody(host));
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, new byte[32], [], [0x1301, 0x1302], [0x00], sni);
        byte[] hs = TlsRecordLayer.BuildHandshakeMessage(TlsHandshakeType.ClientHello, body);
        TlsRecordLayer tls = TlsRecordLayer.BuildRecord(TlsContentType.Handshake, TlsRecordLayer.Tls10, hs);
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        TcpLayer tcp = new(12345, 443, seqNum: 1, ackNum: 0, flags: 0x18);
        return FrameStack.Start(eth).Then(ip).Then(tcp).Then(tls).CreateWithFixedValues().EmitFrame([]);
    }

    [Test]
    public async Task Tshark_RecordContentType_Handshake()
    {
        byte[] frame = BuildClientHelloWithSni("example.com");
        string? value = TsharkVerifier.GetFieldValue(frame, "tls.record.content_type");
        await Assert.That(value).IsNotNull();
        await Assert.That(value).IsEqualTo("22");
    }

    [Test]
    public async Task Tshark_HandshakeType_ClientHello()
    {
        byte[] frame = BuildClientHelloWithSni("example.com");
        string? value = TsharkVerifier.GetFieldValue(frame, "tls.handshake.type");
        await Assert.That(value).IsNotNull();
        await Assert.That(value).IsEqualTo("1");
    }

    [Test]
    public async Task Tshark_Sni_Hostname()
    {
        byte[] frame = BuildClientHelloWithSni("example.com");
        // tshark uses an underscore in its display-filter name for this field.
        string? value = TsharkVerifier.GetFieldValue(frame, "tls.handshake.extensions_server_name");
        await Assert.That(value).IsNotNull();
        await Assert.That(value).IsEqualTo("example.com");
    }
}
