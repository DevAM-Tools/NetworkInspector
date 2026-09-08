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
        System.Reflection.FieldInfo countField = typeof(Collections.ChunkedGrowOnlyStore<object>)
            .GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType
            .GetField("_Count", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object slotStore = typeof(Collections.ChunkedGrowOnlyStore<object>)
            .GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        countField.SetValue(slotStore, Ids.ArrayIndexIdRange.MaxValue + 1);

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
        Collections.ChunkedGrowOnlyStore<object> store = new(chunkShift: 4);
        object value0 = new();
        object value1 = new();
        const int total = 65_536;
        int ready = 0;
        int threw = 0;

        void Writer(object value)
        {
            _ = Interlocked.Increment(ref ready);
            while (Volatile.Read(ref ready) < 2)
            {
            }

            for (int i = 0; i < total; i++)
            {
                try
                {
                    store.Append(value);
                }
                catch (InvalidOperationException)
                {
                    _ = Interlocked.Exchange(ref threw, 1);
                    return;
                }
            }
        }

        Task first = Task.Run(() => Writer(value0));
        Task second = Task.Run(() => Writer(value1));
        await Task.WhenAll(first, second);

        await Assert.That(Volatile.Read(ref threw)).IsEqualTo(1);
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
        System.Reflection.FieldInfo countField = typeof(Collections.ChunkedGrowOnlyStore<int>)
            .GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType
            .GetField("_Count", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object slotStore = typeof(Collections.ChunkedGrowOnlyStore<int>)
            .GetField("_Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        countField.SetValue(slotStore, Ids.ArrayIndexIdRange.MaxValue);
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
        Collections.ChunkedAppendOnlyStore<KeyedItem> store = new(chunkShift: 4);
        const int total = 65_536;
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
    public async Task GrowOnlyStore_ConcurrentAppendRange_Throws()
    {
        bool sawConcurrent = false;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            Collections.ChunkedGrowOnlyStore<int> store = new(chunkShift: 4);
            int[] data = new int[200_000];
            using Barrier barrier = new(2);
            Thread worker = new(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    store.AppendRange(data);
                }
                catch (InvalidOperationException)
                {
                    Volatile.Write(ref sawConcurrent, true);
                }
            })
            {
                Name = "grow-only-append-range-worker",
            };
            worker.Start();
            barrier.SignalAndWait();
            try
            {
                store.Append(1);
            }
            catch (InvalidOperationException)
            {
                sawConcurrent = true;
            }

            worker.Join();
            if (sawConcurrent)
            {
                break;
            }
        }

        await Assert.That(sawConcurrent).IsTrue();
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
        bool sawConcurrent = false;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            Collections.ChunkedAppendOnlyStore<int> store = new(chunkShift: 4);
            int[] data = new int[200_000];
            using Barrier barrier = new(2);
            Thread worker = new(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    store.AppendRange(data);
                }
                catch (InvalidOperationException)
                {
                    Volatile.Write(ref sawConcurrent, true);
                }
            })
            {
                Name = "append-range-worker",
            };
            worker.Start();
            barrier.SignalAndWait();
            try
            {
                store.Append(1);
            }
            catch (InvalidOperationException)
            {
                sawConcurrent = true;
                worker.Join();
                break;
            }

            worker.Join();
        }

        await Assert.That(sawConcurrent).IsTrue();
    }
}
