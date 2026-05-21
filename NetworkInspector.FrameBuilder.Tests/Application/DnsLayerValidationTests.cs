// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Boundary and validation tests for <see cref="DnsLayer"/> builder methods.
/// </summary>
internal sealed class DnsLayerValidationTests
{
    #region BuildResponseSingleRR — RDATA length

    [Test]
    public async Task BuildResponseSingleRR_RdataExceedsUInt16_Throws()
    {
        byte[] rdata = new byte[65536];
        bool threw = false;
        try
        {
            DnsLayer.BuildResponseSingleRR(1, "example.com", 1, rdata, 300);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task BuildResponseSingleRR_MaxRdata_Succeeds()
    {
        byte[] rdata = new byte[65535];
        DnsLayer layer = DnsLayer.BuildResponseSingleRR(1, "a.com", 16, rdata, 0);
        await Assert.That(layer.HeaderSize).IsGreaterThan(0);
    }

    #endregion

    #region ToTcpPayload — message length

    [Test]
    public async Task ToTcpPayload_MessageExceedsUInt16_Throws()
    {
        // Build a layer directly with an oversized pre-built message.
        byte[] oversized = new byte[65536];
        DnsLayer layer = new(oversized);
        bool threw = false;
        try
        {
            layer.ToTcpPayload();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    #endregion

    #region BuildTxtRdata — chunk length

    [Test]
    public async Task BuildTxtRdata_StringExceeds255Bytes_Throws()
    {
        string s = new('a', 256);
        bool threw = false;
        try
        {
            DnsLayer.BuildTxtRdata(s);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task BuildTxtRdata_MaxString_Succeeds()
    {
        string s = new('a', 255);
        byte[] rdata = DnsLayer.BuildTxtRdata(s);
        // 1-byte length prefix + 255 bytes = 256
        await Assert.That(rdata.Length).IsEqualTo(256);
    }

    #endregion

    #region EncodeNamePointer — offset range

    [Test]
    public async Task EncodeNamePointer_OffsetExceeds0x3FFF_Throws()
    {
        bool threw = false;
        try
        {
            DnsLayer.EncodeNamePointer(0x4000);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task EncodeNamePointer_MaxOffset_Succeeds()
    {
        byte[] ptr = DnsLayer.EncodeNamePointer(0x3FFF);
        // Top 2 bits of byte 0 must be set; lower 14 bits are 0x3FFF.
        await Assert.That((ptr[0] & 0xC0)).IsEqualTo(0xC0);
        await Assert.That(ptr[1]).IsEqualTo((byte)0xFF);
    }

    #endregion

    #region EncodeName — non-ASCII rejection

    [Test]
    public async Task EncodeName_NonAsciiCharacter_Throws()
    {
        bool threw = false;
        try
        {
            DnsLayer.EncodeName("exämple.com");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task EncodeName_PureAscii_Succeeds()
    {
        byte[] encoded = DnsLayer.EncodeName("example.com");
        // \x07example\x03com\x00 = 13 bytes
        await Assert.That(encoded.Length).IsEqualTo(13);
    }

    #endregion
}
