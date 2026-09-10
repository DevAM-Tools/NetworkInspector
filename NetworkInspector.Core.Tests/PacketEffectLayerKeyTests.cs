// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Pins <see cref="Packet.TryGetEffectLayerKey"/> packing and slice-identity rules.
/// Additional-buffer binds run inside <see cref="IProtocol.Parse"/> before Seal.
/// </summary>
internal sealed class PacketEffectLayerKeyTests
{
    [Test]
    public async Task TryGetEffectLayerKey_FrameSlice_PacksBufferZeroAndOffset()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        bool ok = packet.TryGetEffectLayerKey(packet.Frame.Data.Slice(14, 8), out int key);

        await Assert.That(ok).IsTrue();
        await Assert.That(key).IsEqualTo(14);
    }

    [Test]
    public async Task TryGetEffectLayerKey_BoundFrameSlice_StillPacksAsFrame()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        BindDuringParseProtocol proto = new(bindFrameSlice: true);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        using Stack stack = builder.Build();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack), protoId);

        await Assert.That(proto.KeyOk).IsTrue();
        await Assert.That(proto.Key).IsEqualTo(14);
        await Assert.That(proto.Key).IsNotEqualTo(1 << 24);
        await Assert.That(packet.BufferCount).IsEqualTo(2);
    }

    [Test]
    public async Task TryGetEffectLayerKey_AdditionalBuffer_PacksBufferIndexOne()
    {
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        BindDuringParseProtocol proto = new(extra: extra, sliceStart: 2, sliceLength: 4);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        using Stack stack = builder.Build();
        _ = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack), protoId);

        await Assert.That(proto.KeyOk).IsTrue();
        await Assert.That(proto.Key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task TryGetEffectLayerKey_MemoryManagerSlice_PacksBufferIndexOne()
    {
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        using ArrayMemoryManager manager = new(extra);
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        BindDuringParseProtocol proto = new(extra: manager.Memory, sliceStart: 2, sliceLength: 4);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        using Stack stack = builder.Build();
        _ = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack), protoId);

        await Assert.That(proto.KeyOk).IsTrue();
        await Assert.That(proto.Key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task TryGetEffectLayerKey_EmptySlice_PacksOffset()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        bool ok = packet.TryGetEffectLayerKey(packet.Frame.Data.Slice(14, 0), out int key);

        await Assert.That(ok).IsTrue();
        await Assert.That(key).IsEqualTo(14);
        await Assert.That(key).IsNotEqualTo(0);
    }

    [Test]
    public async Task TryGetEffectLayerKey_EmptyData_ReturnsFalse()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));

        bool ok = packet.TryGetEffectLayerKey(ReadOnlyMemory<byte>.Empty, out int key);

        await Assert.That(ok).IsFalse();
        await Assert.That(key).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetEffectLayerKey_MemoryManagerEmptySlice_PacksOffset()
    {
        byte[] extra = [1, 2, 3, 4, 5, 6, 7, 8];
        using ArrayMemoryManager manager = new(extra);
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        BindDuringParseProtocol proto = new(extra: manager.Memory, sliceStart: 2, sliceLength: 0);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        using Stack stack = builder.Build();
        _ = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack), protoId);

        await Assert.That(proto.KeyOk).IsTrue();
        await Assert.That(proto.Key).IsEqualTo((1 << 24) | 2);
    }

    [Test]
    public async Task TryGetEffectLayerKey_TwoEmptySlices_RecordDistinctKeys()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        bool ok14 = packet.TryGetEffectLayerKey(packet.Frame.Data.Slice(14, 0), out int key14);
        bool ok20 = packet.TryGetEffectLayerKey(packet.Frame.Data.Slice(20, 0), out int key20);
        EffectStore<int> store = new();
        store.Record(0, key14, 1);
        store.Record(0, key20, 2);

        await Assert.That(ok14 && ok20).IsTrue();
        await Assert.That(store.TryGet(0, key14, out int first) && first == 1).IsTrue();
        await Assert.That(store.TryGet(0, key20, out int second) && second == 2).IsTrue();
    }

    [Test]
    public async Task TryGetEffectLayerKey_CopyNotSlice_ReturnsFalse()
    {
        using Stack stack = _BuildStack();
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, _MakeFrame(stack));
        byte[] copy = packet.Frame.Data.ToArray();

        bool ok = packet.TryGetEffectLayerKey(copy, out int key);

        await Assert.That(ok).IsFalse();
        await Assert.That(key).IsEqualTo(0);
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
    /// Binds an extra buffer or a frame slice during Parse so keys are taken before Seal.
    /// </summary>
    private sealed class BindDuringParseProtocol : IProtocol
    {
        private readonly ReadOnlyMemory<byte> _Extra;
        private readonly int _SliceStart;
        private readonly int _SliceLength;
        private readonly bool _BindFrameSlice;

        public BindDuringParseProtocol(
            ReadOnlyMemory<byte> extra = default,
            int sliceStart = 0,
            int sliceLength = 0,
            bool bindFrameSlice = false)
        {
            _Extra = extra;
            _SliceStart = sliceStart;
            _SliceLength = sliceLength;
            _BindFrameSlice = bindFrameSlice;
        }

        public string Name => "bind.parse";
        public string UiName => "Bind Parse";
        public int Key { get; private set; }
        public bool KeyOk { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (_BindFrameSlice)
            {
                ReadOnlyMemory<byte> slice = data.Slice(14, 8);
                _ = parentField.BindParseBuffer(slice);
                KeyOk = parentField.TryGetEffectLayerKey(slice, out int key);
                Key = key;
                return data.Length;
            }

            ReadOnlyMemory<byte> bound = parentField.BindParseBuffer(_Extra);
            KeyOk = parentField.TryGetEffectLayerKey(bound.Slice(_SliceStart, _SliceLength), out int extraKey);
            Key = extraKey;
            return data.Length;
        }
    }

    /// <summary>
    /// Memory manager used to pin the non-array <see cref="Packet.TryGetEffectLayerKey"/> path.
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
