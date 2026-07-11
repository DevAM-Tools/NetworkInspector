// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for lazy field population behavior.
/// Verifies that lazy containers defer materialization until first child access.
/// </summary>
internal sealed class LazyFieldTests
{
    private static (Stack Stack, MockLazyProtocol Proto, ProtocolId ProtoId) _BuildLazyStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MockLazyProtocol proto = new();
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

    [Test]
    public async Task LazyField_FieldCountBeforeMaterialization()
    {
        (Stack? stack, MockLazyProtocol _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Before materialization: root(1) + packet(1) + packet.id(1) + packet.timestamp(1)
            //   + packet.frame_source_id(1) + packet.info(1) + lazy container(1) = 7
            int count = packet.FieldCount();
            await Assert.That(count).IsEqualTo(7);
        }
    }

    [Test]
    public async Task LazyField_FieldCountAfterMaterialization()
    {
        (Stack? stack, MockLazyProtocol _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // After: root(1) + 5 packet fields + container(1) + 3 fields = 10
            int count = packet.FieldCount(materialize: true);
            await Assert.That(count).IsEqualTo(10);
        }
    }

    [Test]
    public async Task LazyField_IsLazyFlag()
    {
        (Stack? stack, MockLazyProtocol _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            Field root = packet.RootField();
            // Root is not lazy
            bool rootNeedsMaterialization = root.NeedsLazyMaterialization;

            // Container is lazy - extract values before await
            // Navigate past the packet container (eager) to reach the mock's lazy container
            bool found = false;
            bool containerNeedsMaterialization = false;
            int childIndex = 0;
            foreach (Field child in root.Children(materialize: false))
            {
                childIndex++;
                // Second child is the mock protocol's lazy container
                if (childIndex == 2)
                {
                    found = true;
                    containerNeedsMaterialization = child.NeedsLazyMaterialization;
                    break;
                }
            }

            await Assert.That(rootNeedsMaterialization).IsFalse();
            await Assert.That(found).IsTrue();
            await Assert.That(containerNeedsMaterialization).IsTrue();
        }
    }

    [Test]
    public async Task LazyField_AccessingChildrenTriggersMaterialization()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Access children through the mock's lazy container — triggers materialization
            // Navigate past the packet container (first child) to reach the mock container (second child)
            Field root = packet.RootField();
            root.TryGetFirstChild(out Field packetContainer);
            packetContainer.TryGetNext(out Field container);

            // Accessing HasChildren triggers materialization
            bool hasChildren = container.HasChildren;
            bool materialized = !container.NeedsLazyMaterialization;
            ushort childCount = container.ChildCount;

