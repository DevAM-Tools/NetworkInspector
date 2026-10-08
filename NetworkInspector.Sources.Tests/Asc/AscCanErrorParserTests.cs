// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Unit tests for <see cref="AscCanErrorParser"/>.
/// <para>This type is not thread-safe.</para>
/// </summary>
internal sealed class AscCanErrorParserTests
{
    [Test]
    public async Task ErrorFrameSetsCanErrFlag()
    {
        bool ok = AscCanErrorParser.TryParse(
            "0.700000 1 ErrorFrame"u8,
            out _, out int channel, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame)).IsEqualTo(0x20000000u);
        await Assert.That(frame[4]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task OverloadFrameUsesTheSameErrorBuffer()
    {
        bool ok = AscCanErrorParser.TryParse(
            "0.800000 1 OverloadFrame"u8,
            out _, out int channel, out byte[] frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(channel).IsEqualTo(1);
        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame)).IsEqualTo(0x20000000u);
    }

    [Test]
    public async Task MissingChannelReturnsFalse()
    {
        bool ok = AscCanErrorParser.TryParse("0.700000 ErrorFrame"u8, out _, out _, out _);

        await Assert.That(ok).IsFalse();
    }
}
