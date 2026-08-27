// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Pins <see cref="Collections.EffectStore{T}"/> semantics used by stateful protocols to key effects
/// by <c>(PacketId, LayerKey)</c> where layer key is <see cref="Packet.GetEffectLayerKey"/>.
/// </summary>
internal sealed class EffectStoreTests
{
    [Test]
    public async Task TryGet_MissingPacket_ReturnsFalse()
    {
        EffectStore<int> store = new();
        store.Record(0, 480, 10);

        await Assert.That(store.TryGet(1, 480, out _)).IsFalse();
    }

    [Test]
    public async Task TryGet_MissingLayerKey_ReturnsFalse()
    {
        EffectStore<int> store = new();
        store.Record(3, 480, 10);

        await Assert.That(store.TryGet(3, 448, out _)).IsFalse();
    }

    [Test]
    public async Task Record_AppendsInPacketIdOrder_BinarySearchFindsEntry()
    {
        EffectStore<int> store = new();
        store.Record(0, 100, 1);
        store.Record(2, 200, 2);
        store.Record(5, 300, 3);

        await Assert.That(store.TryGet(2, 200, out int effect)).IsTrue();
        await Assert.That(effect).IsEqualTo(2);
    }

    [Test]
    public async Task Record_SamePacketTwoLayers_CountIsOne()
    {
        EffectStore<int> store = new();
        store.Record(4, 480, 100);
        store.Record(4, 448, 200);

        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryGet_UsesVolatileMoreHead_ReturnsNestedLayer()
    {
        EffectStore<int> store = new();
        store.Record(4, 480, 100);
        store.Record(4, 448, 200);
        store.Record(4, 420, 300);

        await Assert.That(store.TryGet(4, 480, out int outer)).IsTrue();
        await Assert.That(outer).IsEqualTo(100);
        await Assert.That(store.TryGet(4, 448, out int inner)).IsTrue();
        await Assert.That(inner).IsEqualTo(200);
        await Assert.That(store.TryGet(4, 420, out int deepest)).IsTrue();
        await Assert.That(deepest).IsEqualTo(300);
    }

    [Test]
    public async Task TailPacketId_ReflectsLastAppendedPacket()
    {
        EffectStore<int> store = new();

        await Assert.That(store.TailPacketId).IsEqualTo(int.MinValue);

        store.Record(1, 50, 1);
        await Assert.That(store.TailPacketId).IsEqualTo(1);

        store.Record(1, 40, 2);
        await Assert.That(store.TailPacketId).IsEqualTo(1);

        store.Record(2, 60, 3);
        await Assert.That(store.TailPacketId).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_DropsAllEntries()
    {
        EffectStore<int> store = new();
        store.Record(0, 10, 1);
        store.Record(1, 20, 2);

        store.Clear();

        await Assert.That(store.Count).IsEqualTo(0);
        await Assert.That(store.TryGet(0, 10, out _)).IsFalse();
        await Assert.That(store.TryGet(1, 20, out _)).IsFalse();
    }

    [Test]
    public async Task TryGet_AfterClear_ReturnsFalse()
    {
        EffectStore<int> store = new();
        store.Record(0, 10, 1);

        store.Clear();

        await Assert.That(store.TryGet(0, 10, out _)).IsFalse();
    }

    [Test]
    public async Task Record_Clear_RecordNewEpoch_OldKeyMisses()
    {
        EffectStore<int> store = new();
        store.Record(0, 10, 1);
        store.Clear();
        store.Record(0, 10, 2);

        await Assert.That(store.TryGet(0, 10, out int effect)).IsTrue();
        await Assert.That(effect).IsEqualTo(2);
    }

    [Test]
    public async Task Record_DuplicateLayerKey_Throws()
    {
        EffectStore<int> store = new();
        store.Record(4, 480, 100);

        await Assert.That(() => store.Record(4, 480, 200)).Throws<InvalidOperationException>();
        await Assert.That(store.TryGet(4, 480, out int effect)).IsTrue();
        await Assert.That(effect).IsEqualTo(100);
    }

    [Test]
    public async Task Record_SingleWriterConcurrentReaders_PublishedEffectsMatch()
    {
        EffectStore<int> store = new();
        const int total = 2048;
        const int layerKey = 7;
        int mismatch = 0;

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                store.Record(i, layerKey, i);
            }
        });

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                while (true)
                {
                    int count = store.Count;
                    for (int i = 0; i < count; i++)
                    {
                        if (!store.TryGet(i, layerKey, out int effect) || effect != i)
                        {
                            _ = Interlocked.Exchange(ref mismatch, 1);
                            return;
                        }
                    }

                    if (count == total)
                    {
                        return;
                    }
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);
        await Assert.That(Volatile.Read(ref mismatch)).IsEqualTo(0);
    }

    [Test]
    public async Task Record_NegativePacketId_ThrowsArgumentOutOfRangeException()
    {
        EffectStore<int> store = new();

        await Assert.That(() => store.Record(-1, 10, 1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Record_DecreasingPacketId_ThrowsInvalidOperationException()
    {
        EffectStore<int> store = new();
        store.Record(1, 10, 1);

        await Assert.That(() => store.Record(0, 20, 2)).Throws<InvalidOperationException>();
        await Assert.That(store.TryGet(1, 10, out int effect)).IsTrue();
        await Assert.That(effect).IsEqualTo(1);
        await Assert.That(store.TryGet(0, 20, out _)).IsFalse();
    }

    [Test]
    public async Task Record_ConcurrentSecondWriter_ThrowsInvalidOperationException()
    {
        EffectStore<int> store = new();
        store.Record(4, 1, 100);
        const int total = 4096;
        int ready = 0;
        int threw = 0;

        void Writer(int layerBase)
        {
            _ = Interlocked.Increment(ref ready);
            while (Volatile.Read(ref ready) < 2)
            {
            }

            for (int i = 0; i < total; i++)
            {
                try
                {
                    store.Record(4, layerBase + i, layerBase + i);
                }
                catch (InvalidOperationException)
                {
                    _ = Interlocked.Exchange(ref threw, 1);
                    return;
                }
            }
        }

        Task first = Task.Run(() => Writer(1_000));
        Task second = Task.Run(() => Writer(2_000_000));
        await Task.WhenAll(first, second);

        await Assert.That(Volatile.Read(ref threw)).IsEqualTo(1);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryGet_ClearRefillDisjointIds_LookupOfOldIdMisses()
    {
        EffectStore<int> store = new(chunkShift: 4);
        const int layerKey = 7;
        int foreign = 0;
        int startedRefill = 0;
        int finished = 0;

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 64; i++)
            {
                store.Record(i, layerKey, i);
            }

            store.Clear();
            Volatile.Write(ref startedRefill, 1);
            for (int i = 0; i < 64; i++)
            {
                store.Record(1_000_000 + i, layerKey, 1_000_000 + i);
            }

            Volatile.Write(ref finished, 1);
        });

        Task reader = Task.Run(() =>
        {
            while (Volatile.Read(ref finished) == 0)
            {
                if (Volatile.Read(ref startedRefill) == 1
                    && store.TryGet(10, layerKey, out int effect)
                    && effect != 10)
                {
                    _ = Interlocked.Exchange(ref foreign, 1);
                    return;
                }
            }
        });

        await writer;
        await reader;
        await Assert.That(Volatile.Read(ref foreign)).IsEqualTo(0);
        await Assert.That(store.TryGet(10, layerKey, out _)).IsFalse();
    }

    [Test]
    public async Task Record_NestedLayers_ConcurrentReadersSeeFullyPublishedNodes()
    {
        EffectStore<int> store = new(chunkShift: 4);
        const int total = 512;
        int mismatch = 0;

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                store.Record(i, 1, i);
                store.Record(i, 2, i + 1000);
                store.Record(i, 3, i + 2000);
            }
        });

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                while (true)
                {
                    int count = store.Count;
                    for (int i = 0; i < count; i++)
                    {
                        if (!store.TryGet(i, 1, out int first) || first != i)
                        {
                            _ = Interlocked.Exchange(ref mismatch, 1);
                            return;
                        }

                        if (store.TryGet(i, 2, out int second) && second != i + 1000)
                        {
                            _ = Interlocked.Exchange(ref mismatch, 1);
                            return;
                        }

                        if (store.TryGet(i, 3, out int third) && third != i + 2000)
                        {
                            _ = Interlocked.Exchange(ref mismatch, 1);
                            return;
                        }
                    }

                    if (count == total)
                    {
                        return;
                    }
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);
        await Assert.That(Volatile.Read(ref mismatch)).IsEqualTo(0);
        await Assert.That(store.TryGet(total - 1, 3, out int last)).IsTrue();
        await Assert.That(last).IsEqualTo(total - 1 + 2000);
    }
}
