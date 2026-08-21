// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="FlexRayLinkTypeFrame"/> wire-format and dispatch-key helpers.
/// </summary>
internal sealed class FlexRayLinkTypeFrameTests
{
    [Test]
    public async Task EncodeDispatchKey_SlotChannelCycle_RoundTrips()
    {
        ulong key = FlexRayLinkTypeFrame.EncodeDispatchKey(frameId: 42, channelB: true, cycle: 7);
        FlexRayLinkTypeFrame.DecodeDispatchKey(key, out ushort frameId, out bool channelB, out byte cycle);

        await Assert.That(frameId).IsEqualTo((ushort)42);
        await Assert.That(channelB).IsTrue();
        await Assert.That(cycle).IsEqualTo((byte)7);
    }

    [Test]
    public async Task EncodeDispatchKey_ChannelA_CycleZero_MatchesSlotOnly()
    {
        ulong key = FlexRayLinkTypeFrame.EncodeDispatchKey(frameId: 42, channelB: false, cycle: 0);
        await Assert.That(key).IsEqualTo(42UL);
    }

    [Test]
    public async Task EncodeDispatchKey_ChannelB_CycleZero_MatchesLegacyKey()
    {
        ulong key = FlexRayLinkTypeFrame.EncodeDispatchKey(frameId: 42, channelB: true, cycle: 0);
        await Assert.That(key).IsEqualTo(42UL | FlexRayLinkTypeFrame.ChannelBKeyBit);
    }

    [Test]
    public async Task BuildFrame_TryParseDataFrame_RoundTripsFields()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(
            channelB: true,
            frameId: 100,
            cycle: 15,
            headerCrc: 0x5A3,
            payload,
            sfi: true,
            ppi: true);

        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out FlexRayLinkTypeFrame.Fields fields, out ReadOnlySpan<byte> parsedPayload);
        byte[] parsedBytes = parsedPayload.ToArray();

        await Assert.That(ok).IsTrue();
        await Assert.That(fields.ChannelB).IsTrue();
        await Assert.That(fields.FrameId).IsEqualTo((ushort)100);
        await Assert.That(fields.Cycle).IsEqualTo((byte)15);
        await Assert.That(fields.HeaderCrc).IsEqualTo((ushort)0x5A3);
        await Assert.That(fields.Sfi).IsTrue();
        await Assert.That(fields.Ppi).IsTrue();
        await Assert.That(parsedBytes).IsEquivalentTo(payload);
    }

    [Test]
    public async Task AscChannelMapping_MapsOneAndTwo()
    {
        await Assert.That(FlexRayLinkTypeFrame.AscChannelToBusChannel(1)).IsFalse();
        await Assert.That(FlexRayLinkTypeFrame.AscChannelToBusChannel(2)).IsTrue();
        await Assert.That(FlexRayLinkTypeFrame.BusChannelToAscChannel(false)).IsEqualTo(1);
        await Assert.That(FlexRayLinkTypeFrame.BusChannelToAscChannel(true)).IsEqualTo(2);
    }

    [Test]
    public async Task EncodeDispatchKey_FrameIdTooLarge_Throws()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.EncodeDispatchKey(0x800, false, 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EncodeDispatchKey_CycleTooLarge_Throws()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.EncodeDispatchKey(1, false, 64))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildFrame_FrameIdTooLarge_Throws()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 2048, 0, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildFrame_CycleTooLarge_Throws()
    {
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 1, 64, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildFrame_PayloadTooLarge_Throws()
    {
        byte[] payload = new byte[FlexRayLinkTypeFrame.MaxPayloadBytes + 1];
        await Assert.That(() => FlexRayLinkTypeFrame.BuildFrame(false, 1, 0, 0, payload))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task MapLegacyTypeFlags_AllCombinations()
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
    public async Task MapBlfFrameFlags_AllCombinations()
    {
        FlexRayLinkTypeFrame.MapBlfFrameFlags(0x0D, out bool ppi, out bool nfi, out bool sfi, out bool stfi);
        await Assert.That(ppi).IsTrue();
        await Assert.That(nfi).IsTrue();
        await Assert.That(sfi).IsTrue();
        await Assert.That(stfi).IsTrue();

        FlexRayLinkTypeFrame.MapBlfFrameFlags(0x02, out ppi, out nfi, out sfi, out stfi);
        await Assert.That(ppi).IsFalse();
        await Assert.That(nfi).IsFalse();
    }

    [Test]
    public async Task MapBlfHeaderBitMask_AllCombinations()
    {
        FlexRayLinkTypeFrame.MapBlfHeaderBitMask(0x1A, out bool ppi, out bool nfi, out bool sfi, out bool stfi);
        await Assert.That(ppi).IsTrue();
        await Assert.That(nfi).IsTrue();
        await Assert.That(sfi).IsTrue();
        await Assert.That(stfi).IsTrue();

        FlexRayLinkTypeFrame.MapBlfHeaderBitMask(0x04, out ppi, out nfi, out sfi, out stfi);
        await Assert.That(ppi).IsFalse();
        await Assert.That(nfi).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrame_BufferOneByteShorterThanHeader_ReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01, 0x02, 0x03, 0x04]);
        ReadOnlySpan<byte> tooShort = frame.AsSpan(0, FlexRayLinkTypeFrame.MinHeaderSize - 1);
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(tooShort, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrame_TruncatedBuffer_ReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01, 0x02, 0x03, 0x04]);
        ReadOnlySpan<byte> truncated = frame.AsSpan(0, FlexRayLinkTypeFrame.MinHeaderSize);
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(truncated, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrame_WrongTypeIndex_ReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01], typeIndex: 0x02);
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryParseDataFrame_ShortPayload_ReturnsFalse()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 10, 3, 0, [0x01, 0x02]);
        frame[4] = (byte)((8 << 1) | (frame[4] & 0x01));
        bool ok = FlexRayLinkTypeFrame.TryParseDataFrame(frame, out _, out _);
        await Assert.That(ok).IsFalse();
    }
}
