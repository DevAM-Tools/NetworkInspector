// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SlabAllocator{T}"/>: allocation success/failure,
/// boundary conditions, and negative count guard.
/// </summary>
internal sealed class SlabAllocatorTests
{
    [Test]
    public async Task TryAllocate_NegativeCount_ThrowsArgumentOutOfRangeException()
    {
        SlabAllocator<int> sut = new(64);
        await Assert.That(() => sut.TryAllocate(-1, out _, out _))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TryAllocate_LargeNegativeCount_ThrowsArgumentOutOfRangeException()
    {
        SlabAllocator<int> sut = new(64);
        await Assert.That(() => sut.TryAllocate(int.MinValue, out _, out _))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TryAllocate_ZeroCount_Succeeds()
    {
        SlabAllocator<int> sut = new(64);
        bool result = sut.TryAllocate(0, out int[] buffer, out int offset);

        await Assert.That(result).IsTrue();
        await Assert.That(offset).IsEqualTo(0);
        await Assert.That(buffer).IsNotNull();
    }

    [Test]
    public async Task TryAllocate_PositiveCount_Succeeds()
    {
        SlabAllocator<int> sut = new(64);
        bool result = sut.TryAllocate(32, out int[] buffer, out int offset);

        await Assert.That(result).IsTrue();
        await Assert.That(offset).IsEqualTo(0);
        await Assert.That(buffer.Length).IsEqualTo(64);
    }

    [Test]
    public async Task TryAllocate_ExceedsCapacity_ReturnsFalse()
    {
        SlabAllocator<int> sut = new(64);
        sut.TryAllocate(32, out _, out _);
        
        bool result = sut.TryAllocate(64, out _, out _);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TryAllocate_MultipleAllocations_UseCorrectOffsets()
    {
        SlabAllocator<int> sut = new(100);
        
        bool result1 = sut.TryAllocate(10, out int[] buffer1, out int offset1);
        bool result2 = sut.TryAllocate(20, out int[] buffer2, out int offset2);
        bool result3 = sut.TryAllocate(30, out int[] buffer3, out int offset3);

        await Assert.That(result1).IsTrue();
        await Assert.That(result2).IsTrue();
        await Assert.That(result3).IsTrue();
        await Assert.That(offset1).IsEqualTo(0);
        await Assert.That(offset2).IsEqualTo(10);
        await Assert.That(offset3).IsEqualTo(30);
        // All buffers should reference the same backing array
        await Assert.That(buffer1).IsEqualTo(buffer2);
        await Assert.That(buffer2).IsEqualTo(buffer3);
    }
}
