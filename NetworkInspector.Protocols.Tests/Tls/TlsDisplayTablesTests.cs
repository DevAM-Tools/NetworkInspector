// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using TlsDisplayTables = NetworkInspector.Protocols.Tls.TlsDisplayTables;

namespace NetworkInspector.Protocols.Tests;
/// <summary>
/// Tests for the TLS display tables with values taken from the wire, including 0x?A?A codes that are not canonical
/// GREASE values.
/// </summary>
internal sealed class TlsDisplayTablesTests
{
    [Test]
    [Arguments((ushort)0x0A0A, "GREASE (0x0A0A)")]
    [Arguments((ushort)0xFAFA, "GREASE (0xFAFA)")]
    [Arguments((ushort)0x1A2A, "GREASE (0x1A2A)")]
    [Arguments((ushort)0xFA0A, "GREASE (0xFA0A)")]
    public async Task CipherSuiteWithGreaseNibblePatternDoesNotThrow(ushort code, string expected)
    {
        string text = TlsDisplayTables.GetCipherSuiteDisplayText(code);

        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments((ushort)0x0A0A, "GREASE (0x0A0A)")]
    [Arguments((ushort)0x1A2A, "GREASE (0x1A2A)")]
    [Arguments((ushort)0x0A1A, "GREASE (0x0A1A)")]
    public async Task ExtensionTypeWithGreaseNibblePatternDoesNotThrow(ushort type, string expected)
    {
        string text = TlsDisplayTables.GetExtensionTypeDisplayText(type);

        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task SignatureAlgorithm0x0301IsRsaWithSha224()
    {
        string text = TlsDisplayTables.GetSignatureAlgorithmDisplayText(0x0301);

        await Assert.That(text).IsEqualTo("rsa_sha224 (0x0301)");
    }

    [Test]
    public async Task ClientHelloWithNonCanonicalGreaseValuesIsParsed()
    {
        // A cipher suite and an extension type of 0x1A2A reach the display tables straight from the wire.
        byte[] extension = TlsRecordLayer.BuildExtension(0x1A2A, [0x00]);
        byte[] body = TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12,
            new byte[32],
            sessionId: [],
            cipherSuites: [0x1A2A, 0x1301],
            compressionMethods: [0x00],
            extensionsConcatenated: extension);
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
            await ProtocolTestHelper.AssertU64Field(stack, packet, "tls.record.content_type", 22).ConfigureAwait(false);
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }
}
