// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the field iteration APIs: Children, Descendants, IterFieldsDfs, IterFieldsFlat.
/// Covers both with and without lazy materialization.
/// </summary>
internal sealed class FieldIteratorTests
{
    // =========================================================================
    // Helpers — build a small packet tree:
    //   root
    //     packet (container, eager — from PacketProtocol)
    //       packet.id
    //       packet.timestamp
    //       packet.frame_source_id
    //       packet.info
    //     eth (container, lazily populated)
    //       eth.dst
    //       eth.src
    //       eth.type
    // =========================================================================

    private static (Stack Stack, MockIterProto Proto, ProtocolId ProtoId) _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MockIterProto proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        return (stack, proto, protoId);
    }

    private static Packet _ParseFrame(Stack stack, byte[] data, ProtocolId firstProtocolId)
    {
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(
            new PacketId(1),
            stack,
            frame,
            firstProtocolId);
    }

    // =========================================================================
    // Children — with materialization (default)
    // =========================================================================

    [Test]
    public async Task Children_DefaultMaterialize_YieldsAllChildren()
    {
        (Stack? stack, MockIterProto _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            // Navigate past packet container (first child) to eth container (second child)
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container); // eth container (lazy)

            int childCount = 0;
            foreach (Field _ in container.Children())
            {
                childCount++;
            }

            // 3 children: eth.dst, eth.src, eth.type
            await Assert.That(childCount).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Children_WithMaterialize_MaterializesLazyContainer()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            // Navigate past packet container to eth container
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);
            bool lazyBefore = container.NeedsLazyMaterialization;

            // Iterating with materialize=true (default) populates the container
            foreach (Field _ in container.Children())
            {
            }

            bool lazyAfter = container.NeedsLazyMaterialization;

            await Assert.That(lazyBefore).IsTrue();
            await Assert.That(lazyAfter).IsFalse();
        }
    }

    [Test]
    public async Task Children_WithoutMaterialize_DoesNotPopulateLazy()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            // Navigate past packet container to eth container (lazy)
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            // Iterate container's children without materializing
            int childCount = 0;
            foreach (Field _ in container.Children(materialize: false))
            {
                childCount++;
            }

            await Assert.That(childCount).IsEqualTo(0);                   // no children yet
            await Assert.That(proto.PopulateCallCount).IsEqualTo(0);      // not called
            await Assert.That(container.NeedsLazyMaterialization).IsTrue(); // still lazy
        }
    }

    [Test]
    public async Task Children_WithoutMaterialize_YieldsEagerChildren()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Root itself is not lazy — iterating its eager children works
            Field root = packet.RootField();
            int childCount = 0;
            foreach (Field _ in root.Children(materialize: false))
            {
                childCount++;
            }

            // Root has two eager children: packet container + eth container
            await Assert.That(childCount).IsEqualTo(2);
        }
    }

    // =========================================================================
    // Descendants — with and without materialization
    // =========================================================================

    [Test]
    public async Task Descendants_DefaultMaterialize_YieldsAllDescendants()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            int count = 0;
            foreach (Field _ in root.Descendants())
            {
                count++;
            }

            // root's descendants: packet(1) + 4 packet children + eth(1) + 3 eth children = 9
            await Assert.That(count).IsEqualTo(9);
        }
    }

    [Test]
    public async Task Descendants_WithoutMaterialize_OnlyEagerDescendants()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            int count = 0;
            foreach (Field _ in root.Descendants(materialize: false))
            {
                count++;
            }

            // packet container(1) + 4 packet children + eth lazy container(1) = 6
            await Assert.That(count).IsEqualTo(6);
            await Assert.That(proto.PopulateCallCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Descendants_ExcludesRoot()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            bool rootFound = false;
            foreach (Field f in root.Descendants())
            {
                if (f.IsRoot)
                {
                    rootFound = true;
                }
            }

            await Assert.That(rootFound).IsFalse();
            await Assert.That(proto.PopulateCallCount).IsEqualTo(1);
        }
    }

    // =========================================================================
    // IterFieldsDfs — with and without materialization
    // =========================================================================

    [Test]
    public async Task IterFieldsDfs_DefaultMaterialize_VisitsAllFields()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            int count = 0;
            foreach (Field _ in packet.IterFieldsDfs())
            {
                count++;
            }

            // root + packet(1) + 4 packet children + eth container + 3 leaf fields = 10
            await Assert.That(count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task IterFieldsDfs_WithoutMaterialize_OnlyEagerFields()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            int count = 0;
            foreach (Field _ in packet.IterFieldsDfs(materialize: false))
            {
                count++;
            }

            // root + packet(1) + 4 packet children + eth container (lazy children not expanded) = 7
            await Assert.That(count).IsEqualTo(7);
            await Assert.That(proto.PopulateCallCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task IterFieldsDfs_IncludesRoot()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            bool rootFound = false;
            foreach (Field f in packet.IterFieldsDfs())
            {
                if (f.IsRoot)
                {
                    rootFound = true;
                }
            }

            await Assert.That(rootFound).IsTrue();
        }
    }

    [Test]
    public async Task IterFieldsDfs_DfsOrder_ParentBeforeChildren()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            List<FieldId> order = [];
            foreach (Field f in packet.IterFieldsDfs())
            {
                order.Add(f.FieldId);
            }

            // eth container must appear before its children
            int containerIdx = order.IndexOf(proto.ContainerFieldId);
            int dstIdx = order.IndexOf(proto.DstFieldId);
            int srcIdx = order.IndexOf(proto.SrcFieldId);
            int typeIdx = order.IndexOf(proto.TypeFieldId);

            await Assert.That(containerIdx).IsLessThan(dstIdx);
            await Assert.That(containerIdx).IsLessThan(srcIdx);
            await Assert.That(containerIdx).IsLessThan(typeIdx);
        }
    }

    // =========================================================================
    // IterFieldsFlat — linear storage-order iteration
    // =========================================================================

    [Test]
    public async Task IterFieldsFlat_DefaultMaterialize_VisitsAllFields()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            int count = 0;
            foreach (Field _ in packet.IterFieldsFlat())
            {
                count++;
            }

            // root + packet(1) + 4 packet children + eth container + 3 leaf fields = 10
            await Assert.That(count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task IterFieldsFlat_WithoutMaterialize_OnlyEagerFields()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            int count = 0;
            foreach (Field _ in packet.IterFieldsFlat(materialize: false))
            {
                count++;
            }

            // root + packet(1) + 4 packet children + eth container = 7 (lazy children not materialized)
            await Assert.That(count).IsEqualTo(7);
            await Assert.That(proto.PopulateCallCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task IterFieldsFlat_WithMaterialize_MaterializesLazyFields()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            foreach (Field _ in packet.IterFieldsFlat())
            {
            }

            await Assert.That(proto.PopulateCallCount).IsEqualTo(1);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
        }
    }

    [Test]
    public async Task IterFieldsFlat_StorageOrder_AllIndicesUnique()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            List<ushort> indices = [];
            foreach (Field f in packet.IterFieldsFlat())
            {
                indices.Add(f.StorageIndex);
            }

            // All storage indices must be unique and ascending (linear walk)
            for (int i = 1; i < indices.Count; i++)
            {
                await Assert.That(indices[i]).IsGreaterThan(indices[i - 1]);
            }
        }
    }

    // =========================================================================
    // MutField iteration — Children and Descendants are also available on MutField
    // =========================================================================

    [Test]
    public async Task MutField_Children_DefaultMaterialize_YieldsAllChildren()
    {
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Use the packet's root MutField to verify MutField iteration
            // Navigate past packet container to eth container
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            // Access via AsField → get same result as Field.Children()
            int count = 0;
            foreach (Field _ in container.Children())
            {
                count++;
            }

            await Assert.That(count).IsEqualTo(3);
            await Assert.That(proto.PopulateCallCount).IsEqualTo(1);
        }
    }

    // =========================================================================
    // Field API — navigation false paths, validity, equality
    // =========================================================================

    [Test]
    public async Task Field_Default_IsInvalid()
    {
        Field invalid = default;

        await Assert.That(invalid.IsValid).IsFalse();
    }

    [Test]
    public async Task Field_Root_HasNoParentOrPrev()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();

            await Assert.That(root.TryGetParent(out _)).IsFalse();
            await Assert.That(root.TryGetPrev(out _)).IsFalse();
            await Assert.That(root.IsRoot).IsTrue();
            await Assert.That(root.Packet).IsSameReferenceAs(packet);
            await Assert.That(root.FieldInfo).IsNotNull();
        }
    }

    [Test]
    public async Task Field_Leaf_HasNoChildrenOrNext()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);
            container.TryGetLastChild(out Field leaf);

            await Assert.That(leaf.TryGetFirstChild(out _)).IsFalse();
            await Assert.That(leaf.TryGetLastChild(out _)).IsFalse();
            await Assert.That(leaf.TryGetNext(out _)).IsFalse();
            await Assert.That(leaf.Value.Type).IsNotEqualTo(FieldType.None);
        }
    }

    [Test]
    public async Task Field_LastChild_AndPrevNavigation()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            await Assert.That(container.TryGetLastChild(out Field lastChild)).IsTrue();
            await Assert.That(lastChild.TryGetPrev(out Field prev)).IsTrue();
            await Assert.That(prev.TryGetNext(out Field next)).IsTrue();
            await Assert.That(next.FieldId).IsEqualTo(lastChild.FieldId);
        }
    }

    [Test]
    public async Task Field_Equality_OperatorsAndHashCode()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            Field same = packet.RootField();
            Packet otherPacket = _ParseFrame(stack, new byte[14], protoId);
            Field different = otherPacket.RootField();

            await Assert.That(root == same).IsTrue();
            await Assert.That(root != different).IsTrue();
            await Assert.That(root.Equals(same)).IsTrue();
            await Assert.That(root.Equals((object)same)).IsTrue();
            await Assert.That(root.Equals((object)"not a field")).IsFalse();
            await Assert.That(root.GetHashCode()).IsEqualTo(same.GetHashCode());
        }
    }

    [Test]
    public async Task Children_AsIEnumerable_NonGenericGetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            IEnumerable nonGeneric = container.Children();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            _ = enumerator.Current;
            enumerator.Reset();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task Children_BoxedEnumerator_ExhaustionReturnsFalse()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            IEnumerable<Field> enumerable = container.Children();
            using IEnumerator<Field> enumerator = enumerable.GetEnumerator();
            int count = 0;
            while (enumerator.MoveNext())
            {
                count++;
            }

            bool exhausted = enumerator.MoveNext();
            await Assert.That(count).IsEqualTo(3);
            await Assert.That(exhausted).IsFalse();
        }
    }

    [Test]
    public async Task Children_StructEnumerator_ExhaustionReturnsFalse()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            FieldChildEnumerator enumerator = container.Children().GetEnumerator();
            int count = 0;
            while (enumerator.MoveNext())
            {
                count++;
            }

            bool exhausted = enumerator.MoveNext();

            await Assert.That(count).IsEqualTo(3);
            await Assert.That(exhausted).IsFalse();
        }
    }

    [Test]
    public async Task Descendants_AsIEnumerable_NonGenericGetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.RootField().Descendants();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task IterFieldsDfs_AsIEnumerable_NonGenericGetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.IterFieldsDfs();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task IterFieldsFlat_AsIEnumerable_NonGenericGetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.IterFieldsFlat();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task InlineStack16_PushSlow_PopHeapPath_AndEmptyThrow()
    {
        InlineStack16 stack16 = default;
        for (int i = 0; i < 17; i++)
        {
            stack16.Push((ushort)i);
        }

        int count = stack16.Count;
        ushort last = default;
        while (stack16.Count > 0)
        {
            last = stack16.Pop();
        }

        bool popThrew = false;
        try
        {
            stack16.Pop();
        }
        catch (InvalidOperationException)
        {
            popThrew = true;
        }

        await Assert.That(count).IsEqualTo(17);
        await Assert.That(last).IsEqualTo((ushort)0);
        await Assert.That(popThrew).IsTrue();
    }

    // =========================================================================

    [Test]
    public async Task Children_AsIEnumerable_UsesBoxedEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            IEnumerable<Field> enumerable = container.Children();
            await Assert.That(enumerable.Count()).IsEqualTo(3);

            using IEnumerator<Field> enumerator = enumerable.GetEnumerator();
            await Assert.That(enumerator.MoveNext()).IsTrue();
            Field first = enumerator.Current;
            object boxed = ((IEnumerator)enumerator).Current;
            await Assert.That(boxed).IsTypeOf<Field>();
            await Assert.That(((Field)boxed).FieldId).IsEqualTo(first.FieldId);
            enumerator.Reset();
            await Assert.That(enumerator.MoveNext()).IsTrue();
            ((IDisposable)enumerator).Dispose();
        }
    }

    [Test]
    public async Task Descendants_AsIEnumerable_UsesBoxedEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable<Field> enumerable = packet.RootField().Descendants();

            await Assert.That(enumerable.Count()).IsEqualTo(9);

            using IEnumerator<Field> enumerator = enumerable.GetEnumerator();
            await Assert.That(enumerator.MoveNext()).IsTrue();
            _ = ((IEnumerator)enumerator).Current;
            enumerator.Reset();
            ((IDisposable)enumerator).Dispose();
        }
    }

    [Test]
    public async Task IterFieldsDfs_AsIEnumerable_UsesBoxedEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable<Field> enumerable = packet.IterFieldsDfs();

            await Assert.That(enumerable.Count()).IsGreaterThan(5);

            using IEnumerator<Field> enumerator = enumerable.GetEnumerator();
            await Assert.That(enumerator.MoveNext()).IsTrue();
            _ = ((IEnumerator)enumerator).Current;
            enumerator.Reset();
            ((IDisposable)enumerator).Dispose();
        }
    }

    [Test]
    public async Task IterFieldsFlat_AsIEnumerable_UsesBoxedEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable<Field> enumerable = packet.IterFieldsFlat();

            await Assert.That(enumerable.Count()).IsGreaterThan(5);

            using IEnumerator<Field> enumerator = enumerable.GetEnumerator();
            await Assert.That(enumerator.MoveNext()).IsTrue();
            _ = ((IEnumerator)enumerator).Current;
            enumerator.Reset();
            ((IDisposable)enumerator).Dispose();
        }
    }

    // =========================================================================
    // Struct enumerators — empty children, exhaust, non-generic IEnumerable
    // =========================================================================

    [Test]
    public async Task Children_StructEnumerator_EmptyParent_ReturnsFalse()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);
            container.TryGetLastChild(out Field leaf);

            FieldChildEnumerator enumerator = leaf.Children().GetEnumerator();
            bool first = enumerator.MoveNext();
            bool second = enumerator.MoveNext();

            await Assert.That(first).IsFalse();
            await Assert.That(second).IsFalse();
        }
    }

    [Test]
    public async Task Children_BoxedEnumerator_ExhaustsThenReturnsFalse()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            int count = 0;
            foreach (Field _ in container.Children())
            {
                count++;
            }

            FieldChildEnumerator tail = container.Children().GetEnumerator();
            while (tail.MoveNext())
            {
            }

            bool afterExhaust = tail.MoveNext();
            await Assert.That(count).IsEqualTo(3);
            await Assert.That(afterExhaust).IsFalse();
        }
    }

    [Test]
    public async Task Children_NonGenericIEnumerable_GetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.RootField().Children();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            object current = enumerator.Current;
            enumerator.Reset();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
            await Assert.That(current).IsTypeOf<Field>();
        }
    }

    [Test]
    public async Task Descendants_NonGenericIEnumerable_GetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.RootField().Descendants();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task IterFieldsDfs_NonGenericIEnumerable_GetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.IterFieldsDfs();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task IterFieldsFlat_NonGenericIEnumerable_GetEnumerator()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            IEnumerable nonGeneric = packet.IterFieldsFlat();
            IEnumerator enumerator = nonGeneric.GetEnumerator();
            bool moved = enumerator.MoveNext();
            if (enumerator is IDisposable disposable)
            {
                disposable.Dispose();
            }

            await Assert.That(moved).IsTrue();
        }
    }

    [Test]
    public async Task Field_HasChildrenAndChildCount_OnContainer()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildStack();
        using (stack)
        {
            Packet packet = _ParseFrame(stack, new byte[14], protoId);
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            await Assert.That(container.HasChildren).IsTrue();
            await Assert.That(container.ChildCount).IsEqualTo((ushort)3);
        }
    }

    [Test]
    public async Task InlineStack16_PushBeyondInlineCapacity_UsesSlowPath()
    {
        InlineStack16 stack = default;
        for (int i = 0; i < 20; i++)
        {
            stack.Push((ushort)i);
        }

        int count = stack.Count;
        ushort top = stack.Pop();

        await Assert.That(count).IsEqualTo(20);
        await Assert.That(top).IsEqualTo((ushort)19);
    }

    [Test]
    public async Task InlineStack16_PopOnEmpty_Throws()
    {
        InlineStack16 stack = default;
        bool threw = false;
        try
        {
            stack.Pop();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    // =========================================================================
    // Mock protocol — simple flat Ethernet-like layout with lazy container
    // =========================================================================

    private sealed class MockIterProto : IProtocol
    {
        private FieldId _ContainerFieldId;
        private FieldId _DstFieldId;
        private FieldId _SrcFieldId;
        private FieldId _TypeFieldId;
        private int _PopulateCallCount;

        public string Name => "eth";
        public string UiName => "Ethernet";

        public FieldId ContainerFieldId => _ContainerFieldId;
        public FieldId DstFieldId => _DstFieldId;
        public FieldId SrcFieldId => _SrcFieldId;
        public FieldId TypeFieldId => _TypeFieldId;
        public int PopulateCallCount => _PopulateCallCount;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "eth", "Ethernet", FieldType.None);
            _DstFieldId = builder.RegisterField(protocolId, "eth.dst", "Destination", FieldType.MacAddress);
            _SrcFieldId = builder.RegisterField(protocolId, "eth.src", "Source", FieldType.MacAddress);
            _TypeFieldId = builder.RegisterField(protocolId, "eth.type", "Type", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (data.Length < 14)
            {
                return ParseError.InsufficientDataWithInfo("eth", 14, (ulong)data.Length);
            }

            // Parse eagerly but populate tree lazily
            byte[] span = data.ToArray();
            MacAddress dst = MacAddress.FromBytes(span.AsSpan(0, 6));
            MacAddress src = MacAddress.FromBytes(span.AsSpan(6, 6));
            ushort ethertype = (ushort)((span[12] << 8) | span[13]);

            FieldId dstFieldId = _DstFieldId;
            FieldId srcFieldId = _SrcFieldId;
            FieldId typeFieldId = _TypeFieldId;

            parentField.AppendLazy(_ContainerFieldId, FieldValue.None, (in container) =>
            {
                System.Threading.Interlocked.Increment(ref _PopulateCallCount);
                container.Append(dstFieldId, FieldValue.NewMacAddress(dst));
                container.Append(srcFieldId, FieldValue.NewMacAddress(src));
                container.Append(typeFieldId, FieldValue.NewU64(ethertype));
                return 0;
            });

            return 14;
        }
    }
}


