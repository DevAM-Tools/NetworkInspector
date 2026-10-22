// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="FieldStorage"/> through <see cref="Packet"/>: the first-chunk / follow-up-chunk split keeps order
/// and links, slot access stays inside the slab slice, recycling keeps, clears, and releases chunks by the retention
/// policy, pooled chunks carry no data of an earlier packet, the field limit and its reserved error slot still hold,
/// and post-Seal lazy growth into follow-up chunks is race free.
/// <para><b>Thread safety:</b> every test builds its own stack and packets; the concurrency tests share one sealed
/// packet between their own threads only. Tests that count pooled chunks run on a dedicated new thread, because the
/// pool and the slab are thread-local.</para>
/// </summary>
internal sealed class FieldStorageTests
{
    #region Helpers

    private sealed class Fields
    {
        public required Stack Stack { get; init; }
        public required FieldId Item { get; init; }
        public required FieldId Container { get; init; }
        public required FieldId Child { get; init; }
        public required FieldId Nested { get; init; }
    }

    private sealed class StubProtocol : IProtocol
    {
        public string Name => "storage.test";

        public string UiName => "Storage Test";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    private static Fields _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolId protocol = builder.RegisterProtocol(new StubProtocol());
        FieldId item = builder.RegisterField(protocol, "storage.item", "Item", FieldType.U64);
        FieldId container = builder.RegisterField(protocol, "storage.container", "Container", FieldType.None);
        FieldId child = builder.RegisterField(protocol, "storage.child", "Child", FieldType.U64);
        FieldId nested = builder.RegisterField(protocol, "storage.nested", "Nested", FieldType.None);
        return new Fields { Stack = builder.Build(), Item = item, Container = container, Child = child, Nested = nested };
    }

