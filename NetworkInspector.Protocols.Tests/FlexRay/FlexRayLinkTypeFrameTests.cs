// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="FlexRayLinkTypeFrame.BuildFrame"/>: padding of odd payloads and the argument ranges.
/// </summary>
internal sealed class FlexRayLinkTypeFrameTests
{
    [Test]
    public async Task OddPayloadIsPaddedWithAZeroByteAndCountedInWords()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 5, 1, 0, [1, 2, 3]);

        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize + 4);
        await Assert.That(frame[FlexRayLinkTypeFrame.MinHeaderSize + 2]).IsEqualTo((byte)3);
        await Assert.That(frame[FlexRayLinkTypeFrame.MinHeaderSize + 3]).IsEqualTo((byte)0);
        await Assert.That((frame[4] >> 1) & 0x7F).IsEqualTo(2);
    }

    [Test]
    public async Task EvenPayloadIsNotPadded()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 5, 1, 0, [1, 2, 3, 4]);

        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize + 4);
        await Assert.That(frame[FlexRayLinkTypeFrame.MinHeaderSize + 3]).IsEqualTo((byte)4);
    }

    [Test]
    public async Task EmptyPayloadGivesTheHeaderOnly()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(true, 0, 0, 0, ReadOnlySpan<byte>.Empty);

        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
    }

    [Test]
    public async Task SlotAboveTheElevenBitRangeThrows()
    {
        await Assert.That(static () => FlexRayLinkTypeFrame.BuildFrame(false, 0x800, 0, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task HighestSlotIsAccepted()
    {
        byte[] frame = FlexRayLinkTypeFrame.BuildFrame(false, 0x7FF, 0, 0, ReadOnlySpan<byte>.Empty);

        await Assert.That(frame.Length).IsEqualTo(FlexRayLinkTypeFrame.MinHeaderSize);
    }

    [Test]
    public async Task CycleAboveSixtyThreeThrows()
    {
        await Assert.That(static () => FlexRayLinkTypeFrame.BuildFrame(false, 1, 64, 0, ReadOnlySpan<byte>.Empty))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PayloadAboveTheMaximumThrows()
    {
        await Assert.That(static () => FlexRayLinkTypeFrame.BuildFrame(false, 1, 1, 0, new byte[FlexRayLinkTypeFrame.MaxPayloadBytes + 1]))
            .Throws<ArgumentOutOfRangeException>();
    }
}
