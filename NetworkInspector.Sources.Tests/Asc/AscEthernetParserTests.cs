// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscEthernetParser.TryParse"/> covering section 4.2 packets,
/// numeric base, wrapped hex, and lines that are not packets.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscEthernetParserTests
{
    [Test]
    public async Task BasicEthernetHexLengthParsed()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx e:001122334455667788990A0B0C0D"u8,
            16, out AscTimestamp ts, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ts.WholeSeconds).IsEqualTo(0);
        await Assert.That(ts.Nanoseconds).IsEqualTo(500_000_000);
        await Assert.That(ch).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(14);
        await Assert.That(frame[0]).IsEqualTo((byte)0x00);
        await Assert.That(frame[1]).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task BaseDecLength14Parsed()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx 14:001122334455667788990A0B0C0D"u8,
            10, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(14);
    }

    [Test]
    public async Task WrappedHex60BytesStartsAtBroadcastMac()
    {
        string first = "0.000000 ETH 2 Tx 3c:FFFFFFFFFFFF";
        string second = new string('A', 108);
        byte[] record = Encoding.ASCII.GetBytes(first + "\n" + second);
        bool ok = AscEthernetParser.TryParse(record, 16, out _, out int ch, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(ch).IsEqualTo(2);
        await Assert.That(frame.Length).IsEqualTo(60);
        await Assert.That(frame[0]).IsEqualTo((byte)0xFF);
        await Assert.That(frame[5]).IsEqualTo((byte)0xFF);
        await Assert.That(frame[6]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task ShortHexReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx e:001122"u8,
            16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task AfdxPacketReturnsEthernetBytes()
    {
        bool ok = AscEthernetParser.TryParse(
            "1.627267 AFDX 1 Tx 1 0 0 4:01020304"u8,
            16, out _, out int channel, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(4);
        await Assert.That(frame[0]).IsEqualTo((byte)0x01);
        await Assert.That(frame[3]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task TxFwdReturnsEthernetBytes()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.000000 ETH 1 TxFwd 4:01020304"u8,
            16, out _, out int channel, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(4);
        await Assert.That(frame[0]).IsEqualTo((byte)0x01);
        await Assert.That(frame[3]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task AfdxWithoutLengthFieldsReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.600000 AFDX 1 Rx e:AABBCCDDEEFF112233445566ABCD"u8,
            16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task EthStatReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.100000 ETH 1 STAT 0 0"u8,
            16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task EthRxErReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.200000 ETH 2 RxEr e:001122334455667788990A0B0C0D"u8,
            16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task LargerFrameAllBytesParsed()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.700000 ETH 1 Rx 12:AABBCCDDEEFF112233445566080045000014"u8,
            16, out _, out _, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.Length).IsEqualTo(18);
    }

    [Test]
    public async Task EmptyLineReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(""u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task MissingColonReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH 1 Rx 14 AABBCCDDEEFF112233445566ABCD"u8,
            16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TruncatedLineReturnsFalse()
    {
        bool ok = AscEthernetParser.TryParse(
            "0.500000 ETH"u8, 16, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
