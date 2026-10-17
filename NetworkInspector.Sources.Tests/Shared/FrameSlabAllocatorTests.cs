// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// Tests for <see cref="FrameSlabAllocator"/>: option validation, slab sharing, rollover, the big-frame bypass, and the
/// helpers the frame parsers use.
/// </summary>
internal sealed class FrameSlabAllocatorTests
{
    #region Option validation

    [Test]
    [Arguments(0)]
    [Arguments(FrameSlabAllocator.MinSlabSize)]
    [Arguments(64 * 1024)]
    [Arguments(FrameSlabAllocator.MaxSlabSize)]
    public async Task ValidateOptionAcceptsOffAndTheRange(int slabSize)
    {
        await Assert.That(FrameSlabAllocator.ValidateOption(slabSize)).IsEqualTo(slabSize);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(1)]
    [Arguments(FrameSlabAllocator.MinSlabSize - 1)]
    [Arguments(FrameSlabAllocator.MaxSlabSize + 1)]
    [Arguments(int.MaxValue)]
    public async Task ValidateOptionRejectsValuesOutsideTheRange(int slabSize)
    {
        await Assert.That(() => FrameSlabAllocator.ValidateOption(slabSize)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OptionSettersValidateTheSlabSize()
    {
        await Assert.That(() => new PcapngSourceOptions { FrameSlabSize = 100 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new BlfSourceOptions { FrameSlabSize = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new AscSourceOptions { FrameSlabSize = FrameSlabAllocator.MaxSlabSize + 1 })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(new PcapngSourceOptions().FrameSlabSize).IsEqualTo(0);
    }

    [Test]
    public async Task CreateOrNullReturnsNullWhenOff()
    {
        await Assert.That(FrameSlabAllocator.CreateOrNull(0)).IsNull();
        await Assert.That(FrameSlabAllocator.CreateOrNull(4096)!.SlabSize).IsEqualTo(4096);
    }

    #endregion

    #region Allocate

    [Test]
    public async Task ConsecutiveSmallFramesShareOneSlab()
    {
        FrameSlabAllocator slabs = new(4096);

        Memory<byte> first = slabs.Allocate(100);
        Memory<byte> second = slabs.Allocate(100);

        bool firstIsArray = MemoryMarshal.TryGetArray<byte>(first, out ArraySegment<byte> a);
        bool secondIsArray = MemoryMarshal.TryGetArray<byte>(second, out ArraySegment<byte> b);
        await Assert.That(firstIsArray && secondIsArray).IsTrue();
        await Assert.That(b.Array).IsSameReferenceAs(a.Array);
        await Assert.That(a.Array!.Length).IsEqualTo(4096);

        // The regions must not overlap: the second starts at or after the end of the first.
        await Assert.That(b.Offset).IsGreaterThanOrEqualTo(a.Offset + a.Count);
        await Assert.That(first.Length).IsEqualTo(100);
        await Assert.That(second.Length).IsEqualTo(100);
    }

    [Test]
    public async Task ExhaustedSlabRollsOverToAFreshOne()
    {
        FrameSlabAllocator slabs = new(FrameSlabAllocator.MinSlabSize);
        int frameLength = FrameSlabAllocator.MinSlabSize / FrameSlabAllocator.BypassDivisor;

        // Four quarter-slab frames fill the slab exactly; the fifth needs a new slab.
        byte[]? firstSlab = null;
        for (int i = 0; i < FrameSlabAllocator.BypassDivisor; i++)
        {
            _ = MemoryMarshal.TryGetArray<byte>(slabs.Allocate(frameLength), out ArraySegment<byte> segment);
            firstSlab ??= segment.Array;
            await Assert.That(segment.Array).IsSameReferenceAs(firstSlab);
        }

        _ = MemoryMarshal.TryGetArray<byte>(slabs.Allocate(frameLength), out ArraySegment<byte> next);
        await Assert.That(next.Array).IsNotSameReferenceAs(firstSlab);
        await Assert.That(next.Array!.Length).IsEqualTo(FrameSlabAllocator.MinSlabSize);
    }

    [Test]
    public async Task FrameAboveAQuarterSlabGetsItsOwnArray()
    {
        FrameSlabAllocator slabs = new(FrameSlabAllocator.MinSlabSize);
        int bigLength = (FrameSlabAllocator.MinSlabSize / FrameSlabAllocator.BypassDivisor) + 1;

        _ = MemoryMarshal.TryGetArray<byte>(slabs.Allocate(10), out ArraySegment<byte> small);
        _ = MemoryMarshal.TryGetArray<byte>(slabs.Allocate(bigLength), out ArraySegment<byte> big);
        _ = MemoryMarshal.TryGetArray<byte>(slabs.Allocate(10), out ArraySegment<byte> afterBig);

        // The big frame owns an exact-size array and does not consume or replace the current slab.
        await Assert.That(big.Array!.Length).IsEqualTo(bigLength);
        await Assert.That(big.Array).IsNotSameReferenceAs(small.Array);
        await Assert.That(afterBig.Array).IsSameReferenceAs(small.Array);
    }

    [Test]
    public async Task ZeroLengthIsEmptyAndNegativeLengthThrows()
    {
        FrameSlabAllocator slabs = new(FrameSlabAllocator.MinSlabSize);

        await Assert.That(slabs.Allocate(0).Length).IsEqualTo(0);
        await Assert.That(() => slabs.Allocate(-1)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Parser helpers

    [Test]
    public async Task RentZeroedReturnsZeroFilledRegionsWithAndWithoutSlabs()
    {
        FrameSlabAllocator slabs = new(FrameSlabAllocator.MinSlabSize);

        // Dirty the slab first so a missing clear would show up in the second region.
        slabs.Allocate(64).Span.Fill(0xFF);
        Memory<byte> fromSlab = FrameSlabAllocator.RentZeroed(slabs, 32);
        Memory<byte> fromArray = FrameSlabAllocator.RentZeroed(null, 32);

        await Assert.That(fromSlab.Length).IsEqualTo(32);
        await Assert.That(fromArray.Length).IsEqualTo(32);
        await Assert.That(fromSlab.Span.ContainsAnyExcept((byte)0)).IsFalse();
        await Assert.That(fromArray.Span.ContainsAnyExcept((byte)0)).IsFalse();
    }

    [Test]
    public async Task ToArrayReusesAWholeArrayAndCopiesASlice()
    {
        byte[] whole = [1, 2, 3, 4];

        bool wholeOk = FrameSlabAllocator.ToArray(true, whole, out byte[] same);
        bool sliceOk = FrameSlabAllocator.ToArray(true, whole.AsMemory(1, 2), out byte[] copy);
        bool failed = FrameSlabAllocator.ToArray(false, whole, out byte[] empty);

        await Assert.That(wholeOk).IsTrue();
        await Assert.That(same).IsSameReferenceAs(whole);
        await Assert.That(sliceOk).IsTrue();
        await Assert.That(copy).IsEquivalentTo(new byte[] { 2, 3 });
        await Assert.That(failed).IsFalse();
        await Assert.That(empty.Length).IsEqualTo(0);
    }

    #endregion
}
