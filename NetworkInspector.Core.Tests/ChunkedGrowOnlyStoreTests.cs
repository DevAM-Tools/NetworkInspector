// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Tests for <see cref="Collections.ChunkedGrowOnlyStore{T}"/> and related types.</summary>
internal sealed class ChunkedGrowOnlyStoreTests
{
    [Test]
    public async Task ReferenceStore_SetGet_Roundtrip()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();

        store.Set(0, value);
        store.Set(17, value);
        store.Set(16, value);

        await Assert.That(store.Get(0)).IsSameReferenceAs(value);
        await Assert.That(store.Get(16)).IsSameReferenceAs(value);
        await Assert.That(store.Get(17)).IsSameReferenceAs(value);
        await Assert.That(store.Get(1)).IsNull();
    }

    [Test]
    public async Task ReferenceStore_SetOutOfRange_Throws()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);

        await Assert
            .That(() => store.Set(Ids.ArrayIndexIdRange.MaxValue + 1, new()))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task LongStore_TryGet_UnsetReturnsFalse()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);

        store.Set(3, 42L);

        await Assert.That(store.TryGet(3, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(42L);
        await Assert.That(store.TryGet(4, out _)).IsFalse();
    }

    [Test]
    public async Task ReadRange_NegativeFromIndex_FillsNulls()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Set(0, value);

        object?[] buffer = new object?[3];
        int read = store.ReadRange(-1, buffer);

        await Assert.That(read).IsEqualTo(3);
        await Assert.That(buffer[0]).IsNull();
        await Assert.That(buffer[1]).IsSameReferenceAs(value);
        await Assert.That(buffer[2]).IsNull();
    }

    [Test]
    public async Task ChunkShift_Invalid_ThrowsOnConstruction()
    {
        await Assert
            .That(() => new Collections.ChunkedGrowOnlyStore<object>(chunkShift: 2))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReferenceStore_Get_InvalidIndex_ReturnsNull()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Set(0, value);

        await Assert.That(store.Get(-1)).IsNull();
        await Assert.That(store.Get(Ids.ArrayIndexIdRange.MaxValue + 1)).IsNull();
        await Assert.That(store.Get(256)).IsNull();
        await Assert.That(store.Get(16)).IsNull();
    }

    [Test]
    public async Task ReferenceStore_Clear_DropsAllValues()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Set(0, value);
        store.Set(17, value);

        store.Clear();

        await Assert.That(store.Get(0)).IsNull();
        await Assert.That(store.Get(17)).IsNull();
    }

    [Test]
    public async Task LongStore_Constructor_InvalidChunkShiftAboveMax_Throws()
    {
        await Assert
            .That(() => new Collections.ChunkedGrowOnlyLongStore(chunkShift: 21))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task LongStore_TryGet_InvalidIndex_ReturnsFalseWithSentinel()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -99L);

        await Assert.That(store.TryGet(-1, out long negativeValue)).IsFalse();
        await Assert.That(negativeValue).IsEqualTo(-99L);
        await Assert.That(store.TryGet(Ids.ArrayIndexIdRange.MaxValue + 1, out long beyondValue)).IsFalse();
        await Assert.That(beyondValue).IsEqualTo(-99L);
    }

    [Test]
    public async Task LongStore_TryGet_BeyondAllocatedChunks_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(0, 10L);

        await Assert.That(store.TryGet(256, out long value)).IsFalse();
        await Assert.That(value).IsEqualTo(-1L);
    }

    [Test]
    public async Task LongStore_TryGet_UnallocatedChunk_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(0, 10L);

        await Assert.That(store.TryGet(16, out long value)).IsFalse();
        await Assert.That(value).IsEqualTo(-1L);
    }

    [Test]
    public async Task LongStore_TryGet_UnsetSentinelValue_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(5, -1L);

        await Assert.That(store.TryGet(5, out long value)).IsFalse();
        await Assert.That(value).IsEqualTo(-1L);
    }

    [Test]
    public async Task LongStore_Clear_DropsAllValues()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(3, 42L);

        store.Clear();

        await Assert.That(store.TryGet(3, out _)).IsFalse();
    }

    [Test]
    public async Task ChunkedOuterArray_Constructor_InvalidChunkShift_Throws()
    {
        await Assert
            .That(() => new Collections.ChunkedOuterArray<object[]>(chunkShift: 3))
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(() => new Collections.ChunkedOuterArray<object[]>(chunkShift: 21))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ChunkedOuterArray_Properties_MatchChunkShift()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 8);

        await Assert.That(outer.ChunkShift).IsEqualTo(8);
        await Assert.That(outer.ChunkSize).IsEqualTo(256);
        await Assert.That(outer.ChunkMask).IsEqualTo(255);
    }

    [Test]
    public async Task ChunkedOuterArray_DecomposeIndex_SplitsIndex()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        (int chunkIndex, int slotIndex) = outer.DecomposeIndex(37);

        await Assert.That(chunkIndex).IsEqualTo(2);
        await Assert.That(slotIndex).IsEqualTo(5);
    }

    [Test]
    public async Task ChunkedOuterArray_GetChunk_UnallocatedAndBeyond_ReturnNull()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[] allocated = outer.GetOrAllocateChunk(0, () => new object[16]);

        await Assert.That(outer.GetChunk(0)).IsSameReferenceAs(allocated);
        await Assert.That(outer.GetChunk(1)).IsNull();
        await Assert.That(outer.GetChunk(100)).IsNull();
    }

    [Test]
    public async Task ChunkedOuterArray_GetOrAllocateChunk_AllocatesAndReuses()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[] first = outer.GetOrAllocateChunk(2, () => new object[16]);
        object[] second = outer.GetOrAllocateChunk(2, () => throw new InvalidOperationException("factory must not run"));

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task ChunkedOuterArray_Clear_DropsAllChunks()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        outer.GetOrAllocateChunk(0, () => new object[16]);

        outer.Clear();

        await Assert.That(outer.GetChunk(0)).IsNull();
    }

    [Test]
    public async Task ChunkedOuterArray_GetOrAllocateChunk_GrowsOuterCapacity()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[] chunk = outer.GetOrAllocateChunk(3, () => new object[16]);

        await Assert.That(outer.GetChunk(3)).IsSameReferenceAs(chunk);
        await Assert.That(outer.GetChunk(0)).IsNull();
    }

    [Test]
    public async Task ReferenceStore_Get_GrownOuterArrayWithNullInnerChunk_ReturnsNull()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Set(272, value);

        await Assert.That(store.Get(16)).IsNull();
        await Assert.That(store.Get(272)).IsSameReferenceAs(value);
    }

    [Test]
    public async Task LongStore_TryGet_GrownOuterArrayWithNullInnerChunk_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(272, 42L);

        await Assert.That(store.TryGet(16, out long value)).IsFalse();
        await Assert.That(value).IsEqualTo(-1L);
    }

    [Test]
    public async Task LongStore_Set_TwoIndicesSameChunk_ReusesInnerChunk()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(0, 10L);
        store.Set(1, 11L);

        await Assert.That(store.TryGet(0, out long first)).IsTrue();
        await Assert.That(first).IsEqualTo(10L);
        await Assert.That(store.TryGet(1, out long second)).IsTrue();
        await Assert.That(second).IsEqualTo(11L);
    }

    [Test]
    public async Task LongStore_Set_SecondWriteSameChunk_ReusesOuterCapacity()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);
        store.Set(0, 1L);
        store.Set(2, 3L);

        await Assert.That(store.TryGet(2, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(3L);
    }

    [Test]
    public async Task ReferenceStore_ConcurrentDisjointSets_BothValuesReadable()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value0 = new();
        object value272 = new();

        for (int attempt = 0; attempt < 64; attempt++)
        {
            store.Clear();
            Task first = Task.Run(() => store.Set(0, value0));
            Task second = Task.Run(() => store.Set(272, value272));
            await Task.WhenAll(first, second);

            await Assert.That(store.Get(0)).IsSameReferenceAs(value0);
            await Assert.That(store.Get(272)).IsSameReferenceAs(value272);
        }
    }

    [Test]
    public async Task LongStore_ConcurrentDisjointSets_BothValuesReadable()
    {
        Collections.ChunkedGrowOnlyLongStore store = new(chunkShift: 4, unsetValue: -1L);

        for (int attempt = 0; attempt < 64; attempt++)
        {
            store.Clear();
            Task first = Task.Run(() => store.Set(0, 10L));
            Task second = Task.Run(() => store.Set(272, 42L));
            await Task.WhenAll(first, second);

            await Assert.That(store.TryGet(0, out long firstValue)).IsTrue();
            await Assert.That(firstValue).IsEqualTo(10L);
            await Assert.That(store.TryGet(272, out long secondValue)).IsTrue();
            await Assert.That(secondValue).IsEqualTo(42L);
        }
    }

    [Test]
    public async Task ChunkedOuterArray_ConcurrentDisjointAllocation_BothChunksReadable()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[] chunk0 = new object[16];
        object[] chunk272 = new object[16];

        for (int attempt = 0; attempt < 64; attempt++)
        {
            outer.Clear();
            Task first = Task.Run(() => outer.GetOrAllocateChunk(0, () => chunk0));
            Task second = Task.Run(() => outer.GetOrAllocateChunk(17, () => chunk272));
            await Task.WhenAll(first, second);

            await Assert.That(outer.GetChunk(0)).IsSameReferenceAs(chunk0);
            await Assert.That(outer.GetChunk(17)).IsSameReferenceAs(chunk272);
        }
    }
}
