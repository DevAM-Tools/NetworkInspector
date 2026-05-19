// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Protocols;

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

    private static (Stack Stack, MockIterProto Proto, ProtocolId ProtoId) BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MockIterProto proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        return (stack, proto, protoId);
    }

    private static Packet ParseFrame(Stack stack, byte[] data, ProtocolId firstProtocolId)
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
        (Stack? stack, MockIterProto _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, _, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
        (Stack? stack, MockIterProto? proto, ProtocolId protoId) = BuildStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = ParseFrame(stack, data, protoId);

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
                ParseContext context = default;
                container.Append(dstFieldId, FieldValue.NewMacAddress(dst), in context);
                container.Append(srcFieldId, FieldValue.NewMacAddress(src), in context);
                container.Append(typeFieldId, FieldValue.NewU64(ethertype), in context);
                return 0;
            });

            return 14;
        }
    }
}