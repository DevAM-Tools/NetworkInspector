// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Boundary and validation tests for <see cref="TlsRecordLayer"/> and
/// <see cref="DtlsRecordLayer"/> builder methods.
/// </summary>
internal sealed class TlsRecordLayerValidationTests
{
    #region TlsRecordLayer — BuildRecord

    [Test]
    public async Task BuildRecord_MaxBodyLength_Succeeds()
    {
        byte[] body = new byte[65535];
        TlsRecordLayer layer = TlsRecordLayer.BuildRecord(TlsContentType.ApplicationData, TlsRecordLayer.Tls12, body);
        await Assert.That(layer.HeaderSize).IsEqualTo(5 + 65535);
    }

    [Test]
    public async Task BuildRecord_BodyExceedsUInt16_Throws()
    {
        byte[] body = new byte[65536];
        await Assert.That(() => TlsRecordLayer.BuildRecord(TlsContentType.ApplicationData, TlsRecordLayer.Tls12, body)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region TlsRecordLayer — BuildHandshakeMessage

    [Test]
    public async Task BuildHandshakeMessage_MaxBodyLength_Succeeds()
    {
        byte[] body = new byte[0x00FFFFFF];
        byte[] msg = TlsRecordLayer.BuildHandshakeMessage(0x01, body);
        await Assert.That(msg.Length).IsEqualTo(4 + 0x00FFFFFF);
    }

    [Test]
    public async Task BuildHandshakeMessage_BodyExceeds24Bit_Throws()
    {
        byte[] body = new byte[0x01000000];
        await Assert.That(() => TlsRecordLayer.BuildHandshakeMessage(0x01, body)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region TlsRecordLayer — BuildClientHelloBody cipher suites

    [Test]
    public async Task BuildClientHelloBody_TooManyCipherSuites_Throws()
    {
        byte[] random = new byte[32];
        ushort[] ciphers = new ushort[32768]; // 32768 × 2 = 65536 bytes > 65535
        await Assert.That(() => TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, random, ReadOnlySpan<byte>.Empty,
            ciphers, [0], ReadOnlySpan<byte>.Empty)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildClientHelloBody_ExtensionsTooLong_Throws()
    {
        byte[] random = new byte[32];
        byte[] exts = new byte[65536];
        await Assert.That(() => TlsRecordLayer.BuildClientHelloBody(
            TlsRecordLayer.Tls12, random, ReadOnlySpan<byte>.Empty,
            [0x002F], [0], exts)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region TlsRecordLayer — BuildServerHelloBody extensions

    [Test]
    public async Task BuildServerHelloBody_ExtensionsTooLong_Throws()
    {
        byte[] random = new byte[32];
        byte[] exts = new byte[65536];
        await Assert.That(() => TlsRecordLayer.BuildServerHelloBody(
            TlsRecordLayer.Tls12, random, ReadOnlySpan<byte>.Empty,
            0x002F, 0, exts)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region TlsRecordLayer — BuildExtension

    [Test]
    public async Task BuildExtension_DataTooLong_Throws()
    {
        byte[] data = new byte[65536];
        await Assert.That(() => TlsRecordLayer.BuildExtension(0x0000, data)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildExtension_MaxData_Succeeds()
    {
        byte[] data = new byte[65535];
        byte[] ext = TlsRecordLayer.BuildExtension(0x0010, data);
        await Assert.That(ext.Length).IsEqualTo(4 + 65535);
    }

    #endregion

    #region TlsRecordLayer — BuildAlpnExtensionBody

    [Test]
    public async Task BuildAlpnExtensionBody_ProtocolNameTooLong_Throws()
    {
        string longName = new('a', 256);
        await Assert.That(() => TlsRecordLayer.BuildAlpnExtensionBody(longName)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildAlpnExtensionBody_MaxProtocolName_Succeeds()
    {
        string name255 = new('a', 255);
        byte[] body = TlsRecordLayer.BuildAlpnExtensionBody(name255);
        // 2-byte list length + 1-byte name length + 255-byte name = 258 bytes.
        await Assert.That(body.Length).IsEqualTo(258);
    }

    #endregion

    #region TlsRecordLayer — BuildSupportedVersionsExtensionBody

    [Test]
    public async Task BuildSupportedVersionsExtensionBody_TooManyVersions_Throws()
    {
        ushort[] versions = new ushort[128]; // 128 × 2 = 256 > 255
        await Assert.That(() => TlsRecordLayer.BuildSupportedVersionsExtensionBody(versions)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildSupportedVersionsExtensionBody_MaxVersions_Succeeds()
    {
        ushort[] versions = new ushort[127]; // 127 × 2 = 254 ≤ 255
        byte[] body = TlsRecordLayer.BuildSupportedVersionsExtensionBody(versions);
        await Assert.That(body.Length).IsEqualTo(1 + 254);
    }

    #endregion

    #region DtlsRecordLayer — BuildRecord

    [Test]
    public async Task DtlsBuildRecord_MaxBodyLength_Succeeds()
    {
        byte[] body = new byte[65535];
        DtlsRecordLayer layer = DtlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData, DtlsRecordLayer.Dtls12, 0, 0, body);
        await Assert.That(layer.HeaderSize).IsEqualTo(13 + 65535);
    }

    [Test]
    public async Task DtlsBuildRecord_BodyExceedsUInt16_Throws()
    {
        byte[] body = new byte[65536];
        await Assert.That(() => DtlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData, DtlsRecordLayer.Dtls12, 0, 0, body)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DtlsBuildRecord_MaxSequenceNumber_Succeeds()
    {
        byte[] body = [0x01];
        DtlsRecordLayer layer = DtlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData, DtlsRecordLayer.Dtls12, 0, 0x0000_FFFF_FFFF_FFFFul, body);
        await Assert.That(layer.HeaderSize).IsEqualTo(14);
    }

    [Test]
    public async Task DtlsBuildRecord_SequenceNumberExceeds48Bit_Throws()
    {
        byte[] body = [0x01];
        await Assert.That(() => DtlsRecordLayer.BuildRecord(
            TlsContentType.ApplicationData, DtlsRecordLayer.Dtls12, 0, 0x0001_0000_0000_0000ul, body)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region DtlsRecordLayer — BuildHandshakeMessage

    [Test]
    public async Task DtlsBuildHandshakeMessage_BodyExceeds24Bit_Throws()
    {
        byte[] body = new byte[0x01000000];
        await Assert.That(() => DtlsRecordLayer.BuildHandshakeMessage(0x01, 0, body)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion
}
