// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="TcpOptionsBuilder"/>: every single-option method writes exactly the bytes of its RFC encoding, and
/// the bundled SYN option set is the same bytes as its parts.
/// </summary>
internal sealed class TcpOptionsBuilderTests
{
    #region Single options

    [Test]
    public async Task NopAndEolWriteOneByteEach()
    {
        TcpOptionsBuilder builder = new();
        builder.Nop();
        builder.Nop();
        builder.Eol();

        // Three bytes, padded to the next 4-byte boundary with the End-Of-Options value.
        await Assert.That(builder.Build().Data.ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x01, 0x00, 0x00 });
    }

    [Test]
    public async Task SackPermittedWritesKindAndLength()
    {
        TcpOptionsBuilder builder = new();
        builder.SackPermitted();
        builder.Nop();
        builder.Nop();

        await Assert.That(builder.Build().Data.ToArray()).IsEquivalentTo(new byte[] { 0x04, 0x02, 0x01, 0x01 });
    }

    [Test]
    public async Task WindowScaleWritesTheShiftCount()
    {
        TcpOptionsBuilder builder = new();
        builder.Nop();
        builder.WindowScale(14);

        await Assert.That(builder.Build().Data.ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x03, 0x03, 0x0E });
    }

    [Test]
    public async Task WindowScaleAboveFourteenThrows()
    {
        // RFC 7323 section 2.3: a larger shift must not be sent, so the builder refuses to encode it.
        await Assert.That(static () =>
        {
            TcpOptionsBuilder builder = new();
            builder.WindowScale(15);
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TimestampsWriteBothValuesBigEndian()
    {
        TcpOptionsBuilder builder = new();
        builder.Nop();
        builder.Nop();
        builder.Timestamps(0x01020304, 0x0A0B0C0D);

        byte[] expected = [0x01, 0x01, 0x08, 0x0A, 0x01, 0x02, 0x03, 0x04, 0x0A, 0x0B, 0x0C, 0x0D];
        await Assert.That(builder.Build().Data.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task SackWritesOneEightByteBlockPerRange()
    {
        TcpOptionsBuilder builder = new();
        builder.Nop();
        builder.Nop();
        builder.Sack((100u, 200u), (300u, 400u));

        byte[] data = builder.Build().Data.ToArray();

        // kind 5, length 2 + 2 * 8 = 18, then left/right edges big endian.
        await Assert.That(data[2]).IsEqualTo((byte)0x05);
        await Assert.That(data[3]).IsEqualTo((byte)18);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4))).IsEqualTo(100u);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8))).IsEqualTo(200u);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12))).IsEqualTo(300u);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16))).IsEqualTo(400u);
    }

    [Test]
    public async Task SackWithoutBlocksThrows()
    {
        await Assert.That(static () =>
        {
            TcpOptionsBuilder builder = new();
            builder.Sack();
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SackWithFiveBlocksThrows()
    {
        // Four blocks is the most the 40-byte option area can hold once the two header bytes are counted.
        await Assert.That(static () =>
        {
            TcpOptionsBuilder builder = new();
            builder.Sack((1u, 2u), (3u, 4u), (5u, 6u), (7u, 8u), (9u, 10u));
        }).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task UnknownOptionDerivesTheLengthFromThePayload()
    {
        TcpOptionsBuilder builder = new();
        builder.Unknown(0xFE, [0xAA, 0xBB]);

        await Assert.That(builder.Build().Data.ToArray()).IsEquivalentTo(new byte[] { 0xFE, 0x04, 0xAA, 0xBB });
    }

    #endregion

    #region Bundled SYN options

    [Test]
    public async Task SynOptionsEqualTheirBuildingBlocks()
    {
        TcpOptionsBuilder bundle = new();
        bundle.SynOptions();

        TcpOptionsBuilder parts = new();
        parts.Mss(1460);
        parts.SackPermitted();
        parts.Nop();
        parts.Nop();
        parts.Timestamps(0, 0);
        parts.Nop();
        parts.WindowScale(7);

        byte[] bundled = bundle.Build().Data.ToArray();

        // 22 bytes of options, padded to 24.
        await Assert.That(bundled.Length).IsEqualTo(24);
        await Assert.That(bundled).IsEquivalentTo(parts.Build().Data.ToArray());
    }

    #endregion
}
