// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Exit-path coverage for the Exporters-linked <see cref="FlexRayLinkTypeFrame"/> copy.
/// </summary>
internal sealed class FlexRayLinkTypeFrameExitTests
{
    [Test]
    public async Task EncodeDispatchKeyFrameIdTooLargeThrows()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.EncodeDispatchKey(0x800, false, 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncodeDispatchKeyCycleTooLargeThrows()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.EncodeDispatchKey(1, false, 64))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DecodeDispatchKeyRoundTrips()
    {
        ulong key = FlexRayLinkTypeFrame.EncodeDispatchKey(42, true, 7);
        FlexRayLinkTypeFrame.DecodeDispatchKey(key, out ushort frameId, out bool channelB, out byte cycle);

        await Assert.That(frameId).IsEqualTo((ushort)42);
        await Assert.That(channelB).IsTrue();
        await Assert.That(cycle).IsEqualTo((byte)7);
    }

    [Test]
    public async Task BuildFrameFrameIdTooLargeThrows()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 2048, 0, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildFrameCycleTooLargeThrows()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 1, 64, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildFramePayloadTooLargeThrows()
    {
        byte[] payload = new byte[FlexRayLinkTypeFrame.MaxPayloadBytes + 1];
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 1, 0, 0, payload))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BusChannelToAscChannelMapsBoth()
    {
        await Assert.That(FlexRayLinkTypeFrame.BusChannelToAscChannel(false)).IsEqualTo(1);
        await Assert.That(FlexRayLinkTypeFrame.BusChannelToAscChannel(true)).IsEqualTo(2);
        await Assert.That(FlexRayLinkTypeFrame.AscChannelToBusChannel(1)).IsFalse();
        await Assert.That(FlexRayLinkTypeFrame.AscChannelToBusChannel(2)).IsTrue();
    }

    [Test]
    public async Task MapLegacyTypeFlagsAllCombinations()
    {
        FlexRayLinkTypeFrame.MapLegacyTypeFlags(0xB0, out bool ppi, out bool nfi, out bool sfi, out bool stfi);
        await Assert.That(ppi).IsTrue();
        await Assert.That(nfi).IsTrue();
        await Assert.That(sfi).IsTrue();
        await Assert.That(stfi).IsTrue();

        FlexRayLinkTypeFrame.MapLegacyTypeFlags(0x40, out ppi, out nfi, out sfi, out stfi);
        await Assert.That(ppi).IsFalse();
        await Assert.That(nfi).IsFalse();
        await Assert.That(sfi).IsFalse();
        await Assert.That(stfi).IsFalse();
    }

    [Test]
    public async Task MapBlfFrameFlagsAllCombinations()
    {
        FlexRayLinkTypeFrame.MapBlfFrameFlags(0x1C, out bool ppi, out bool nfi, out bool sfi, out bool stfi);
        await Assert.That(ppi).IsTrue();
        await Assert.That(nfi).IsTrue();
        await Assert.That(sfi).IsTrue();
        await Assert.That(stfi).IsTrue();

        FlexRayLinkTypeFrame.MapBlfFrameFlags(0x01, out ppi, out nfi, out sfi, out stfi);
        await Assert.That(ppi).IsFalse();
        await Assert.That(nfi).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrameBufferTooShortReturnsFalse()
    {
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(new byte[FlexRayLinkTypeFrame.MinHeaderSize - 1], out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrameWrongTypeIndexReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01], typeIndex: 0x02);
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrameTruncatedPayloadReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01, 0x02]);
        frame[4] = (byte)((8 << 1) | (frame[4] & 0x01));
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out _, out _);
        await Assert.That(ok).IsFalse();
    }
}
