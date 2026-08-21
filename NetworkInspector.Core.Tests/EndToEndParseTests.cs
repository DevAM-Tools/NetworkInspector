// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// End-to-end parse pipeline tests using a mock Ethernet protocol.
/// Verifies frame construction, packet parsing, field tree navigation, and error handling.
/// </summary>
internal sealed class EndToEndParseTests
{
    /// <summary>
    /// Builds a Stack with a mock Ethernet protocol set as the packet protocol.
    /// </summary>
    private static (Stack Stack, MockEthernetProtocol Eth, ProtocolId EthId) _BuildEthernetStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        MockEthernetProtocol eth = new();
        ProtocolId ethId = builder.RegisterProtocol(eth);
        eth.RegisterFields(builder, ethId);
        Stack stack = builder.Build();
        return (stack, eth, ethId);
    }

    /// <summary>
    /// Creates a minimal 14-byte Ethernet frame.
    /// </summary>
    private static byte[] _BuildEthernetFrame(
        byte[] dstMac, byte[] srcMac, ushort ethertype)
    {
        byte[] frame = new byte[14];
        Array.Copy(dstMac, 0, frame, 0, 6);
        Array.Copy(srcMac, 0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), ethertype);
        return frame;
    }

    private static Packet _ParseTestFrame(Stack stack, byte[] frameData, ProtocolId firstProtocolId)
    {
        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            frameData,
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
    public async Task Parse_ValidEthernetFrame_CreatesFields()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
                [0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            bool isFinalized = packet.IsFinalized;
            int fieldCountBeforeMaterialize = packet.FieldCount(materialize: false); // materialize: false — current materialized count only

            // Before materialization: root(1) + packet(1) + packet.id(1) + packet.timestamp(1)
            //   + packet.frame_source_id(1) + packet.info(1) + lazy eth container(1) = 7
            await Assert.That(isFinalized).IsTrue();
            await Assert.That(fieldCountBeforeMaterialize).IsEqualTo(7);

            // After materialization: 7 + 3 eth fields = 10
            int fieldCountAfterMaterialize = packet.FieldCount(materialize: true); // materialize: true — count after full materialization
            await Assert.That(fieldCountAfterMaterialize).IsGreaterThanOrEqualTo(10);
        }
    }

    [Test]
    public async Task Parse_ValidEthernetFrame_FieldValuesCorrect()
    {
        (Stack? stack, MockEthernetProtocol? eth, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF],
                [0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            // Extract values before any await (Field is a ref struct)
            string? dstMacStr = null;
            string? srcMacStr = null;
            ulong? ethertypeVal = null;

            // Fields are under the eth container (root → packet → ... | root → eth container → fields)
            Field rootField = packet.RootField();
            rootField.TryGetFirstChild(out Field packetContainer, materialize: true); // materialize: true — navigate/populate children including lazy
            packetContainer.TryGetNext(out Field ethContainer);
            foreach (Field child in ethContainer.Children(materialize: true)) // materialize: true — navigate/populate children including lazy
            {
                if (child.FieldId == eth.DstFieldId)
                {
                    child.Value.Data.TryGetAsMacAddress(out MacAddress macVal);
                    dstMacStr = macVal.Format();
                }
                else if (child.FieldId == eth.SrcFieldId)
                {
                    child.Value.Data.TryGetAsMacAddress(out MacAddress macVal2);
                    srcMacStr = macVal2.Format();
                }
                else if (child.FieldId == eth.TypeFieldId)
                {
                    child.Value.Data.TryGetAsU64(out ulong u64Val);
                    ethertypeVal = u64Val;
                }
            }

            await Assert.That(dstMacStr).IsEqualTo("AA:BB:CC:DD:EE:FF");
            await Assert.That(srcMacStr).IsEqualTo("00:11:22:33:44:55");
            await Assert.That(ethertypeVal).IsEqualTo(0x0800UL);
        }
    }

    [Test]
    public async Task Parse_ShortData_ProducesError()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] shortData = new byte[10];
            Packet packet = _ParseTestFrame(stack, shortData, ethId);

            bool isFinalized = packet.IsFinalized;
            bool rootFieldValid = packet.RootField().FieldId.IsValid;

            await Assert.That(isFinalized).IsTrue();
            await Assert.That(rootFieldValid).IsTrue();
        }
    }

    [Test]
    public async Task Parse_EmptyData_ProducesError()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(0),
                ReadOnlyMemory<byte>.Empty,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet packet = Packet.ParseFrame(
                new PacketId(1),
                stack,
                frame,
                ethId);

            bool isFinalized = packet.IsFinalized;
            await Assert.That(isFinalized).IsTrue();
        }
    }

    [Test]
    public async Task Parse_FieldTreeNavigation_RootHasChildren()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x86DD);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            Field root = packet.RootField();
            bool isRoot = root.IsRoot;
            bool hasChildren = root.HasChildren(materialize: true); // materialize: true — navigate/populate children including lazy
            ushort childCount = root.ChildCount(materialize: true); // materialize: true — navigate/populate children including lazy

            await Assert.That(isRoot).IsTrue();
            await Assert.That(hasChildren).IsTrue();
            // Root has 2 children: packet container (from PacketProtocol) + eth container (from mock)
            await Assert.That(childCount).IsEqualTo((ushort)2);
        }
    }

    [Test]
    public async Task Parse_FieldTreeNavigation_SiblingTraversal()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            Field root = packet.RootField();
            // Root has 2 children: packet container + eth container
            bool hasPacketContainer = root.TryGetFirstChild(out Field packetContainer, materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasEthContainer = packetContainer.TryGetNext(out Field ethContainer);
            bool hasThirdRootChild = ethContainer.TryGetNext(out _);

            // Eth container has 3 children (lazy, materialized on access)
            bool hasFirst = ethContainer.TryGetFirstChild(out Field first, materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasSecond = first.TryGetNext(out Field second);
            bool hasThird = second.TryGetNext(out Field third);
            bool hasFourth = third.TryGetNext(out _);

            await Assert.That(hasPacketContainer).IsTrue();
            await Assert.That(hasEthContainer).IsTrue();
            await Assert.That(hasThirdRootChild).IsFalse();
            await Assert.That(hasFirst).IsTrue();
            await Assert.That(hasSecond).IsTrue();
            await Assert.That(hasThird).IsTrue();
            await Assert.That(hasFourth).IsFalse();
        }
    }

    [Test]
    public async Task Parse_FieldTreeNavigation_ParentNavigation()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            Field root = packet.RootField();
            ushort rootIndex = root.StorageIndex;
            // Navigate to eth container (second child of root) and its first child
            root.TryGetFirstChild(out Field packetContainer, materialize: true); // materialize: true — navigate/populate children including lazy
            packetContainer.TryGetNext(out Field container);
            container.TryGetFirstChild(out Field child, materialize: true); // materialize: true — navigate/populate children including lazy
            bool hasParent = child.TryGetParent(out Field parent);
            ushort parentIndex = parent.StorageIndex;
            ushort containerIndex = container.StorageIndex;

            // Child's parent is the container, not root
            await Assert.That(hasParent).IsTrue();
            await Assert.That(parentIndex).IsEqualTo(containerIndex);
        }
    }

    [Test]
    public async Task Parse_PacketMetadata()
    {
        // Set up registry with a known source and interface so FrameSourceId can be derived
        FrameInterfaceRegistry registry = new();
        using NullFrameSource nullSource = new();
        FrameSourceId expectedSourceId = registry.RegisterSource(nullSource);
        FrameInterfaceId ifaceId = registry.Register(expectedSourceId, "test0", linkType: LinkType.Ethernet);

        // Build stack using the same registry
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, registry);
        MockEthernetProtocol eth = new();
        ProtocolId ethId = builder.RegisterProtocol(eth);
        eth.RegisterFields(builder, ethId);
        Stack stack = builder.Build();

        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x0800);

            Frame frame = Frame.Create(
                new FrameId(42),
                Timestamp.FromSecs(9999),
                frameData,
                LinkType.Ethernet,
                ifaceId,
                registry).Value;

            Packet packet = Packet.ParseFrame(
                new PacketId(100),
                stack,
                frame,
                ethId);

            PacketId packetId = packet.Id;
            FrameSourceId sourceId = packet.FrameSourceId;
            FrameId frameId = packet.Frame.Id;
            int dataLength = packet.Frame.Length;

            await Assert.That(packetId).IsEqualTo(new PacketId(100));
            await Assert.That(sourceId).IsEqualTo(expectedSourceId);
            await Assert.That(frameId).IsEqualTo(new FrameId(42));
            await Assert.That(dataLength).IsEqualTo(14);
        }
    }

    [Test]
    public async Task Parse_FieldCount_MatchesExpected()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
                [0x00, 0x00, 0x00, 0x00, 0x00, 0x01],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            // Before materialization: root(1) + packet(1) + packet.id(1) + packet.timestamp(1)
            //   + packet.frame_source_id(1) + packet.info(1) + lazy eth container(1) = 7
            int fieldCountLazy = packet.FieldCount(materialize: false); // materialize: false — current materialized count only
            await Assert.That(fieldCountLazy).IsEqualTo(7);

            // After materialization: 7 + 3 eth fields = 10
            int fieldCountFull = packet.FieldCount(materialize: true); // materialize: true — count after full materialization
            await Assert.That(fieldCountFull).IsGreaterThanOrEqualTo(10);
        }
    }

    [Test]
    public async Task Frame_Properties()
    {
        FrameInterfaceRegistry registry = new();
        using NullFrameSource nullSource = new();
        FrameSourceId sourceId = registry.RegisterSource(nullSource);
        FrameInterfaceId ifaceId = registry.Register(sourceId, "test0", linkType: LinkType.Ethernet);

        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
        Frame frame = Frame.Create(
            new FrameId(7),
            Timestamp.FromMillis(123),
            data,
            LinkType.Ethernet,
            ifaceId,
            registry).Value;

        FrameId id = frame.Id;
        LinkType linkType = frame.LinkType;
        FrameInterfaceId interfaceId = frame.InterfaceId;
        bool hasInterface = frame.HasInterface;
        int length = frame.Length;
        bool isEmpty = frame.IsEmpty;

        await Assert.That(id).IsEqualTo(new FrameId(7));
        await Assert.That(linkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(interfaceId).IsEqualTo(ifaceId);
        await Assert.That(hasInterface).IsTrue();
        await Assert.That(length).IsEqualTo(14);
        await Assert.That(isEmpty).IsFalse();
    }

    [Test]
    public async Task Frame_EmptyData()
    {
        FrameInterfaceRegistry registry = new();
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            ReadOnlyMemory<byte>.Empty,
            LinkType.Null,
            FrameInterfaceId.Invalid,
            registry).Value;

        bool isEmpty = frame.IsEmpty;
        int length = frame.Length;
        bool hasInterface = frame.HasInterface;

        await Assert.That(isEmpty).IsTrue();
        await Assert.That(length).IsEqualTo(0);
        await Assert.That(hasInterface).IsFalse();
    }

    [Test]
    public async Task Parse_FieldByIndex_ValidAccess()
    {
        (Stack? stack, MockEthernetProtocol _, ProtocolId ethId) = _BuildEthernetStack();
        using (stack)
        {
            byte[] frameData = _BuildEthernetFrame(
                [0x01, 0x02, 0x03, 0x04, 0x05, 0x06],
                [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F],
                0x0800);

            Packet packet = _ParseTestFrame(stack, frameData, ethId);

            bool hasRoot = packet.TryGetFieldAt(0, out Field rootField);
            bool isRoot = rootField.IsRoot;

            await Assert.That(hasRoot).IsTrue();
            await Assert.That(isRoot).IsTrue();

            bool hasInvalid = packet.TryGetFieldAt(9999, out _);
            await Assert.That(hasInvalid).IsFalse();
        }
    }

    // === Stubs ===

    /// <summary>Minimal IFrameSource stub for testing — never produces frames.</summary>
    private sealed class NullFrameSource : IFrameSource
    {
        /// <inheritdoc/>
        public string UiName => "test";
        /// <inheritdoc/>
        public string? Description => null;
        /// <inheritdoc/>
        public int? EstimatedFrameCount => null;
        /// <inheritdoc/>
        public bool IsRunning => false;
        /// <inheritdoc/>
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }
        /// <inheritdoc/>
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }

    // === Mock Ethernet Protocol ===

    /// <summary>
    /// Mock Ethernet protocol that reads 14 bytes: 6 dst + 6 src + 2 ethertype.
    /// Uses closure-based lazy field population — children are materialized on first access.
    /// </summary>
    private sealed class MockEthernetProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;
        private FieldId _DstFieldId;
        private FieldId _SrcFieldId;
        private FieldId _TypeFieldId;

        public string Name => "eth";
        public string UiName => "Ethernet";

        public FieldId ContainerFieldId => _ContainerFieldId;
        public FieldId DstFieldId => _DstFieldId;
        public FieldId SrcFieldId => _SrcFieldId;
        public FieldId TypeFieldId => _TypeFieldId;

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
                container.Append(dstFieldId, FieldValue.NewMacAddress(dst));
                container.Append(srcFieldId, FieldValue.NewMacAddress(src));
                container.Append(typeFieldId, FieldValue.NewU64(ethertype));
                return 0;
            });
            return 14;
        }
    }
}


