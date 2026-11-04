// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for <see cref="PcapngWriter"/> timestamp conversion helpers.
/// </summary>
internal sealed class PcapngWriterTests
{
    [Test]
    public async Task ConvertTimestampNanosecondsPassesThrough()
    {
        Timestamp timestamp = new(1_234_567_890L);

        ulong converted = PcapngWriter.ConvertTimestamp(timestamp, PcapngWriter.TsResolNanoseconds);

        await Assert.That(converted).IsEqualTo(1_234_567_890UL);
    }

    [Test]
    public async Task ConvertTimestampFinerResolutionSaturatesToUlongMax()
    {
        Timestamp timestamp = new(long.MaxValue);

        ulong converted = PcapngWriter.ConvertTimestamp(timestamp, 10);

        await Assert.That(converted).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task ConvertTimestampAtTheBuilderMaximumResolutionUsesTheTable()
    {
        Timestamp timestamp = new(1L);

        ulong converted = PcapngWriter.ConvertTimestamp(timestamp, 19);

        await Assert.That(converted).IsEqualTo(10_000_000_000UL);
    }

    [Test]
    public async Task ConvertTimestampBeyondTheTableIsAnInvariantViolation()
    {
        Timestamp timestamp = new(1L);

        // Resolution 28 would need 10^19, which the table (and the builder range 0..19) never asks for.
        await Assert.That(() => PcapngWriter.ConvertTimestamp(timestamp, 28)).Throws<System.Diagnostics.UnreachableException>();
        await Assert.That(PcapngWriter.ConvertTimestamp(timestamp, 27)).IsEqualTo(1_000_000_000_000_000_000UL);
    }

    [Test]
    public async Task TryBlockLengthNearIntMaxReturnsFalse()
    {
        bool ok = PcapngWriter.TryBlockLength(int.MaxValue - 1, out uint blockLength);

        await Assert.That(ok).IsFalse();
        await Assert.That(blockLength).IsEqualTo(0u);
    }

    [Test]
    public async Task TryBlockLengthFourBytePayloadIs36()
    {
        bool ok = PcapngWriter.TryBlockLength(4, out uint blockLength);

        await Assert.That(ok).IsTrue();
        await Assert.That(blockLength).IsEqualTo(36u);
    }
}
