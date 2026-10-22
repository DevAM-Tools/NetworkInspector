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
    public async Task ReferenceStore_AppendGet_Roundtrip()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object first = new();
        object second = new();
        object third = new();

        store.Append(first);
        store.Append(second);
        store.Append(third);

        await Assert.That(store.Count).IsEqualTo(3);
        await Assert.That(store.Get(0)).IsSameReferenceAs(first);
        await Assert.That(store.Get(1)).IsSameReferenceAs(second);
        await Assert.That(store.Get(2)).IsSameReferenceAs(third);
        await Assert.That(store.Get(3)).IsNull();
    }

    [Test]
    public async Task ReferenceStore_AppendPastIndexRange_Throws()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        _SetPublishedCount(store, Ids.ArrayIndexIdRange.MaxValue + 1);

        await Assert
            .That(() => store.Append(new()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LongStore_TryGet_UnpublishedIndex_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);

        store.Append(42L);

        await Assert.That(store.Count).IsEqualTo(1);
        await Assert.That(store.TryGet(0, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(42L);
        await Assert.That(store.TryGet(1, out _)).IsFalse();
    }

    [Test]
    public async Task ReadRange_NegativeFromIndex_FillsNulls()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Append(value);

        object?[] buffer = new object?[3];
        int read = store.ReadRange(-1, buffer);

        await Assert.That(read).IsEqualTo(3);
        await Assert.That(buffer[0]).IsNull();
        await Assert.That(buffer[1]).IsSameReferenceAs(value);
        await Assert.That(buffer[2]).IsNull();
    }

    [Test]
    public async Task ReadRangeFromIndexMinValueFillsNulls()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        store.Append(value);

        object?[] buffer = new object?[4];
        int read = store.ReadRange(int.MinValue, buffer);

        await Assert.That(read).IsEqualTo(4);
        await Assert.That(buffer[0]).IsNull();
        await Assert.That(buffer[1]).IsNull();
        await Assert.That(buffer[2]).IsNull();
        await Assert.That(buffer[3]).IsNull();
        await Assert.That(store.Get(0)).IsSameReferenceAs(value);
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
        store.Append(value);

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
        store.Append(value);
        store.Append(value);

        store.Clear();

        await Assert.That(store.Count).IsEqualTo(0);
        await Assert.That(store.Get(0)).IsNull();
        await Assert.That(store.Get(1)).IsNull();
    }

    [Test]
    public async Task LongStore_Constructor_InvalidChunkShiftAboveMax_Throws()
    {
        await Assert
            .That(() => new Collections.ChunkedGrowOnlyStore<long>(chunkShift: 21))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task LongStore_TryGet_InvalidIndex_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);

        await Assert.That(store.TryGet(-1, out _)).IsFalse();
        await Assert.That(store.TryGet(Ids.ArrayIndexIdRange.MaxValue + 1, out _)).IsFalse();
    }

    [Test]
    public async Task LongStore_TryGet_BeyondAllocatedChunks_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        store.Append(10L);

        await Assert.That(store.TryGet(256, out _)).IsFalse();
    }

    [Test]
    public async Task LongStore_TryGet_UnallocatedChunk_ReturnsFalse()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        store.Append(10L);

        await Assert.That(store.TryGet(16, out _)).IsFalse();
    }

    [Test]
    public async Task LongStore_Clear_DropsAllValues()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        store.Append(42L);

        store.Clear();

        await Assert.That(store.TryGet(0, out _)).IsFalse();
        await Assert.That(store.Count).IsEqualTo(0);
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
    public async Task ReferenceStore_Append_AcrossChunkBoundary_Roundtrips()
    {
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value = new();
        for (int i = 0; i < 17; i++)
        {
            store.Append(value);
        }

        await Assert.That(store.Count).IsEqualTo(17);
        await Assert.That(store.Get(16)).IsSameReferenceAs(value);
        await Assert.That(store.Get(17)).IsNull();
    }

    [Test]
    public async Task LongStore_Append_AcrossChunkBoundary_Roundtrips()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        for (int i = 0; i < 17; i++)
        {
            store.Append(i);
        }

        await Assert.That(store.Count).IsEqualTo(17);
        await Assert.That(store.TryGet(16, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(16L);
        await Assert.That(store.TryGet(17, out _)).IsFalse();
    }

    [Test]
    public async Task LongStore_Append_TwoIndicesSameChunk_ReusesInnerChunk()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        store.Append(10L);
        store.Append(11L);

        await Assert.That(store.TryGet(0, out long first)).IsTrue();
        await Assert.That(first).IsEqualTo(10L);
        await Assert.That(store.TryGet(1, out long second)).IsTrue();
        await Assert.That(second).IsEqualTo(11L);
    }

    [Test]
    public async Task LongStore_Append_SequentialSameChunk_Roundtrips()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        store.Append(1L);
        store.Append(2L);
        store.Append(3L);

        await Assert.That(store.TryGet(2, out long value)).IsTrue();
        await Assert.That(value).IsEqualTo(3L);
    }

    [Test]
    public async Task GrowOnlyStore_ConcurrentAppend_ThrowsInvalidOperationException()
    {
        object value0 = new();
        object value1 = new();

        // Fresh store per round; the probe repeats rounds until both writers overlapped once.
        Collections.ChunkedGrowOnlyStore<object>? rejected = await ConcurrentWriterProbe.SecondWriterIsRejectedAsync(
            static () => new Collections.ChunkedGrowOnlyStore<object>(chunkShift: 4),
            (store, _) => store.Append(value0),
            (store, _) => store.Append(value1),
            burst: 65_536);

        await Assert.That(rejected).IsNotNull();
    }

    [Test]
    public async Task ValueStore_AppendGet_Roundtrip()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);

        store.Append(11);
        store.Append(22);

        await Assert.That(store.Count).IsEqualTo(2);
        await Assert.That(store.Get(0)).IsEqualTo(11);
        await Assert.That(store.Get(1)).IsEqualTo(22);
        await Assert.That(store.Get(2)).IsEqualTo(0);
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
        store.Append(5);

        await Assert.That(store.TryGet(0, out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(5);
    }

    [Test]
    public async Task GrowOnlyStore_AppendRange_Empty_NoOp()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        store.Append(7);

        store.AppendRange(ReadOnlySpan<int>.Empty);

        await Assert.That(store.Count).IsEqualTo(1);
        await Assert.That(store.Get(0)).IsEqualTo(7);
    }

    [Test]
    public async Task GrowOnlyStore_AppendRange_TwoChunks_RoundTrip()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        int[] values = new int[20];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = 100 + i;
        }

        store.AppendRange(values);

        await Assert.That(store.Count).IsEqualTo(20);
        await Assert.That(store.Get(20)).IsEqualTo(0);
        for (int i = 0; i < values.Length; i++)
        {
            await Assert.That(store.Get(i)).IsEqualTo(100 + i);
        }
    }

    [Test]
    public async Task GrowOnlyStore_AppendRange_Length100Then5000_GrowsCount()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        int[] hundred = new int[100];
        int[] fiveThousand = new int[5000];
        for (int i = 0; i < hundred.Length; i++)
        {
            hundred[i] = i + 1;
        }

        for (int i = 0; i < fiveThousand.Length; i++)
        {
            fiveThousand[i] = i + 1000;
        }

        store.AppendRange(hundred);
        store.AppendRange(fiveThousand);

        await Assert.That(store.Count).IsEqualTo(5100);
        await Assert.That(store.Get(0)).IsEqualTo(1);
        await Assert.That(store.Get(99)).IsEqualTo(100);
        await Assert.That(store.Get(100)).IsEqualTo(1000);
        await Assert.That(store.Get(5099)).IsEqualTo(5999);
    }

    [Test]
    public async Task GrowOnlyStore_AppendRange_PastIndexRange_Throws()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        _SetPublishedCount(store, Ids.ArrayIndexIdRange.MaxValue);
        int[] values = [1, 2];

        await Assert
            .That(() => store.AppendRange(values))
            .Throws<InvalidOperationException>();
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
        // Fresh store per round; the probe repeats rounds until both writers overlapped once.
        Collections.ChunkedAppendOnlyStore<KeyedItem>? rejected = await ConcurrentWriterProbe.SecondWriterIsRejectedAsync(
            static () => new Collections.ChunkedAppendOnlyStore<KeyedItem>(chunkShift: 4),
            static (store, i) => store.Append(new KeyedItem(i, i)),
            static (store, i) => store.Append(new KeyedItem(unchecked(1_000_000 + i), i)),
            burst: 65_536);

        await Assert.That(rejected).IsNotNull();
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
        store.Append(value);

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

    /// <summary>
    /// The factory runs inside the allocating call, so a second allocation from it overlaps the first: the
    /// single-allocator gate throws instead of risking a lost chunk, and releases on the way out.
    /// </summary>
    [Test]
    public async Task ChunkedOuterArrayOverlappingAllocationThrows()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);

        await Assert
            .That(() => outer.GetOrAllocateChunk(0, () => outer.GetOrAllocateChunk(1, () => new object[16])))
            .Throws<InvalidOperationException>();

        object[] chunk = outer.GetOrAllocateChunk(0, () => new object[16]);
        await Assert.That(outer.GetChunk(0)).IsSameReferenceAs(chunk);
        await Assert.That(outer.GetChunk(1)).IsNull();
    }

    /// <summary><c>Clear</c> during an allocating call overlaps it and throws; the allocation then installs nothing.</summary>
    [Test]
    public async Task ChunkedOuterArrayClearDuringAllocationThrows()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);

        await Assert
            .That(() => outer.GetOrAllocateChunk(2, () =>
            {
                outer.Clear();
                return new object[16];
            }))
            .Throws<InvalidOperationException>();

        await Assert.That(outer.GetChunk(2)).IsNull();
    }

    /// <summary>
    /// Returning an installed chunk takes no gate, so it succeeds even while an allocation runs; only allocating
    /// calls must not overlap.
    /// </summary>
    [Test]
    public async Task ChunkedOuterArrayInstalledChunkIsReturnedDuringAnAllocation()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[] first = outer.GetOrAllocateChunk(0, () => new object[16]);
        object[]? seen = null;

        object[] second = outer.GetOrAllocateChunk(5, () =>
        {
            seen = outer.GetOrAllocateChunk(0, () => throw new InvalidOperationException("factory must not run"));
            return new object[16];
        });

        await Assert.That(seen).IsSameReferenceAs(first);
        await Assert.That(outer.GetChunk(5)).IsSameReferenceAs(second);
    }

    /// <summary>A throwing factory installs nothing and releases the gate; the next allocation of the index succeeds.</summary>
    [Test]
    public async Task ChunkedOuterArrayFailedFactoryLeavesTheArrayUsable()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);

        await Assert
            .That(() => outer.GetOrAllocateChunk(3, () => throw new InvalidOperationException("factory failed")))
            .Throws<InvalidOperationException>();
        await Assert.That(outer.GetChunk(3)).IsNull();

        object[] chunk = outer.GetOrAllocateChunk(3, () => new object[16]);
        await Assert.That(outer.GetChunk(3)).IsSameReferenceAs(chunk);
    }

    /// <summary>Allocations one after the other on different threads are allowed; the second one grows the outer array.</summary>
    [Test]
    public async Task ChunkedOuterArraySequentialAllocationsOnDifferentThreadsKeepEveryChunk()
    {
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);

        object[] first = await Task.Run(() => outer.GetOrAllocateChunk(0, () => new object[16]));
        object[] second = await Task.Run(() => outer.GetOrAllocateChunk(17, () => new object[16]));

        await Assert.That(outer.GetChunk(0)).IsSameReferenceAs(first);
        await Assert.That(outer.GetChunk(17)).IsSameReferenceAs(second);
    }

    /// <summary>
    /// One allocator installs chunks 0 … 255 in order, growing the outer array nine times, while four readers poll.
    /// A reader that saw n chunks published must find each of them, also across a growth that replaced the outer array.
    /// </summary>
    [Test]
    public async Task ChunkedOuterArrayReadersDuringGrowthSeeEveryInstalledChunk()
    {
        const int chunkCount = 256;
        const int readerCount = 4;
        Collections.ChunkedOuterArray<object[]> outer = new(chunkShift: 4);
        object[][] expected = new object[chunkCount][];
        for (int index = 0; index < chunkCount; index++)
        {
            expected[index] = new object[16];
        }

        // Shared between the allocator and the readers: published through Volatile, counted with Interlocked.
        int published = 0;
        int missing = 0;

        Task allocator = Task.Run(() =>
        {
            for (int index = 0; index < chunkCount; index++)
            {
                object[] chunk = expected[index];
                _ = outer.GetOrAllocateChunk(index, () => chunk);
                Volatile.Write(ref published, index + 1);
            }
        });

        // Readers stop once everything is published, or when the allocator failed (its exception surfaces below).
        Task[] readers = new Task[readerCount];
        for (int reader = 0; reader < readerCount; reader++)
        {
            readers[reader] = Task.Run(() =>
            {
                int seen;
                do
                {
                    seen = Volatile.Read(ref published);
                    for (int index = 0; index < seen; index++)
                    {
                        if (!ReferenceEquals(outer.GetChunk(index), expected[index]))
                        {
                            _ = Interlocked.Increment(ref missing);
                        }
                    }
                }
                while (seen < chunkCount && !allocator.IsFaulted);
            });
        }

        await allocator;
        await Task.WhenAll(readers);

        await Assert.That(missing).IsEqualTo(0);
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
    public async Task GrowOnlyStore_ConcurrentAppendRange_Throws()
    {
        int[] data = new int[50_000];

        // Fresh store per round: the first writer appends ranges, the second single values until the first is done.
        Collections.ChunkedGrowOnlyStore<int>? rejected = await ConcurrentWriterProbe.SecondWriterIsRejectedAsync(
            static () => new Collections.ChunkedGrowOnlyStore<int>(chunkShift: 4),
            (store, _) => store.AppendRange(data),
            static (store, _) => store.Append(1),
            burst: 16);

        await Assert.That(rejected).IsNotNull();
    }

    [Test]
    public async Task AppendRange_PublishesCount()
    {
        Collections.ChunkedAppendOnlyStore<int> store = new(chunkShift: 4);
        int[] hundred = new int[100];
        int[] fiveThousand = new int[5000];
        for (int i = 0; i < hundred.Length; i++)
        {
            hundred[i] = i;
        }

        for (int i = 0; i < fiveThousand.Length; i++)
        {
            fiveThousand[i] = i + 100;
        }

        store.AppendRange(hundred);
        await Assert.That(store.Count).IsEqualTo(100);
        await Assert.That(store.ItemRef(0)).IsEqualTo(0);
        await Assert.That(store.ItemRef(99)).IsEqualTo(99);

        store.AppendRange(fiveThousand);
        await Assert.That(store.Count).IsEqualTo(5100);
        await Assert.That(store.ItemRef(100)).IsEqualTo(100);
        await Assert.That(store.ItemRef(5099)).IsEqualTo(5099);
    }

    [Test]
    public async Task AppendRange_Empty_NoOp()
    {
        Collections.ChunkedAppendOnlyStore<int> store = new(chunkShift: 4);
        store.Append(3);

        store.AppendRange(ReadOnlySpan<int>.Empty);

        await Assert.That(store.Count).IsEqualTo(1);
        await Assert.That(store.ItemRef(0)).IsEqualTo(3);
    }

    [Test]
    public async Task AppendRange_ConcurrentAppend_Throws()
    {
        int[] data = new int[50_000];

        // Fresh store per round: the first writer appends ranges, the second single values until the first is done.
        Collections.ChunkedAppendOnlyStore<int>? rejected = await ConcurrentWriterProbe.SecondWriterIsRejectedAsync(
            static () => new Collections.ChunkedAppendOnlyStore<int>(chunkShift: 4),
            (store, _) => store.AppendRange(data),
            static (store, _) => store.Append(1),
            burst: 16);

        await Assert.That(rejected).IsNotNull();
    }

    [Test]
    public async Task AppendOverlappingClearDoesNotPublishAHole()
    {
        Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4, unsetValue: -1);
        ClearStop stop = new();
        Thread clearer = new(() =>
        {
            while (stop.Value == 0)
            {
                store.Clear();
            }
        })
        {
            Name = "grow-only-clear",
            IsBackground = true,
        };
        clearer.Start();

        int stablePublishes = 0;
        try
        {
            for (int i = 0; i < 100_000; i++)
            {
                // A Clear that lands after this read makes Count smaller. That is not a hole.
                int before = store.Count;
                store.Append(7);
                int after = store.Count;
                if (after != before + 1)
                {
                    continue;
                }

                bool found = store.TryGet(after - 1, out int value);
                if (store.Count < after)
                {
                    continue;
                }

                stablePublishes++;
                if (!found || value != 7)
                {
                    await Assert.That(found).IsTrue();
                    await Assert.That(value).IsEqualTo(7);
                }
            }
        }
        finally
        {
            stop.Value = 1;
            clearer.Join();
        }

        store.Append(7);

        await Assert.That(stablePublishes).IsGreaterThan(0);
        await Assert.That(store.Count).IsGreaterThan(0);
        for (int index = 0; index < store.Count; index++)
        {
            bool found = store.TryGet(index, out int value);
            if (!found || value == -1)
            {
                await Assert.That(found).IsTrue();
                await Assert.That(value).IsNotEqualTo(-1);
            }
        }

        bool lastFound = store.TryGet(store.Count - 1, out int last);
        await Assert.That(lastFound).IsTrue();
        await Assert.That(last).IsEqualTo(7);
    }

    [Test]
    public async Task AppendRangeOverlappingClearDoesNotPublishAHole()
    {
        Collections.ChunkedAppendOnlyStore<int> store = new(chunkShift: 4);
        int[] pair = [7, 8];
        ClearStop stop = new();
        Thread clearer = new(() =>
        {
            while (stop.Value == 0)
            {
                store.Clear();
            }
        })
        {
            Name = "grow-only-clear",
            IsBackground = true,
        };
        clearer.Start();

        int stablePublishes = 0;
        try
        {
            for (int i = 0; i < 100_000; i++)
            {
                int before = store.Count;
                store.AppendRange(pair);
                int after = store.Count;
                if (after != before + pair.Length)
                {
                    continue;
                }

                bool firstFound = store.TryReadPublished(before, out int first);
                bool secondFound = store.TryReadPublished(before + 1, out int second);
                if (store.Count < after)
                {
                    continue;
                }

                // A Clear that replaced the generation makes TryReadPublished miss.
                // A hit is still the pair just written, never the unset sentinel.
                if (!firstFound || !secondFound)
                {
                    continue;
                }

                stablePublishes++;
                if (first != 7 || second != 8)
                {
                    await Assert.That(first).IsEqualTo(7);
                    await Assert.That(second).IsEqualTo(8);
                }
            }
        }
        finally
        {
            stop.Value = 1;
            clearer.Join();
        }

        // Quiescent prefix from the overlapped phase, then one more publish with no Clear.
        await _AssertPackedPrefixHasNoUnset(store);
        store.AppendRange(pair);

        await Assert.That(stablePublishes).IsGreaterThan(0);
        await _AssertPackedPrefixHasNoUnset(store);
        await Assert.That(store.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task PackedStoreAppendAfterClearPublishesIntoTheNewGeneration()
    {
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        for (int i = 0; i < 20; i++)
        {
            store.Append(new KeyedItem(i, 1));
        }

        store.Clear();
        store.Append(new KeyedItem(0, 2));

        await Assert.That(store.Count).IsEqualTo(1);
        await Assert.That(store.ItemRef(0)).IsEqualTo(new KeyedItem(0, 2));
        await Assert.That(store.TryReadPublished(1, out _)).IsFalse();
        await Assert.That(store.TryReadPublished(17, out _)).IsFalse();
        await Assert.That(store.BinarySearch(17)).IsEqualTo(-1);
    }

    [Test]
    public async Task GrowOnlyStoreTryGetPublishedChunkAfterClearReturnsFalse()
    {
        Collections.ChunkedGrowOnlyStore<long> store = new(chunkShift: 4);
        for (int i = 0; i < 20; i++)
        {
            store.Append(i);
        }

        store.Clear();
        bool firstChunk = store.TryGetPublishedChunk(0, 20, out _);
        bool secondChunk = store.TryGetPublishedChunk(1, 20, out _);

        await Assert.That(firstChunk).IsFalse();
        await Assert.That(secondChunk).IsFalse();
    }

    [Test]
    public async Task PackedStoreReadersDuringAppendAndClearSeeOnlyPublishedEntries()
    {
        // Every generation holds Key == index and a round tag >= 1. A reader that mixed a count with the
        // chunks of another generation, or read an unpublished slot, would see a default entry or a wrong key.
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        const int rounds = 200;
        const int entriesPerRound = 100;
        int finished = 0;
        int violation = 0;

        Task writer = Task.Run(() =>
        {
            for (int round = 1; round <= rounds; round++)
            {
                for (int i = 0; i < entriesPerRound; i++)
                {
                    store.Append(new KeyedItem(i, round));
                }

                store.Clear();
            }

            Volatile.Write(ref finished, 1);
        });

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = Task.Run(() =>
            {
                int probe = seed;
                while (Volatile.Read(ref finished) == 0)
                {
                    probe = (probe + 37) % entriesPerRound;
                    if (store.TryReadPublished(probe, out KeyedItem item) && (item.Key != probe || item.Payload < 1))
                    {
                        _ = Interlocked.Exchange(ref violation, 1);
                        return;
                    }

                    int found = store.BinarySearch(probe);
                    if (found >= 0 && found != probe)
                    {
                        _ = Interlocked.Exchange(ref violation, 1);
                        return;
                    }
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);

        await Assert.That(Volatile.Read(ref violation)).IsEqualTo(0);
        await Assert.That(store.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Sets the published count of the live generation, so a test can reach the index-range limit
    /// without appending two billion entries.
    /// </summary>
    private static void _SetPublishedCount<TStore>(TStore store, int count)
        where TStore : class
    {
        object slotStore = typeof(TStore)
            .GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        object generation = slotStore.GetType()
            .GetField("_Current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(slotStore)!;
        generation.GetType()
            .GetField("Count", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(generation, count);
    }

    private static async Task _AssertPackedPrefixHasNoUnset(Collections.ChunkedAppendOnlyStore<int> store)
    {
        for (int index = 0; index < store.Count; index++)
        {
            bool found = store.TryReadPublished(index, out int value);
            if (!found || value == 0)
            {
                await Assert.That(found).IsTrue();
                await Assert.That(value).IsNotEqualTo(0);
            }
        }
    }

    /// <summary>
    /// Cross-thread stop for a clearer. The test thread writes 1; the clearer reads it.
    /// </summary>
    private sealed class ClearStop
    {
        internal volatile int Value;
    }

    #region Tail chunk and owned appends

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TailChunkAppendAcrossChunkEdgesAndAfterClear(bool owned)
    {
        // 16 slots per chunk: 100 entries span six full chunks and one with four entries.
        ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
        for (int i = 0; i < 100; i++)
        {
            if (owned)
            {
                store.AppendOwned(i);
            }
            else
            {
                store.Append(i);
            }
        }

        bool allMatch = true;
        for (int i = 0; i < 100; i++)
        {
            allMatch &= store.Get(i) == i;
        }

        List<int> spans = [];
        for (int chunk = 0; store.TryGetPublishedChunk(chunk, store.Count, out ReadOnlySpan<int> span); chunk++)
        {
            spans.Add(span.Length);
        }

        // Clear starts a new generation: the tail cache must not write into the old one.
        store.Clear();
        for (int i = 0; i <= 20; i++)
        {
            if (owned)
            {
                store.AppendOwned(1_000 + i);
            }
            else
            {
                store.Append(1_000 + i);
            }
        }

        bool refilled = true;
        for (int i = 0; i <= 20; i++)
        {
            refilled &= store.Get(i) == 1_000 + i;
        }

        await Assert.That(allMatch).IsTrue();
        int[] expectedSpans = [16, 16, 16, 16, 16, 16, 4];
        await Assert.That(spans.SequenceEqual(expectedSpans)).IsTrue();
        await Assert.That(store.Count).IsEqualTo(21);
        await Assert.That(refilled).IsTrue();
        await Assert.That(store.TryGet(21, out _)).IsFalse();
    }

    [Test]
    public async Task OwnedAppendsWithConcurrentReadersSeeOnlyPublishedRows()
    {
        const int total = 300_000;
        ChunkedGrowOnlyStore<long> store = new(chunkShift: 6);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using Barrier start = new(4);
        int done = 0;
        int wrong = 0;

        Task owner = Task.Run(() =>
        {
            start.SignalAndWait(timeout.Token);
            for (int i = 0; i < total; i++)
            {
                store.AppendOwned(i);
            }

            Volatile.Write(ref done, 1);
        });

        Task[] readers = new Task[3];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                start.SignalAndWait(timeout.Token);
                int checkedUpTo = 0;
                while (Volatile.Read(ref done) == 0 && !timeout.IsCancellationRequested)
                {
                    // Every index below a published count must hold its value, never the unset default.
                    int count = store.Count;
                    for (int i = checkedUpTo; i < count; i++)
                    {
                        if (store.Get(i) != i)
                        {
                            _ = Interlocked.Increment(ref wrong);
                        }
                    }

                    checkedUpTo = count;
                }
            });
        }

        await owner.WaitAsync(timeout.Token);
        await Task.WhenAll(readers).WaitAsync(timeout.Token);
        await Assert.That(Volatile.Read(ref wrong)).IsEqualTo(0);
        await Assert.That(store.Count).IsEqualTo(total);
        await Assert.That(store.Get(total - 1)).IsEqualTo(total - 1);
    }

    #endregion
}
