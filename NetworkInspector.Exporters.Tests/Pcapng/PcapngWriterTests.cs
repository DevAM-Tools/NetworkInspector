// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pcapng;

/// <summary>
/// Tests for <see cref="PcapngWriter"/> timestamp conversion helpers.
/// </summary>
internal sealed class PcapngWriterTests
{
    [Test]
    public async Task ConvertTimestamp_Nanoseconds_PassesThrough()
    {
        Timestamp timestamp = new(1_234_567_890L);

        ulong converted = PcapngWriter.ConvertTimestamp(timestamp, PcapngWriter.TsResolNanoseconds);

        await Assert.That(converted).IsEqualTo(1_234_567_890UL);
    }

    [Test]
    public async Task ConvertTimestamp_FinerResolution_SaturatesToUlongMax()
    {
        Timestamp timestamp = new(long.MaxValue);

        ulong converted = PcapngWriter.ConvertTimestamp(timestamp, 10);

        await Assert.That(converted).IsEqualTo(ulong.MaxValue);
    }

    [Test]
    public async Task TryBlockLength_NearIntMax_ReturnsFalse()
    {
        bool ok = PcapngWriter.TryBlockLength(int.MaxValue - 1, out uint blockLength);

        await Assert.That(ok).IsFalse();
        await Assert.That(blockLength).IsEqualTo(0u);
    }

    [Test]
    public async Task TryBlockLength_FourBytePayload_Is36()
    {
        bool ok = PcapngWriter.TryBlockLength(4, out uint blockLength);

        await Assert.That(ok).IsTrue();
        await Assert.That(blockLength).IsEqualTo(36u);
    }
}
