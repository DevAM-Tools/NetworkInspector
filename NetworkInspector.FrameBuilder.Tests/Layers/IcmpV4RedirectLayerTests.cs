// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="IcmpV4RedirectLayer"/> — verifies Type=5, code values, and that the
/// Gateway Internet Address is serialized correctly in bytes 4–7 of the ICMP header.
/// </summary>
internal sealed class IcmpV4RedirectLayerTests
{
    private static readonly IPv4Address _Gateway = new(0xC0A80101); // 192.168.1.1

    private static byte[] WriteHeader(IcmpV4RedirectLayer layer)
    {
        byte[] buf = new byte[layer.HeaderSize];
        layer.WriteHeader(buf.AsSpan());
        return buf;
    }

    #region Header layout

    [Test]
    public async Task WriteHeader_TypeByteIsAlways5()
    {
        // RFC 792: Redirect is ICMP type 5.
        IcmpV4RedirectLayer layer = new(_Gateway);
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[0]).IsEqualTo((byte)5);
    }

    [Test]
    public async Task WriteHeader_DefaultCode_IsRedirectForHost()
    {
        // The default code is CodeRedirectForHost (1).
        IcmpV4RedirectLayer layer = new(_Gateway);
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[1]).IsEqualTo(IcmpV4RedirectLayer.CodeRedirectForHost);
    }

    [Test]
    [Arguments(IcmpV4RedirectLayer.CodeRedirectForNetwork, 0)]
    [Arguments(IcmpV4RedirectLayer.CodeRedirectForHost, 1)]
    [Arguments(IcmpV4RedirectLayer.CodeRedirectForTosNetwork, 2)]
    [Arguments(IcmpV4RedirectLayer.CodeRedirectForTosHost, 3)]
    public async Task WriteHeader_AllCodes_StoredInByteOne(byte code, int expectedValue)
    {
        // Each named code constant must equal its documented numeric value and be written to byte 1.
        IcmpV4RedirectLayer layer = new(_Gateway, code);
        byte[] buf = WriteHeader(layer);

        await Assert.That((int)buf[1]).IsEqualTo(expectedValue)
            .Because($"code byte must be {expectedValue} for constant value {code}");
    }

    #endregion

    #region Gateway Internet Address in bytes 4–7

    [Test]
    public async Task WriteHeader_GatewayAddress_WrittenBigEndianAtBytes4To7()
    {
        // The gateway address occupies bytes 4–7 in big-endian (network) byte order,
        // identical to IPv4Address.RawValue which is already stored as big-endian uint.
        IcmpV4RedirectLayer layer = new(_Gateway);
        byte[] buf = WriteHeader(layer);

        uint rawValue = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
        await Assert.That(rawValue).IsEqualTo(_Gateway.RawValue)
            .Because("gateway address must be the raw IPv4 value written big-endian to bytes 4–7");
    }

    [Test]
    public async Task WriteHeader_GatewayAddressOctets_MatchExpectedBytes()
    {
        // Cross-check byte-by-byte: 192.168.1.1 = 0xC0 0xA8 0x01 0x01.
        IcmpV4RedirectLayer layer = new(new IPv4Address(0xC0A80101));
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[4]).IsEqualTo((byte)0xC0);
        await Assert.That(buf[5]).IsEqualTo((byte)0xA8);
        await Assert.That(buf[6]).IsEqualTo((byte)0x01);
        await Assert.That(buf[7]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task WriteHeader_GatewayAddressZero_AllGatewayBytesAreZero()
    {
        // A zero gateway address (0.0.0.0) must produce all-zero bytes 4–7.
        IcmpV4RedirectLayer layer = new(new IPv4Address(0));
        byte[] buf = WriteHeader(layer);

        await Assert.That(buf[4]).IsEqualTo((byte)0);
        await Assert.That(buf[5]).IsEqualTo((byte)0);
        await Assert.That(buf[6]).IsEqualTo((byte)0);
        await Assert.That(buf[7]).IsEqualTo((byte)0);
    }

    #endregion

    #region HeaderSize

    [Test]
    public async Task HeaderSize_Is8()
    {
        IcmpV4RedirectLayer layer = new(_Gateway);

        await Assert.That(layer.HeaderSize).IsEqualTo(8);
    }

    #endregion
}
