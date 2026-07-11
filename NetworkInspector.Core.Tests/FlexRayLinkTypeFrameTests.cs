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
}
