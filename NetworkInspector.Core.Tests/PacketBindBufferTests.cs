// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the single bind of the packet (<see cref="MutField.TryBindBuffer"/>) and the byte counter a protocol reads to apply its
/// own total (<see cref="MutField.BoundBytes"/>): the table of 255 buffers, the sealed packet, the sum and its reset on recycle, an
/// empty buffer, and the effect-layer key of a bound slice.
/// </summary>
internal sealed class PacketBindBufferTests
{
    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack, int frameId = 1) =>
        Frame.Create(
            frameId,
            Timestamp.FromSecs(frameId),
            FrameBuilders.GenerateStaticUdpFrame(64),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static Packet _NewPacket(Stack stack, int frameId = 1) => new(0, stack, _MakeFrame(stack, frameId), FieldTreeMode.Build);

    /// <summary>255 buffers are bound, the 256th is refused with the table failure, and the refusal changes neither the count nor the bytes.</summary>
    [Test]
    public async Task TheTableTakes255BuffersAndRefusesTheNext()
    {
        using Stack stack = _BuildStack();
        Packet packet = _NewPacket(stack);
        MutField root = packet.RootFieldMut();
        bool allBound = true;

        for (int index = 0; index < 255; index++)
        {
            allBound &= root.TryBindBuffer(new byte[2], out _, out _);
        }

        ulong bytesBefore = root.BoundBytes;
        bool refused = root.TryBindBuffer(new byte[2], out ReadOnlyMemory<byte> bound, out BufferBindFailure failure);
        ulong bytesAfter = root.BoundBytes; // read before the first await: a MutField cannot live across it

        await Assert.That(allBound).IsTrue();
        await Assert.That(refused).IsFalse();
        await Assert.That(failure).IsEqualTo(BufferBindFailure.TableFull);
        await Assert.That(bound.IsEmpty).IsTrue();
        await Assert.That(packet.BufferCount).IsEqualTo(256);
        await Assert.That(bytesAfter).IsEqualTo(bytesBefore);
        await Assert.That(bytesBefore).IsEqualTo(510UL);
    }

    /// <summary>After Seal the bind reports the sealed failure and stores nothing.</summary>
    [Test]
    public async Task ABindAfterSealReportsSealed()
    {
        using Stack stack = _BuildStack();
        Packet packet = _NewPacket(stack);
        packet.Seal();

        bool ok = packet.RootFieldMut().TryBindBuffer(new byte[4], out ReadOnlyMemory<byte> bound, out BufferBindFailure failure);

        await Assert.That(ok).IsFalse();
        await Assert.That(failure).IsEqualTo(BufferBindFailure.Sealed);
        await Assert.That(bound.IsEmpty).IsTrue();
        await Assert.That(packet.BufferCount).IsEqualTo(1);
        await Assert.That(packet.AdditionalBufferBytes).IsEqualTo(0UL);
    }

    /// <summary>The counter sums the lengths of what was bound, whoever bound it, and starts at 0 again for a recycled packet.</summary>
    [Test]
    public async Task BoundBytesSumsTheBuffersAndRestartsAfterRecycle()
    {
        using Stack stack = _BuildStack();
        Packet packet = _NewPacket(stack);
        MutField root = packet.RootFieldMut();
        ulong empty = root.BoundBytes;

        _ = root.TryBindBuffer(new byte[10], out _, out _);
        _ = root.TryBindBuffer(new byte[20], out _, out _);
        ulong bound = root.BoundBytes;
        packet.Seal();
        RecycleError? recycled = packet.PrepareForReuse(1, stack, _MakeFrame(stack, 2), FieldTreeMode.Build);

        await Assert.That(empty).IsEqualTo(0UL);
        await Assert.That(bound).IsEqualTo(30UL);
        await Assert.That(recycled).IsNull();
        await Assert.That(packet.RootFieldMut().BoundBytes).IsEqualTo(0UL);
    }

    /// <summary>An empty buffer is stored like any other: it takes a slot, adds no bytes, and later buffers keep their index.</summary>
    [Test]
    public async Task AnEmptyBufferTakesASlotAndAddsNoBytes()
    {
        using Stack stack = _BuildStack();
        Packet packet = _NewPacket(stack);
        MutField root = packet.RootFieldMut();

        bool emptyOk = root.TryBindBuffer(ReadOnlyMemory<byte>.Empty, out ReadOnlyMemory<byte> emptyBound, out BufferBindFailure emptyFailure);
        bool nextOk = root.TryBindBuffer(new byte[] { 1, 2, 3 }, out ReadOnlyMemory<byte> nextBound, out _);
        bool keyOk = root.TryGetEffectLayerKey(nextBound, out int key);
        ulong bytes = root.BoundBytes; // read before the first await: a MutField cannot live across it

        await Assert.That(emptyOk).IsTrue();
        await Assert.That(emptyFailure).IsEqualTo(BufferBindFailure.None);
        await Assert.That(emptyBound.IsEmpty).IsTrue();
        await Assert.That(nextOk).IsTrue();
        await Assert.That(bytes).IsEqualTo(3UL);
        await Assert.That(keyOk).IsTrue();
        await Assert.That(key >>> 24).IsEqualTo(2);
    }

    /// <summary>A bound slice resolves an effect-layer key with the buffer index in bits 31–24, and a reparse binding the same bytes in the same order gets the same key.</summary>
    [Test]
    public async Task ABoundSliceHasAnEffectKeyThatRepeatsOnReparse()
    {
        using Stack stack = _BuildStack();
        Packet packet = _NewPacket(stack);
        byte[] bytes = new byte[16];
        _ = packet.RootFieldMut().TryBindBuffer(bytes, out ReadOnlyMemory<byte> first, out _);
        bool firstOk = packet.TryGetEffectLayerKey(first.Slice(4, 8), out int firstKey);
        packet.Seal();

        RecycleError? recycled = packet.PrepareForReuse(1, stack, _MakeFrame(stack, 2), FieldTreeMode.Build);
        _ = packet.RootFieldMut().TryBindBuffer(bytes, out ReadOnlyMemory<byte> second, out _);
        bool secondOk = packet.TryGetEffectLayerKey(second.Slice(4, 8), out int secondKey);

        await Assert.That(recycled).IsNull();
        await Assert.That(firstOk).IsTrue();
        await Assert.That(firstKey >>> 24).IsEqualTo(1);
        await Assert.That(firstKey & 0xFFFFFF).IsEqualTo(4);
        await Assert.That(secondOk).IsTrue();
        await Assert.That(secondKey).IsEqualTo(firstKey);
    }
}
