// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Tests for <see cref="Collections.ChunkedGrowOnlyStore{T}"/> and related types.</summary>
internal sealed class ChunkedGrowOnlyStoreTests
{
    private readonly record struct KeyedItem(int Key, int Payload) : Collections.ISortKeyed
    {
        public int SortKey => Key;
    }

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
    public async Task ValueStore_SetGet_Roundtrip()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);

        store.Set(0, 11);
        store.Set(17, 22);

        await Assert.That(store.Get(0)).IsEqualTo(11);
        await Assert.That(store.Get(17)).IsEqualTo(22);
        await Assert.That(store.Get(1)).IsEqualTo(0);
    }

    [Test]
    public async Task PackedStore_Count_NewStore_IsZero()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);

        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PackedStore_Append_AcrossChunkBoundary_RoundtripsAllEntries()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);

        for (int i = 0; i < 40; i++)
        {
            store.Append(new KeyedItem(i, i * 31));
        }

        await Assert.That(store.Count).IsEqualTo(40);
        for (int i = 0; i < 40; i++)
        {
            KeyedItem item = store.ItemRef(i);
            await Assert.That(item.Key).IsEqualTo(i);
            await Assert.That(item.Payload).IsEqualTo(i * 31);
        }
    }

    [Test]
    public async Task PackedStore_ItemRef_NegativeIndex_Throws()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(0, 1));

        await Assert.That(() => store.ItemRef(-1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackedStore_ItemRef_IndexEqualToCount_Throws()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(0, 1));

        await Assert.That(() => store.ItemRef(1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackedStore_ItemRef_TailMutationByWriter_IsReadable()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(7, 100));

        store.ItemRef(0) = new KeyedItem(7, 200);

        await Assert.That(store.ItemRef(0).Payload).IsEqualTo(200);
    }

    [Test]
    public async Task PackedStore_BinarySearch_EmptyStore_ReturnsMinusOne()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);

        await Assert.That(store.BinarySearch(0)).IsEqualTo(-1);
    }

    [Test]
    public async Task PackedStore_BinarySearch_FirstMiddleLast_FindsIndices()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(2, 0));
        store.Append(new KeyedItem(5, 1));
        store.Append(new KeyedItem(9, 2));

        await Assert.That(store.BinarySearch(2, static (in KeyedItem e) => e.Key)).IsEqualTo(0);
        await Assert.That(store.BinarySearch(5, static (in KeyedItem e) => e.Key)).IsEqualTo(1);
        await Assert.That(store.BinarySearch(9, static (in KeyedItem e) => e.Key)).IsEqualTo(2);
    }

    [Test]
    public async Task PackedStore_BinarySearch_MissingKey_ReturnsMinusOne()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(2, 0));
        store.Append(new KeyedItem(5, 1));
        store.Append(new KeyedItem(9, 2));

        await Assert.That(store.BinarySearch(1, static (in KeyedItem e) => e.Key)).IsEqualTo(-1);
        await Assert.That(store.BinarySearch(4, static (in KeyedItem e) => e.Key)).IsEqualTo(-1);
        await Assert.That(store.BinarySearch(10, static (in KeyedItem e) => e.Key)).IsEqualTo(-1);
    }

    [Test]
    public async Task PackedStore_Clear_ResetsCountAndDropsEntries()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(0, 1));
        store.Append(new KeyedItem(1, 2));

        store.Clear();

        await Assert.That(store.Count).IsEqualTo(0);
        await Assert.That(store.BinarySearch(0)).IsEqualTo(-1);
        await Assert.That(() => store.ItemRef(0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackedStore_BinarySearch_NullGetter_Throws()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);

        await Assert.That(() => store.BinarySearch(0, null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PackedStore_BinarySearchExtension_NullStore_Throws()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem>? store = null;

        await Assert.That(() => store!.BinarySearch(0)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task PackedStore_Constructor_InvalidChunkShift_Throws()
    {
        await Assert
            .That(() => new Collections.ChunkedAppendOnlyStore<KeyedItem>(chunkShift: 3))
            .Throws<ArgumentOutOfRangeException>();

        await Assert
            .That(() => new Collections.ChunkedAppendOnlyStore<KeyedItem>(chunkShift: 21))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackedStore_Append_SingleWriterWithConcurrentReader_PublishedPrefixIsConsistent()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        const int total = 4096;
        bool torn = false;

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                store.Append(new KeyedItem(i, i * 31));
            }
        });
        Task reader = Task.Run(() =>
        {
            // Re-scan the published prefix until the writer finishes; every published entry must be
            // fully visible (no default/torn structs behind the volatile count).
            while (true)
            {
                int count = store.Count;
                for (int i = 0; i < count; i++)
                {
                    KeyedItem item = store.ItemRef(i);
                    if (item.Key != i || item.Payload != i * 31)
                    {
                        torn = true;
                        return;
                    }
                }

                if (count == total)
                {
                    return;
                }
            }
        });
        await Task.WhenAll(writer, reader);

        await Assert.That(torn).IsFalse();
    }

    [Test]
    public async Task TryGet_InvalidIndex_ReturnsFalseAndUnset()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4, unsetValue: -1);

        await Assert.That(store.TryGet(-1, out int negative)).IsFalse();
        await Assert.That(negative).IsEqualTo(-1);
        await Assert.That(store.TryGet(Ids.ArrayIndexIdRange.MaxValue + 1, out int beyond)).IsFalse();
        await Assert.That(beyond).IsEqualTo(-1);
        await Assert.That(store.TryGet(0, out int missing)).IsFalse();
        await Assert.That(missing).IsEqualTo(-1);
    }

    [Test]
    public async Task TryGet_AllocatedSlot_ReturnsTrue()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4, unsetValue: -1);
        store.Set(0, 5);

        await Assert.That(store.TryGet(0, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(5);
    }

    [Test]
    public async Task ValueStore_ConcurrentDisjointSets_BothValuesReadable()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);

        for (int attempt = 0; attempt < 64; attempt++)
        {
            store.Clear();
            Task first = Task.Run(() => store.Set(0, 10));
            Task second = Task.Run(() => store.Set(272, 42));
            await Task.WhenAll(first, second);

            await Assert.That(store.Get(0)).IsEqualTo(10);
            await Assert.That(store.Get(272)).IsEqualTo(42);
        }
    }

    [Test]
    public async Task PackedStore_BinarySearch_DuplicateKeys_ReturnsSomeMatchingIndex()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(2, 0));
        store.Append(new KeyedItem(2, 1));

        int index = store.BinarySearch(2, static (in KeyedItem e) => e.Key);

        await Assert.That(index == 0 || index == 1).IsTrue();
        await Assert.That(store.ItemRef(index).Key).IsEqualTo(2);
    }

    [Test]
    public async Task Append_ConcurrentSecondWriter_ThrowsInvalidOperationException()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        const int total = 4096;
        int ready = 0;
        int threw = 0;

        void Writer(int keyBase)
        {
            _ = Interlocked.Increment(ref ready);
            while (Volatile.Read(ref ready) < 2)
            {
            }

            for (int i = 0; i < total; i++)
            {
                try
                {
                    store.Append(new KeyedItem(keyBase + i, i));
                }
                catch (InvalidOperationException)
                {
                    _ = Interlocked.Exchange(ref threw, 1);
                    return;
                }
            }
        }

        Task first = Task.Run(() => Writer(0));
        Task second = Task.Run(() => Writer(1_000_000));
        await Task.WhenAll(first, second);

        await Assert.That(Volatile.Read(ref threw)).IsEqualTo(1);
    }

    [Test]
    public async Task PackedStore_TryReadPublished_AfterClear_ReturnsFalse()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        store.Append(new KeyedItem(0, 1));

        store.Clear();

        await Assert.That(store.TryReadPublished(0, out _)).IsFalse();
    }

    [Test]
    public async Task Clear_ThenRefillDisjointKeys_ConcurrentReadersSearchingOldKey_SeeMiss()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        const int rounds = 32;
        const int newKeysPerRound = 64;
        int hit = 0;
        int finished = 0;
        int disjointPhase = 0;

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i <= 20; i++)
            {
                store.Append(new KeyedItem(i, i));
            }

            store.Clear();
            Volatile.Write(ref disjointPhase, 1);

            for (int round = 0; round < rounds; round++)
            {
                for (int i = 0; i < newKeysPerRound; i++)
                {
                    store.Append(new KeyedItem(1_000_000 + i, i));
                }

                store.Clear();
            }

            Volatile.Write(ref finished, 1);
        });

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                while (Volatile.Read(ref finished) == 0)
                {
                    int phase = Volatile.Read(ref disjointPhase);
                    int index = store.BinarySearch(10, static (in KeyedItem e) => e.Key);
                    if (phase == 1 && index >= 0)
                    {
                        _ = Interlocked.Exchange(ref hit, 1);
                        return;
                    }
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);
        await Assert.That(Volatile.Read(ref hit)).IsEqualTo(0);
        await Assert.That(store.BinarySearch(10, static (in KeyedItem e) => e.Key)).IsEqualTo(-1);
    }

    [Test]
    public async Task ReadRange_FromIndexNearMaxValue_DoesNotTreatOverflowAsHoles()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Set(0, value);

        object?[] buffer = new object?[4];
        int read = store.ReadRange(Ids.ArrayIndexIdRange.MaxValue - 1, buffer);

        await Assert.That(read).IsEqualTo(2);
        await Assert.That(buffer[0]).IsNull();
        await Assert.That(buffer[1]).IsNull();
        await Assert.That(buffer[2]).IsNotSameReferenceAs(value);
        await Assert.That(buffer[3]).IsNotSameReferenceAs(value);
        await Assert.That(store.Get(0)).IsSameReferenceAs(value);
    }

    [Test]
    public async Task ReadRange_NullStore_Throws()
    {
        Collections.ChunkedGrowOnlyStore<object>? store = null;
        object?[] buffer = new object?[1];

        await Assert.That(() => store!.ReadRange(0, buffer)).Throws<ArgumentNullException>();
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

    [Test]
    public async Task ChunkedOuterArray_GetOrAllocateChunk_ChunkIndexBeyondMax_Throws()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);

        await Assert
            .That(() => outer.GetOrAllocateChunk(0x10000000, () => new object[16]))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ValueStore_ConcurrentDisjointSets_SameChunk_BothValuesReadable()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);

        for (int attempt = 0; attempt < 64; attempt++)
        {
            store.Clear();
            Task first = Task.Run(() => store.Set(0, 10));
            Task second = Task.Run(() => store.Set(1, 11));
            await Task.WhenAll(first, second);

            await Assert.That(store.Get(0)).IsEqualTo(10);
            await Assert.That(store.Get(1)).IsEqualTo(11);
        }
    }
}