    private static Frame _Frame(Stack stack, int id) => Frame.Create(
        new FrameId(id), Timestamp.FromSecs(id), new byte[] { 0x01 }, LinkType.Ethernet,
        FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

    /// <summary>A fresh Build-mode packet whose root has <paramref name="count"/> U64 children with values 0..count-1.</summary>
    private static Packet _PacketWithChildren(Fields fields, int count, int valueOffset = 0)
    {
        Packet packet = new(new PacketId(0), fields.Stack, _Frame(fields.Stack, 0));
        _AppendChildren(packet, fields.Item, count, valueOffset);
        packet.Seal();
        return packet;
    }

    private static void _AppendChildren(Packet packet, FieldId fieldId, int count, int valueOffset)
    {
        MutField root = packet.RootFieldMut();
        for (int i = 0; i < count; i++)
        {
            _ = root.Append(fieldId, FieldValue.NewU64((ulong)(valueOffset + i)));
        }
    }

    /// <summary>Recycles <paramref name="packet"/> and fills it with <paramref name="count"/> children.</summary>
    private static void _Recycle(Fields fields, Packet packet, int id, int count, int valueOffset, FieldTreeMode mode = FieldTreeMode.Build)
    {
        RecycleError? error = packet.PrepareForReuse(new PacketId(id), _Frame(fields.Stack, id), mode);
        if (error is not null)
        {
            throw new InvalidOperationException(error.Value.ToString());
        }

        if (mode == FieldTreeMode.Build)
        {
            _AppendChildren(packet, fields.Item, count, valueOffset);
        }

        packet.Seal();
    }

    private static ulong _U64(Field field) =>
        field.Value.Data.TryGetAsU64(out ulong value)
            ? value
            : ulong.MaxValue;

    private static List<(int FieldId, ulong Value)> _Dfs(Packet packet, bool materialize)
    {
        List<(int, ulong)> walk = [];
        foreach (Field field in packet.IterFieldsDfs(materialize))
        {
            walk.Add((field.FieldId.Value, _U64(field)));
        }

        return walk;
    }

    /// <summary>Values of the root's children in order.</summary>
    private static List<ulong> _ChildValues(Packet packet)
    {
        List<ulong> values = [];
        foreach (Field child in packet.RootField().Children(materialize: false))
        {
            values.Add(_U64(child));
        }

        return values;
    }

    private static List<ulong> _Range(int start, int count) => [.. Enumerable.Range(start, count).Select(static i => (ulong)i)];

    /// <summary>A copy of the packet's embedded storage (reading through the copy sees the same slab arrays).</summary>
    private static FieldStorage _Storage(Packet packet) =>
        (FieldStorage)typeof(Packet).GetField("_Fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(packet)!;

    private static int _AllocatedChunks(Packet packet) => _Storage(packet).AllocatedChunks;

    /// <summary>First-chunk array and offset of a storage, read by reflection.</summary>
    private static (FieldBody[] Buffer, int Offset) _FirstChunk(object storage)
    {
        FieldBody[] buffer = (FieldBody[])typeof(FieldStorage)
            .GetField("_First", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(storage)!;
        int offset = (int)typeof(FieldStorage)
            .GetField("_FirstOffset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(storage)!;
        return (buffer, offset);
    }

    /// <summary>Follow-up chunks that own a buffer (descriptors below <c>Allocated</c>), read by reflection.</summary>
    private static (FieldBody[] Buffer, int Offset)[] _ChunkBuffers(Packet packet)
    {
        object storage = _Storage(packet);
        object? table = typeof(FieldStorage).GetField("_Chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage);
        if (table is null)
        {
            return [];
        }

        FieldBodyChunk[] chunks = (FieldBodyChunk[])table.GetType().GetField("Chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        int allocated = (int)table.GetType().GetField("Allocated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        (FieldBody[] Buffer, int Offset)[] result = new (FieldBody[], int)[allocated];
        for (int c = 0; c < allocated; c++)
        {
            result[c] = (chunks[c].Buffer, chunks[c].Offset);
        }

        return result;
    }

    /// <summary>Length of the follow-up descriptor array (0 without a table), read by reflection.</summary>
    private static int _DescriptorCapacity(Packet packet)
    {
        object storage = _Storage(packet);
        object? table = typeof(FieldStorage).GetField("_Chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(storage);
        return table is null
            ? 0
            : ((FieldBodyChunk[])table.GetType().GetField("Chunks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!).Length;
    }

    /// <summary>Every slot of the first chunk and of every follow-up chunk with a buffer, including unused ones.</summary>
    private static List<FieldBody> _AllSlots(Packet packet)
    {
        List<FieldBody> slots = [];
        (FieldBody[] first, int firstOffset) = _FirstChunk(_Storage(packet));
        slots.AddRange(first.AsSpan(firstOffset, FieldStorage.FirstChunkSize));
        foreach ((FieldBody[] buffer, int offset) in _ChunkBuffers(packet))
        {
            slots.AddRange(buffer.AsSpan(offset, FieldStorage.ChunkSize));
        }

        return slots;
    }

    /// <summary>Whether a slot holds nothing: no field id, no value, no links (the state Array.Clear leaves).</summary>
    private static bool _IsZero(in FieldBody slot) => slot.Equals(default(FieldBody));

    private static ulong _SlotU64(in FieldBody slot) =>
        slot.Value.Data.TryGetAsU64(out ulong value)
            ? value
            : 0;

    /// <summary>This thread's chunk pool, or null when this thread never released surplus chunks.</summary>
    private static FieldChunkPool? _PoolOnThisThread() =>
        (FieldChunkPool?)typeof(FieldStorage).GetField("_Pool", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);

    // The pool and slab are [ThreadStatic]; a LongRunning task gets a dedicated thread, so pool counts are exact.
    private static Task<T> _OnFreshThread<T>(Func<T> body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Follow-up chunks a parse with <paramref name="slots"/> slots needs.</summary>
    private static int _ChunksFor(int slots) => (slots - FieldStorage.FirstChunkSize + FieldStorage.ChunkSize - 1) / FieldStorage.ChunkSize;

    #endregion

    #region Chunk edges

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(FieldStorage.FirstChunkSize - 2)]
    [Arguments(FieldStorage.FirstChunkSize - 1)]
    [Arguments(FieldStorage.FirstChunkSize)]
    [Arguments(FieldStorage.FirstChunkSize + FieldStorage.ChunkSize - 1)]
    [Arguments(FieldStorage.FirstChunkSize + FieldStorage.ChunkSize)]
    [Arguments(FieldStorage.FirstChunkSize + (3 * FieldStorage.ChunkSize))]
    [Arguments(300)]
    public async Task ChildrenAcrossChunkEdgesKeepOrderAndLinks(int count)
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = _PacketWithChildren(fields, count);

        // Flat and DFS walks: root first, then the children in insertion order.
        List<ulong> flat = [];
        foreach (Field field in packet.IterFieldsFlat(materialize: false))
        {
            if (field.FieldId == fields.Item)
            {
                flat.Add(_U64(field));
            }
        }

        List<(int FieldId, ulong Value)> dfs = _Dfs(packet, materialize: false);
        Field root = packet.RootField();

        // Neighbor links of every child.
        bool linksOk = true;
        int index = 0;
        foreach (Field child in root.Children(materialize: false))
        {
            linksOk &= _U64(child) == (ulong)index;
            linksOk &= child.TryGetParent(out Field parent) && parent.Equals(root);
            linksOk &= child.TryGetPrev(out Field prev) == (index > 0) && (index == 0 || _U64(prev) == (ulong)(index - 1));
            linksOk &= child.TryGetNext(out Field next) == (index < count - 1) && (index == count - 1 || _U64(next) == (ulong)(index + 1));
            index++;
        }

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(count + 1);
        await Assert.That(flat.SequenceEqual(_Range(0, count))).IsTrue();
        await Assert.That(dfs.Count).IsEqualTo(count + 1);
        await Assert.That(dfs.Skip(1).Select(static e => e.Value).SequenceEqual(flat)).IsTrue();
        await Assert.That((int)root.ChildCount(materialize: false)).IsEqualTo(count);
        await Assert.That(index).IsEqualTo(count);
        await Assert.That(linksOk).IsTrue();
    }

    [Test]
    public async Task PrependAndInsertAfterAtTheChunkEdgeKeepOrder()
    {
        // FirstChunkSize − 1 children fill the first chunk; the prepend lands in the first follow-up slot and the
        // insert-after in the next one. The anchor is the child with value 3, which exists for every chunk shape.
        const int anchorValue = 3;
        int children = FieldStorage.FirstChunkSize - 1;
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = new(new PacketId(0), stack, _Frame(stack, 0));
        _AppendChildren(packet, fields.Item, children, valueOffset: 0);
        MutField root = packet.RootFieldMut();
        _ = root.Prepend(fields.Item, FieldValue.NewU64(1_000));
        MutField anchor = default;
        foreach (MutField child in root.Children(materialize: false))
        {
            if (child.Value.Data.TryGetAsU64(out ulong value) && value == anchorValue)
            {
                anchor = child;
            }
        }

        _ = anchor.InsertAfter(fields.Item, FieldValue.NewU64(2_000));
        packet.Seal();

        List<ulong> expected =
        [
            1_000,
            .. _Range(0, anchorValue + 1),
            2_000,
            .. _Range(anchorValue + 1, children - anchorValue - 1),
        ];
        await Assert.That(_ChildValues(packet).SequenceEqual(expected)).IsTrue();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(FieldStorage.FirstChunkSize + 2);
    }

    #endregion

    #region Slot boundary

    [Test]
    public async Task SlotStaysInsideItsSlabSliceAtChunkEdges()
    {
        FieldId marker = new(77);
        FieldId other = new(88);
        FieldStorage first = default;
        FieldStorage second = default;

        // Two storages carved back to back from the same thread-local slab.
        first.Initialize();
        second.Initialize();
        int last = FieldStorage.FirstChunkSize - 1;
        first.Slot(last) = new FieldBody(marker, FieldValue.NewU64(1));
        second.Slot(last) = new FieldBody(other, FieldValue.NewU64(2));
        second.Slot(0) = new FieldBody(other, FieldValue.NewU64(3));
        FieldId firstLast = first.Slot(last).FieldId;
        FieldId secondLast = second.Slot(last).FieldId;
        FieldId firstZero = first.Slot(0).FieldId;

        // The first follow-up slot exists only after EnsureSlot and is distinct from the first chunk.
        first.EnsureSlot(FieldStorage.FirstChunkSize, postSeal: false);
        first.Slot(FieldStorage.FirstChunkSize) = new FieldBody(other, FieldValue.NewU64(4));
        FieldId firstFollowUp = first.Slot(FieldStorage.FirstChunkSize).FieldId;
        FieldId firstLastAfter = first.Slot(last).FieldId;

        // The last slot of the first follow-up chunk and the first slot of the second are distinct slots.
        int secondChunkStart = FieldStorage.FirstChunkSize + FieldStorage.ChunkSize;
        first.EnsureSlot(secondChunkStart, postSeal: false);
        first.Slot(secondChunkStart - 1) = new FieldBody(marker, FieldValue.NewU64(5));
        first.Slot(secondChunkStart) = new FieldBody(other, FieldValue.NewU64(6));

        await Assert.That(firstLast).IsEqualTo(marker);
        await Assert.That(secondLast).IsEqualTo(other);
        await Assert.That(firstZero).IsNotEqualTo(other);
        await Assert.That(firstFollowUp).IsEqualTo(other);
        await Assert.That(firstLastAfter).IsEqualTo(marker);
        await Assert.That(first.Slot(secondChunkStart - 1).FieldId).IsEqualTo(marker);
        await Assert.That(first.Slot(secondChunkStart).FieldId).IsEqualTo(other);
        await Assert.That(first.Slot(FieldStorage.FirstChunkSize).FieldId).IsEqualTo(other);
    }

    [Test]
    public async Task FirstChunkHoldsTheFrameFieldBudgetPlusOne()
    {
        // The root, every frame field of a materialized frame, and at least one protocol field share the first chunk.
        int firstChunkSize = FieldStorage.FirstChunkSize;

        await Assert.That(firstChunkSize).IsGreaterThanOrEqualTo(FieldStorage.FrameFieldBudget + 1);
    }

    [Test]
    public async Task RootAndMaterializedFrameUseTheFrameFieldBudget()
    {
        // A classic CAN frame (16 bytes, no interface) on the standard protocols, fully materialized.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _ = builder.RegisterStandardProtocols();
        using Stack stack = builder.Build();
        byte[] can = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(can, 0x123u);
        can[4] = 8;
        Frame frame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), can, LinkType.CanSocketcan, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new();
        bool parsed = Packet.TryParse(new PacketId(0), stack, frame, in options, out Packet? packet, out ParseFailure failure);
        packet!.MaterializeAll();

        // The frame node and its descendants, plus the root.
        int frameSlots = 0;
        foreach (Field child in packet.RootField().Children(materialize: true))
        {
            if (child.FieldInfo?.Name != "frame")
            {
                continue;
            }

            frameSlots++;
            foreach (Field descendant in child.Descendants(materialize: true))
            {
                frameSlots++;
            }
        }

        await Assert.That(parsed).IsTrue();
        await Assert.That(1 + frameSlots).IsEqualTo(FieldStorage.FrameFieldBudget);
    }

    [Test]
    public async Task TornFirstChunkStateThrowsInsteadOfLeavingTheArray()
    {
        // A reader that breaks the recycle contract can observe a torn (array, offset) pair. Simulate it: the offset
        // points at the last array element, so slot 5 lies past the array end. A checked access must throw; an
        // unchecked one would return a reference outside the array.
        FieldStorage storage = default;
        storage.Initialize();
        object boxed = storage;
        (FieldBody[] buffer, int _) = _FirstChunk(boxed);
        typeof(FieldStorage)
            .GetField("_FirstOffset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(boxed, buffer.Length - 1);
        FieldStorage torn = (FieldStorage)boxed;

        await Assert
            .That(() => torn.Slot(5).FieldId)
            .Throws<IndexOutOfRangeException>();
    }

    [Test]
    public async Task SlotInAChunkThatWasNeverEnsuredThrows()
    {
        // No follow-up chunk at all: the descriptor table is null.
        FieldStorage fresh = default;
        fresh.Initialize();

        // One follow-up chunk: the second descriptor exists in the table capacity but owns no buffer.
        FieldStorage oneChunk = default;
        oneChunk.Initialize();
        oneChunk.EnsureSlot(FieldStorage.FirstChunkSize, postSeal: false);

        await Assert
            .That(() => fresh.Slot(FieldStorage.FirstChunkSize).FieldId)
            .Throws<NullReferenceException>();
        await Assert
            .That(() => oneChunk.Slot(FieldStorage.FirstChunkSize + FieldStorage.ChunkSize).FieldId)
            .Throws<NullReferenceException>();
    }

    #endregion

    #region Recycle

    [Test]
    public async Task SameSizeRecycleKeepsTheChunkBuffers()
    {
        int children = FieldStorage.FirstChunkSize + (2 * FieldStorage.ChunkSize);
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = _PacketWithChildren(fields, children);
        int allocatedBefore = _AllocatedChunks(packet);
        (FieldBody[] Buffer, int Offset)[] before = _ChunkBuffers(packet);

        // Two recycles of the same size: same buffers and offsets, correct trees.
        _Recycle(fields, packet, 1, children, valueOffset: 100);
        List<ulong> firstTree = _ChildValues(packet);
        (FieldBody[] Buffer, int Offset)[] afterFirst = _ChunkBuffers(packet);
        int allocatedFirst = _AllocatedChunks(packet);
        _Recycle(fields, packet, 2, children, valueOffset: 200);
        List<ulong> secondTree = _ChildValues(packet);
        (FieldBody[] Buffer, int Offset)[] afterSecond = _ChunkBuffers(packet);
        int allocatedSecond = _AllocatedChunks(packet);

        bool sameFirst = afterFirst.Length == before.Length;
        bool sameSecond = afterSecond.Length == before.Length;
        for (int c = 0; c < before.Length && sameFirst && sameSecond; c++)
        {
            sameFirst &= ReferenceEquals(afterFirst[c].Buffer, before[c].Buffer) && afterFirst[c].Offset == before[c].Offset;
            sameSecond &= ReferenceEquals(afterSecond[c].Buffer, before[c].Buffer) && afterSecond[c].Offset == before[c].Offset;
        }

        await Assert.That(allocatedBefore).IsEqualTo(_ChunksFor(children + 1));
        await Assert.That(allocatedFirst).IsEqualTo(allocatedBefore);
        await Assert.That(allocatedSecond).IsEqualTo(allocatedBefore);
        await Assert.That(sameFirst).IsTrue();
        await Assert.That(sameSecond).IsTrue();
        await Assert.That(firstTree.SequenceEqual(_Range(100, children))).IsTrue();
        await Assert.That(secondTree.SequenceEqual(_Range(200, children))).IsTrue();
    }

    [Test]
    public async Task RecycleLeavesKeptChunksZeroBeyondTheNewCount()
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = _PacketWithChildren(fields, 50);
        _Recycle(fields, packet, 1, 3, valueOffset: 500);

        // Slots 0..3 hold the root and the three new children; everything else in every kept chunk must be zero.
        List<FieldBody> slots = _AllSlots(packet);
        bool restZero = true;
        for (int i = 4; i < slots.Count; i++)
        {
            restZero &= _IsZero(slots[i]);
        }

        await Assert.That(_AllocatedChunks(packet)).IsGreaterThan(0);
        await Assert.That(slots.Count).IsEqualTo(FieldStorage.FirstChunkSize + (_AllocatedChunks(packet) * FieldStorage.ChunkSize));
        await Assert.That(restZero).IsTrue();
        await Assert.That(_ChildValues(packet).SequenceEqual(new ulong[] { 500, 501, 502 })).IsTrue();
    }

    [Test]
    public async Task RecycleAfterALargeFrameFollowsTheRetentionPolicy()
    {
        (int Allocated, int PoolCount, int Descriptors, bool SameTree) result = await _OnFreshThread(static () =>
        {
            Fields fields = _BuildStack();
            using Stack stack = fields.Stack;
            Packet packet = _PacketWithChildren(fields, 600);
            List<(int FieldId, ulong Value)> large = _Dfs(packet, materialize: false);

            // A 5-child recycle after the 600-child parse (bounded: surplus to the pool, small descriptor table); then the
            // same 600-child parse again.
            _Recycle(fields, packet, 1, 5, valueOffset: 0);
            int allocated = _AllocatedChunks(packet);
            int poolCount = _PoolOnThisThread()?.Count ?? -1;
            int descriptors = _DescriptorCapacity(packet);
            _Recycle(fields, packet, 2, 600, valueOffset: 0);
            bool sameTree = _Dfs(packet, materialize: false).SequenceEqual(large);
            return (allocated, poolCount, descriptors, sameTree);
        });

        int used = _ChunksFor(601);
        int retained = FieldStorage.RetainedSlots / FieldStorage.ChunkSize;
        await Assert.That(result.Allocated).IsEqualTo(retained);
        await Assert.That(result.PoolCount).IsEqualTo(Math.Min(used - retained, FieldChunkPool.MaxPooledSlots / FieldStorage.ChunkSize));
        await Assert.That(result.Descriptors).IsEqualTo(Math.Max(retained, 4));
        await Assert.That(result.SameTree).IsTrue();
    }

    [Test]
    public async Task PoolHoldsAtMostItsCapacity()
    {
        (int Allocated, int PoolCount, int Capacity) result = await _OnFreshThread(static () =>
        {
            // Far more surplus than the pool holds: 2 000 children, then a 1-child recycle.
            Fields fields = _BuildStack();
            using Stack stack = fields.Stack;
            Packet packet = _PacketWithChildren(fields, 2_000);
            _Recycle(fields, packet, 1, 1, valueOffset: 0);
            FieldChunkPool? pool = _PoolOnThisThread();
            return (_AllocatedChunks(packet), pool?.Count ?? -1, pool?.Capacity ?? -1);
        });

        int capacity = FieldChunkPool.MaxPooledSlots / FieldStorage.ChunkSize;
        await Assert.That(result.Capacity).IsEqualTo(capacity);
        await Assert.That(result.PoolCount).IsEqualTo(capacity);
        await Assert.That(result.PoolCount * FieldStorage.ChunkSize).IsLessThanOrEqualTo(FieldChunkPool.MaxPooledSlots);
        await Assert.That(result.Allocated).IsEqualTo(FieldStorage.RetainedSlots / FieldStorage.ChunkSize);
    }

    [Test]
    public async Task PooledChunksCarryNoValuesOfTheEarlierPacket()
    {
        const ulong earlier = 1_000_000;
        (int PoolBefore, int PoolAfter, bool CleanB, bool CleanA) result = await _OnFreshThread(static () =>
        {
            Fields fields = _BuildStack();
            using Stack stack = fields.Stack;

            // Packet A: 600 children with values ≥ 1 000 000; the 5-child recycle passes its surplus chunks to the pool.
            Packet a = _PacketWithChildren(fields, 600, valueOffset: (int)earlier);
            _Recycle(fields, a, 1, 5, valueOffset: 0);
            int poolBefore = _PoolOnThisThread()?.Count ?? -1;

            // Packet B on the same thread takes pooled chunks first.
            Packet b = _PacketWithChildren(fields, 300);
            int poolAfter = _PoolOnThisThread()?.Count ?? -1;
            bool cleanB = _AllSlots(b).TrueForAll(static slot => _SlotU64(slot) < earlier);

            // A keeps its retained chunks; a 300-child recycle into them must not expose the earlier values either.
            _Recycle(fields, a, 2, 300, valueOffset: 0);
            bool cleanA = _AllSlots(a).TrueForAll(static slot => _SlotU64(slot) < earlier);
            return (poolBefore, poolAfter, cleanB, cleanA);
        });

        await Assert.That(result.PoolBefore).IsGreaterThan(0);
        await Assert.That(result.PoolAfter).IsLessThan(result.PoolBefore);
        await Assert.That(result.CleanB).IsTrue();
        await Assert.That(result.CleanA).IsTrue();
    }

    [Test]
    public async Task BuildSkipBuildRecycleKeepsCorrectTrees()
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = _PacketWithChildren(fields, 100);
        FieldBody[] firstChunk = _FirstChunk(_Storage(packet)).Buffer;

        // Build → Skip → Build on the same storage: the first chunk is reused, not re-initialized.
        _Recycle(fields, packet, 1, 0, valueOffset: 0, FieldTreeMode.Skip);
        int skipCount = packet.FieldCount(materialize: false);
        _Recycle(fields, packet, 2, 3, valueOffset: 40);
        List<ulong> afterSkip = _ChildValues(packet);

        await Assert.That(skipCount).IsEqualTo(1);
        await Assert.That(afterSkip.SequenceEqual(new ulong[] { 40, 41, 42 })).IsTrue();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(4);
        await Assert.That(ReferenceEquals(_FirstChunk(_Storage(packet)).Buffer, firstChunk)).IsTrue();
    }

    #endregion

    #region Field limit

    [Test]
    public async Task FieldLimitAndReservedErrorSlotStillHold()
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet packet = new(new PacketId(0), stack, _Frame(stack, 0));
        MutField root = packet.RootFieldMut();

        // Regular appends stop at 65 533 slots (root included).
        int appended = 0;
        bool threw = false;
        try
        {
            while (true)
            {
                _ = root.Append(fields.Item, FieldValue.NewU64((ulong)appended));
                appended++;
            }
        }
        catch (FieldAppendException)
        {
            threw = true;
        }

        int regularSlots = packet.FieldCount(materialize: false);

        // The caught error uses the reserved slot 65 533; afterwards neither path can add a slot.
        packet.RecordCaughtError(0, "full");
        int withError = packet.FieldCount(materialize: false);
        bool threwAgain = false;
        try
        {
            _ = root.Append(fields.Item, FieldValue.None);
        }
        catch (FieldAppendException)
        {
            threwAgain = true;
        }

        packet.RecordCaughtError(0, "dropped");
        packet.Seal();
        int flat = 0;
        foreach (Field field in packet.IterFieldsFlat(materialize: false))
        {
            flat++;
        }

        await Assert.That(threw).IsTrue();
        await Assert.That(regularSlots).IsEqualTo(65_533);
        await Assert.That(appended).IsEqualTo(65_532);
        await Assert.That(withError).IsEqualTo(65_534);
        await Assert.That(threwAgain).IsTrue();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(65_534);
        await Assert.That(flat).IsEqualTo(65_534);
        await Assert.That(packet.RootField().ChildCount(materialize: false)).IsEqualTo((ushort)65_533);
    }

    #endregion

    #region Concurrent lazy growth

    /// <summary>
    /// Builds a packet with 40 eager children and 200 lazy containers. Container k's populator appends three children
    /// (values 3k, 3k+1, 3k+2); every tenth also registers a nested lazy container with three more children.
    /// </summary>
    private static Packet _LazyPacket(Fields fields)
    {
        Packet packet = new(new PacketId(0), fields.Stack, _Frame(fields.Stack, 0));
        MutField root = packet.RootFieldMut();
        for (int i = 0; i < 40; i++)
        {
            _ = root.Append(fields.Item, FieldValue.NewU64((ulong)i));
        }

        for (int k = 0; k < 200; k++)
        {
            int captured = k;
            _ = root.AppendLazy(fields.Container, FieldValue.NewU64((ulong)(10_000 + k)), (in MutField container) =>
            {
                for (int j = 0; j < 3; j++)
                {
                    _ = container.Append(fields.Child, FieldValue.NewU64((ulong)((3 * captured) + j)));
                }

                if (captured % 10 == 0)
                {
                    _ = container.AppendLazy(fields.Nested, FieldValue.NewU64((ulong)(20_000 + captured)), (in MutField nested) =>
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            _ = nested.Append(fields.Child, FieldValue.NewU64((ulong)(30_000 + (3 * captured) + j)));
                        }

                        return 0;
                    });
                }

                return 0;
            });
        }

        packet.Seal();
        return packet;
    }

    /// <summary>
    /// Materializes <paramref name="packet"/> from eight threads through four read paths at once. Returns each thread's
    /// final DFS and how many fields with an id outside <paramref name="allowed"/> any thread saw.
    /// </summary>
    private static async Task<(List<(int FieldId, ulong Value)>[] Finals, int Foreign)> _MaterializeConcurrently(
        Fields fields, Packet packet, HashSet<int> allowed)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using Barrier start = new(8);
        List<(int FieldId, ulong Value)>[] finals = new List<(int, ulong)>[8];
        int foreign = 0;
        Task[] workers = new Task[8];
        for (int t = 0; t < workers.Length; t++)
        {
            int mode = t % 4;
            int slot = t;
            workers[t] = Task.Run(() =>
            {
                start.SignalAndWait(timeout.Token);

                // Mixed materializing readers; each checks that it never sees a slot without a real field id.
                switch (mode)
                {
                    case 0:
                        packet.MaterializeAll();
                        break;
                    case 1:
                        FieldLookupCookie cookie = default;
                        while (packet.TryGetNextField(fields.Child, ref cookie, out Field found, materialize: true))
                        {
                            if (!allowed.Contains(found.FieldId.Value))
                            {
                                _ = Interlocked.Increment(ref foreign);
                            }
                        }

                        break;
                    case 2:
                        foreach (Field field in packet.IterFieldsDfs(materialize: true))
                        {
                            if (!allowed.Contains(field.FieldId.Value))
                            {
                                _ = Interlocked.Increment(ref foreign);
                            }
                        }

                        break;
                    default:
                        foreach (Field container in packet.RootField().Children(materialize: true))
                        {
                            foreach (Field child in container.Children(materialize: true))
                            {
                                if (!allowed.Contains(child.FieldId.Value))
                                {
                                    _ = Interlocked.Increment(ref foreign);
                                }
                            }
                        }

                        break;
                }

                finals[slot] = _Dfs(packet, materialize: true);
            }, timeout.Token);
        }

        await Task.WhenAll(workers).WaitAsync(timeout.Token);
        return (finals, Volatile.Read(ref foreign));
    }

    [Test]
    public async Task PostSealLazyGrowthIntoChunksUnderEightThreads()
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        Packet reference = _LazyPacket(fields);
        reference.MaterializeAll();
        List<(int FieldId, ulong Value)> expected = _Dfs(reference, materialize: false);
        HashSet<int> allowed = [stack.RootFieldId.Value, fields.Item.Value, fields.Container.Value, fields.Child.Value, fields.Nested.Value];

        Packet packet = _LazyPacket(fields);
        (List<(int FieldId, ulong Value)>[] finals, int foreign) = await _MaterializeConcurrently(fields, packet, allowed);

        await Assert.That(foreign).IsEqualTo(0);
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(reference.FieldCount(materialize: false));
        await Assert.That(packet.FieldCount(materialize: false)).IsGreaterThan(FieldStorage.FirstChunkSize * 10);
        foreach (List<(int FieldId, ulong Value)> walk in finals)
        {
            await Assert.That(walk.SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task RecycleAfterConcurrentGrowthKeepsTreesCorrect()
    {
        Fields fields = _BuildStack();
        using Stack stack = fields.Stack;
        HashSet<int> allowed = [stack.RootFieldId.Value, fields.Item.Value, fields.Container.Value, fields.Child.Value, fields.Nested.Value];
        Packet packet = _LazyPacket(fields);
        (_, int foreign) = await _MaterializeConcurrently(fields, packet, allowed);

        // The worker threads appended the follow-up chunks; this thread recycles them (keeps or pools them).
        _Recycle(fields, packet, 1, 5, valueOffset: 0);
        List<ulong> small = _ChildValues(packet);
        int smallCount = packet.FieldCount(materialize: false);
        _Recycle(fields, packet, 2, 300, valueOffset: 1_000);
        List<ulong> large = _ChildValues(packet);
        List<(int FieldId, ulong Value)> largeDfs = _Dfs(packet, materialize: false);

        await Assert.That(foreign).IsEqualTo(0);
        await Assert.That(small.SequenceEqual(_Range(0, 5))).IsTrue();
        await Assert.That(smallCount).IsEqualTo(6);
        await Assert.That(large.SequenceEqual(_Range(1_000, 300))).IsTrue();
        await Assert.That(largeDfs.Count).IsEqualTo(301);
        await Assert.That(largeDfs.Skip(1).All(e => e.FieldId == fields.Item.Value)).IsTrue();
    }

    #endregion
}