            await Assert.That(hasChildren).IsTrue();
            await Assert.That(materialized).IsTrue();
            await Assert.That(childCount).IsEqualTo((ushort)3);
        }
    }

    [Test]
    public async Task LazyField_DfsIteratorMaterializes()
    {
        (Stack? stack, _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // DFS iterator materializes lazy fields during traversal
            int fieldCount = 0;
            foreach (Field _ in packet.IterFieldsDfs())
            {
                fieldCount++;
            }

            // root(1) + 5 packet fields + container(1) + 3 children = 10
            await Assert.That(fieldCount).IsEqualTo(10);
        }
    }

    [Test]
    public async Task LazyField_HasUnpopulatedLazyFields()
    {
        (Stack? stack, MockLazyProtocol _, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            bool hasBefore = packet.HasUnpopulatedLazyFields;
            packet.MaterializeAll();
            bool hasAfter = packet.HasUnpopulatedLazyFields;

            await Assert.That(hasBefore).IsTrue();
            await Assert.That(hasAfter).IsFalse();
        }
    }

    [Test]
    public async Task LazyField_MaterializeIsIdempotent()
    {
        (Stack? stack, MockLazyProtocol? proto, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            int countFirst = packet.FieldCount(materialize: true);

            // Second call should be a no-op (MaterializeAll is idempotent)
            int countSecond = packet.FieldCount(materialize: true);

            await Assert.That(countFirst).IsEqualTo(countSecond);
            await Assert.That(proto.PopulateCallCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task LazyField_FieldValuesCorrectAfterMaterialization()
    {
        (Stack? stack, MockLazyProtocol? proto, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            // Put specific values in the data
            data[0] = 0xAA;
            data[1] = 0xBB;
            data[2] = 0xCC;
            data[3] = 0xDD;
            data[4] = 0xEE;
            data[5] = 0xFF;
            data[6] = 0x00;
            data[7] = 0x11;
            data[8] = 0x22;
            data[9] = 0x33;
            data[10] = 0x44;
            data[11] = 0x55;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 0x0800);

            Packet packet = _ParseFrame(stack, data, protoId);

            // Access via DFS to trigger materialization
            string? dstMacStr = null;
            ulong? typeVal = null;

            foreach (Field field in packet.IterFieldsDfs())
            {
                if (field.FieldId == proto.DstFieldId)
                {
                    field.Value.Data.TryGetAsMacAddress(out MacAddress macVal);
                    dstMacStr = macVal.Format();
                }
                else if (field.FieldId == proto.TypeFieldId)
                {
                    field.Value.Data.TryGetAsU64(out ulong u64Val);
                    typeVal = u64Val;
                }
            }

            await Assert.That(dstMacStr).IsEqualTo("AA:BB:CC:DD:EE:FF");
            await Assert.That(typeVal).IsEqualTo(0x0800UL);
        }
    }

    [Test]
    public async Task LazyField_ChildrenWithoutMaterializationDoesNotPopulate()
    {
        (Stack? stack, MockLazyProtocol? proto, ProtocolId protoId) = _BuildLazyStack();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Iterate without materialization
            int childCount = 0;
            Field root = packet.RootField();
            foreach (Field _ in root.Children(materialize: false))
            {
                childCount++;
            }

            // Container is a child of root (eagerly created), but its children are lazy
            // Root now has 2 eager children: packet container + mock lazy container
            await Assert.That(childCount).IsEqualTo(2);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsTrue();
            await Assert.That(proto.PopulateCallCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task LazyField_NestedLazy_MaterializeAllResolvesAll()
    {
        // Arrange: protocol that creates a nested lazy container inside a lazy container.
        // MaterializeAll must re-scan to pick up the inner lazy field.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MockNestedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        Stack stack = builder.Build();
        using (stack)
        {
            byte[] data = new byte[14];
            Packet packet = _ParseFrame(stack, data, protoId);

            // Before materialization: root(1) + 5 packet fields + outer lazy(1) = 7
            await Assert.That(packet.FieldCount()).IsEqualTo(7);

            // MaterializeAll must finish without hanging, even with nested lazy.
            // Run with a cancellation timeout to detect infinite spin.
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            try
            {
                await Task.Run(() => packet.MaterializeAll(), cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("MaterializeAll() timed out — likely infinite spin on nested lazy fields.");
            }

            // After: root(1) + 5 packet fields + outer(1) + outer-child-A(1) + inner(1) + inner-child(1) = 10
            await Assert.That(packet.FieldCount()).IsEqualTo(10);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
            await Assert.That(proto.OuterCallCount).IsEqualTo(1);
            await Assert.That(proto.InnerCallCount).IsEqualTo(1);
        }
    }

    // === Mock Lazy Protocol ===

    /// <summary>
    /// Mock protocol using closure-based lazy field population for testing lazy behavior.
    /// Tracks how many times the closure is called.
    /// </summary>
    private sealed class MockLazyProtocol : IProtocol
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

            // Parse all fields eagerly — captured by closure
            ReadOnlySpan<byte> span = data.Span;
            MacAddress dst = MacAddress.FromBytes(span[..6]);
            MacAddress src = MacAddress.FromBytes(span[6..12]);
            ushort ethertype = BinaryPrimitives.ReadUInt16BigEndian(span[12..14]);

            // Capture field IDs for the closure
            FieldId dstFieldId = _DstFieldId;
            FieldId srcFieldId = _SrcFieldId;
            FieldId typeFieldId = _TypeFieldId;

            parentField.AppendLazy(_ContainerFieldId, FieldValue.None, (in container) =>
            {
                Interlocked.Increment(ref _PopulateCallCount);
                container.Append(dstFieldId, FieldValue.NewMacAddress(dst));
                container.Append(srcFieldId, FieldValue.NewMacAddress(src));
                container.Append(typeFieldId, FieldValue.NewU64(ethertype));
                return 0;
            });
            return 14;
        }
    }

    /// <summary>
    /// Mock protocol that creates nested lazy fields: an outer lazy container whose
    /// populator creates an inner lazy container. Validates that MaterializeAll()
    /// correctly handles lazy-within-lazy by re-scanning after each pass.
    /// </summary>
    private sealed class MockNestedLazyProtocol : IProtocol
    {
        private FieldId _OuterContainerId;
        private FieldId _OuterChildId;
        private FieldId _InnerContainerId;
        private FieldId _InnerChildId;
        private int _OuterCallCount;
        private int _InnerCallCount;

        public string Name => "nested";
        public string UiName => "Nested";

        public int OuterCallCount => _OuterCallCount;
        public int InnerCallCount => _InnerCallCount;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _OuterContainerId = builder.RegisterField(protocolId, "nested.outer", "Outer", FieldType.None);
            _OuterChildId = builder.RegisterField(protocolId, "nested.outer.a", "Child A", FieldType.U64);
            _InnerContainerId = builder.RegisterField(protocolId, "nested.inner", "Inner", FieldType.None);
            _InnerChildId = builder.RegisterField(protocolId, "nested.inner.x", "Child X", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            // Capture field IDs for closures
            FieldId outerChildId = _OuterChildId;
            FieldId innerContainerId = _InnerContainerId;
            FieldId innerChildId = _InnerChildId;

            parentField.AppendLazy(_OuterContainerId, FieldValue.None, (in container) =>
            {
                Interlocked.Increment(ref _OuterCallCount);

                // Append an eager child
                container.Append(outerChildId, FieldValue.NewU64(42));

                // Append a nested lazy container — this is the crux of the test:
                // the inner lazy field's index will be beyond the initial scan range.
                container.AppendLazy(innerContainerId, FieldValue.None, (in innerContainer) =>
                {
                    Interlocked.Increment(ref _InnerCallCount);
                    innerContainer.Append(innerChildId, FieldValue.NewU64(99));
                    return 0;
                });

                return 0;
            });
            return 14;
        }
    }
}


