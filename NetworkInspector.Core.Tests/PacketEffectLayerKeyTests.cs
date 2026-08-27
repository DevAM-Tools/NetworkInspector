// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Pins <see cref="Packet.GetEffectLayerKey"/> packing and slice-identity rules.
/// </summary>
internal sealed class PacketEffectLayerKeyTests
{
    [Test]
    public async Task GetEffectLayerKey_FrameSlice_PacksBufferZeroAndOffset()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        int key = packet.GetEffectLayerKey(packet.Frame.Data.Slice(14, 8));

        await Assert.That(key).IsEqualTo(14);
    }

    [Test]
    public async Task GetEffectLayerKey_BoundFrameSlice_StillPacksAsFrame()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        ReadOnlyMemory<byte> slice = packet.Frame.Data.Slice(14, 8);
        _ = packet.BindParseBuffer(slice);

        int key = packet.GetEffectLayerKey(slice);

        await Assert.That(key).IsEqualTo(14);
        await Assert.That(key).IsNotEqualTo(1 << 24);
    }

    [Test]
    public async Task GetEffectLayerKey_AdditionalBuffer_PacksBufferIndexOne()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        _ = packet.AddBuffer(extra);

        int key = packet.GetEffectLayerKey(extra.AsMemory().Slice(2, 4));

        await Assert.That(key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task GetEffectLayerKey_MemoryManagerSlice_PacksBufferIndexOne()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        using ArrayMemoryManager manager = new(extra);
        _ = packet.AddBuffer(manager.Memory);

        int key = packet.GetEffectLayerKey(manager.Memory.Slice(2, 4));

        await Assert.That(key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task GetEffectLayerKey_EmptySlice_PacksOffset()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        int key = packet.GetEffectLayerKey(packet.Frame.Data.Slice(14, 0));

        await Assert.That(key).IsEqualTo(14);
        await Assert.That(key).IsNotEqualTo(0);
    }

    [Test]
    public async Task GetEffectLayerKey_EmptyData_ThrowsInvalidOperationException()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        await Assert.That(() => packet.GetEffectLayerKey(ReadOnlyMemory<byte>.Empty))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task GetEffectLayerKey_MemoryManagerEmptySlice_PacksOffset()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        using ArrayMemoryManager manager = new(extra);
        _ = packet.AddBuffer(manager.Memory);

        int key = packet.GetEffectLayerKey(manager.Memory.Slice(2, 0));

        await Assert.That(key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task GetEffectLayerKey_TwoEmptySlices_RecordDistinctKeys()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        int key14 = packet.GetEffectLayerKey(packet.Frame.Data.Slice(14, 0));
        int key20 = packet.GetEffectLayerKey(packet.Frame.Data.Slice(20, 0));
        EffectStore<int> store = new();
        store.Record(0, key14, 1);
        store.Record(0, key20, 2);

        await Assert.That(store.TryGet(0, key14, out int first) && first == 1).IsTrue();
        await Assert.That(store.TryGet(0, key20, out int second) && second == 2).IsTrue();
    }

    [Test]
    public async Task GetEffectLayerKey_CopyNotSlice_ThrowsInvalidOperationException()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        byte[] copy = packet.Frame.Data.ToArray();

        await Assert.That(() => packet.GetEffectLayerKey(copy))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PackEffectLayerKey_Buffer256_Throws()
    {
        await Assert.That(() => Packet.PackEffectLayerKeyForTests(256, 0))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackEffectLayerKey_OffsetTooLarge_Throws()
    {
        await Assert.That(() => Packet.PackEffectLayerKeyForTests(0, 0x1000000))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task PackEffectLayerKey_MaxValues_Packs()
    {
        int key = Packet.PackEffectLayerKeyForTests(255, 0xFFFFFF);

        await Assert.That(key).IsEqualTo((255 << 24) | 0xFFFFFF);
    }

    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack)
    {
        byte[] data = new byte[64];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 0x0800);
        return Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
    }

    /// <summary>
    /// Memory manager used to pin the non-array <see cref="Packet.GetEffectLayerKey"/> path.
    /// </summary>
    private sealed class ArrayMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _Buffer;

        public ArrayMemoryManager(byte[] buffer) => _Buffer = buffer;

        public override Span<byte> GetSpan() => _Buffer;

        public override MemoryHandle Pin(int elementIndex = 0) => default;

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
