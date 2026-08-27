// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the post-parser pipeline: build-time sort order, runtime execution lifecycle,
/// error and exception handling, and indexed-parse integration.
/// </summary>
internal sealed class PostParserTests
{
    // ── Static test data ──────────────────────────────────────────────────────────────

    // Shared arrays used in data-source methods.
    // CA1861: prefer static readonly fields over repeated inline array expressions.
    private static readonly int[] _SortedOrder0123 = [0, 1, 2, 3];

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a valid synthetic Ethernet/IPv4/UDP frame for tests that need real bytes.
    /// The frame has a fixed size and predictable layout so tests can assert specific field values.
    /// </summary>
    private static byte[] _BuildEthernetFrame()
    {
        byte[] frame = new byte[42]; // 14 eth + 20 ip + 8 udp
        // Ethernet header: ethertype = IPv4 (0x0800)
        frame[12] = 0x08;
        frame[13] = 0x00;
        // IPv4 header: version=4, IHL=5, proto=UDP(17), total_len=28
        frame[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), 28);
        frame[23] = 17; // UDP
        frame[24] = 0; // no frag
        // UDP header
        int udp = 34;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp), 1234);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 2), 5678);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(udp + 4), 8); // udp len = 8 (header only)
        return frame;
    }

    /// <summary>Wraps raw bytes into a <see cref="Frame"/> using the given stack's registry.</summary>
    private static Frame _MakeFrame(Stack stack, byte[]? data = null, int frameId = 1) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            data ?? _BuildEthernetFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    // ── Sort order and root-parent execution ──────────────────────────────────────────

    /// <summary>Test cases for the sort-order test: (priorities, expected execution order by registration index).</summary>
    public static IEnumerable<Func<(int[], int[])>> PostParserSortOrderCases()
    {
        yield return static () => ([10, 5, 20, 0], [3, 1, 0, 2]);  // mixed priorities: sorted 0,5,10,20 → indices 3,1,0,2
        yield return static () => ([0, 0, 0, 0], _SortedOrder0123);  // all same → registration order
        yield return static () => ([-1, 1, 0, -2], [3, 0, 2, 1]);   // mixed with negatives: sorted -2,-1,0,1 → indices 3,0,2,1
    }

    [Test]
    [MethodDataSource(nameof(PostParserSortOrderCases))]
    public async Task PostParsers_SortedByPriorityAscendingThenRegistrationOrder(
        int[] priorities, int[] expectedOrderByRegistrationIndex)
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        List<int> executionOrder = [];

        // Register stub frame protocol
        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        // Register post-parsers in order
        PostParserProto[] protos = new PostParserProto[priorities.Length];
        for (int i = 0; i < priorities.Length; i++)
        {
            int capturedIndex = i;
            protos[i] = new PostParserProto($"pp{i}", $"PP{i}", () => executionOrder.Add(capturedIndex));
            ProtocolId ppId = builder.RegisterProtocol(protos[i]);
            builder.RegisterPostParser(ppId, priorities[i]);
        }

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: execution order matches expected order (by original registration index)
        await Assert.That(executionOrder.Count).IsEqualTo(priorities.Length);
        for (int i = 0; i < expectedOrderByRegistrationIndex.Length; i++)
        {
            await Assert.That(executionOrder[i]).IsEqualTo(expectedOrderByRegistrationIndex[i]);
        }

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_StackPostParsers_ReflectsSortedOrder()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        PostParserProto pp0 = new("pp0", "PP0", () => { });
        PostParserProto pp1 = new("pp1", "PP1", () => { });
        PostParserProto pp2 = new("pp2", "PP2", () => { });
        ProtocolId id0 = builder.RegisterProtocol(pp0);
        ProtocolId id1 = builder.RegisterProtocol(pp1);
        ProtocolId id2 = builder.RegisterProtocol(pp2);

        // Register in reverse priority order: 10, 5, 0 → sorted order should be pp2, pp1, pp0
        builder.RegisterPostParser(id0, priority: 10);
        builder.RegisterPostParser(id1, priority: 5);
        builder.RegisterPostParser(id2, priority: 0);

        // Act
        Stack stack = builder.Build();

        // Assert: PostParsers is sorted by priority ascending; ProtocolId matches registration
        ReadOnlyMemory<PostParserInfo> sorted = stack.PostParsers;
        await Assert.That(sorted.Length).IsEqualTo(3);
        await Assert.That(sorted.Span[0].Priority).IsEqualTo(0);
        await Assert.That(sorted.Span[0].ProtocolId).IsEqualTo(id2);
        await Assert.That(sorted.Span[1].Priority).IsEqualTo(5);
        await Assert.That(sorted.Span[1].ProtocolId).IsEqualTo(id1);
        await Assert.That(sorted.Span[2].Priority).IsEqualTo(10);
        await Assert.That(sorted.Span[2].ProtocolId).IsEqualTo(id0);

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_SamePriority_MaintainRegistrationOrder()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        List<string> executionOrder = [];

        PostParserProto pp1 = new("pp1", "PP1", () => executionOrder.Add("pp1"));
        PostParserProto pp2 = new("pp2", "PP2", () => executionOrder.Add("pp2"));
        PostParserProto pp3 = new("pp3", "PP3", () => executionOrder.Add("pp3"));

        ProtocolId id1 = builder.RegisterProtocol(pp1);
        ProtocolId id2 = builder.RegisterProtocol(pp2);
        ProtocolId id3 = builder.RegisterProtocol(pp3);

        // All same priority → should run in registration order
        builder.RegisterPostParser(id1, priority: 5);
        builder.RegisterPostParser(id2, priority: 5);
        builder.RegisterPostParser(id3, priority: 5);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: registration order preserved when priority ties
        await Assert.That(executionOrder.Count).IsEqualTo(3);
        await Assert.That(executionOrder[0]).IsEqualTo("pp1");
        await Assert.That(executionOrder[1]).IsEqualTo("pp2");
        await Assert.That(executionOrder[2]).IsEqualTo("pp3");

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_RegisteredDescription_AppearsInPostParserInfo()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        PostParserProto ppWithDesc = new("pp.with.desc", "WithDesc", () => { });
        PostParserProto ppNoDesc = new("pp.no.desc", "NoDesc", () => { });
        ProtocolId idWith = builder.RegisterProtocol(ppWithDesc);
        ProtocolId idNo = builder.RegisterProtocol(ppNoDesc);

        builder.RegisterPostParser(idWith, priority: 0, description: "my description");
        builder.RegisterPostParser(idNo, priority: 1, description: null);

        // Act
        Stack stack = builder.Build();

        // Assert: descriptions round-trip through PostParserInfo
        ReadOnlyMemory<PostParserInfo> infos = stack.PostParsers;
        await Assert.That(infos.Span[0].Description).IsEqualTo("my description");
        await Assert.That(infos.Span[1].Description).IsNull();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_PostParserCount_ReflectsRegistrationCount()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        // Assert: count is 0 before any post-parser is registered
        await Assert.That(builder.PostParserCount).IsEqualTo(0);

        PostParserProto pp1 = new("pp.count1", "Count1", () => { });
        PostParserProto pp2 = new("pp.count2", "Count2", () => { });
        ProtocolId id1 = builder.RegisterProtocol(pp1);
        ProtocolId id2 = builder.RegisterProtocol(pp2);

        builder.RegisterPostParser(id1, priority: 0);
        await Assert.That(builder.PostParserCount).IsEqualTo(1);

        builder.RegisterPostParser(id2, priority: 1);
        await Assert.That(builder.PostParserCount).IsEqualTo(2);

        // Act
        Stack stack = builder.Build();

        // Assert: built stack reflects the same count
        await Assert.That(stack.PostParserCount).IsEqualTo(2);

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_AppendFieldsAsRootSiblings()
    {
        // Arrange: post-parser that appends a field under parentField (the root)
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        FieldId customFieldId = default;
        FieldAppendingProto ppProto = new("custom.proto", "CustomProto", default);

        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        customFieldId = builder.RegisterField(ppId, "custom.field", "Custom Field", FieldType.U64);
        ppProto.SetFieldId(customFieldId);

        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: custom field appears in the packet tree
        bool found = packet.TryGetFieldValue(customFieldId, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(found).IsTrue();
        bool asU64 = value.Data.TryGetAsU64(out ulong u64Value);
        await Assert.That(asU64).IsTrue();
        await Assert.That(u64Value).IsEqualTo(42UL);

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_InfoSetByPostParser_AppearsInPacketInfo()
    {
        // Arrange: post-parser that sets packet.info; this info should be captured by
        // the subsequent packet.info append (info is set before packet.info is appended)
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        InfoSettingProto ppProto = new("info.setter", "InfoSetter", "PostParserInfo");
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: info set by post-parser is captured in packet.info
        await Assert.That(packet.Info).IsEqualTo("PostParserInfo");

        stack.Dispose();
    }

    // ── Overload and error-path matrix ─────────────────────────────────────────────────

    [Test]
    public async Task PostParsers_RunEvenWhen_MainParseReturnsError()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        ErrorProto frameProto = new("frame.custom", "Frame", "main parse error");
        ProtocolId frameId = builder.RegisterProtocol(frameProto);

        bool postParserRan = false;
        PostParserProto ppProto = new("pp", "PP", () => postParserRan = true);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act: ParseFrame with firstProtocolId that returns an error
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, frameId);

        // Assert: post-parser ran despite main parse error
        await Assert.That(postParserRan).IsTrue();

        // Assert: main parse error was recorded
        bool hasError = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(hasError).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_RunEvenWhen_MainParseThrows()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        ThrowingProto frameProto = new("frame.custom", "Frame", new InvalidOperationException("main exception"));
        ProtocolId frameId = builder.RegisterProtocol(frameProto);

        bool postParserRan = false;
        PostParserProto ppProto = new("pp", "PP", () => postParserRan = true);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, frameId);

        // Assert: post-parser ran despite main parse exception
        await Assert.That(postParserRan).IsTrue();

        // Assert: main exception was recorded as packet error
        bool hasError = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(hasError).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_FailingPostParser_DoesNotSuppressLaterPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        List<string> ran = [];

        ErrorProto pp1 = new("pp1", "PP1", "post-parser 1 error");
        PostParserProto pp2 = new("pp2", "PP2", () => ran.Add("pp2"));
        PostParserProto pp3 = new("pp3", "PP3", () => ran.Add("pp3"));

        ProtocolId id1 = builder.RegisterProtocol(pp1);
        ProtocolId id2 = builder.RegisterProtocol(pp2);
        ProtocolId id3 = builder.RegisterProtocol(pp3);

        builder.RegisterPostParser(id1, priority: 0);
        builder.RegisterPostParser(id2, priority: 1);
        builder.RegisterPostParser(id3, priority: 2);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: pp2 and pp3 ran despite pp1 returning an error
        await Assert.That(ran.Count).IsEqualTo(2);
        await Assert.That(ran[0]).IsEqualTo("pp2");
        await Assert.That(ran[1]).IsEqualTo("pp3");

        // Assert: pp1's error was recorded
        bool hasError = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(hasError).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_ThrowingPostParser_DoesNotSuppressLaterPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        List<string> ran = [];

        ThrowingProto pp1 = new("pp1", "PP1", new InvalidOperationException("pp1 threw"));
        PostParserProto pp2 = new("pp2", "PP2", () => ran.Add("pp2"));
        PostParserProto pp3 = new("pp3", "PP3", () => ran.Add("pp3"));

        ProtocolId id1 = builder.RegisterProtocol(pp1);
        ProtocolId id2 = builder.RegisterProtocol(pp2);
        ProtocolId id3 = builder.RegisterProtocol(pp3);

        builder.RegisterPostParser(id1, priority: 0);
        builder.RegisterPostParser(id2, priority: 1);
        builder.RegisterPostParser(id3, priority: 2);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: pp2 and pp3 ran despite pp1 throwing
        await Assert.That(ran.Count).IsEqualTo(2);
        await Assert.That(ran[0]).IsEqualTo("pp2");
        await Assert.That(ran[1]).IsEqualTo("pp3");

        // Assert: pp1's exception was recorded as packet error
        bool hasError = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(hasError).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_NoPostParsers_PacketParsesNormally()
    {
        // Arrange: stack with no post-parsers
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: parse completed normally, no errors
        await Assert.That(packet.IsFinalized).IsTrue();
        bool hasError = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true); // materialize: true — need complete field tree for assertion
        await Assert.That(hasError).IsFalse();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_RecycleOverload_RunsPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        int runCount = 0;
        PostParserProto ppProto = new("pp", "PP", () => runCount++);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act: first parse, then recycle
        Frame frame1 = _MakeFrame(stack, new byte[42], frameId: 1);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame1);
        int countAfterFirst = runCount;

        Frame frame2 = _MakeFrame(stack, new byte[42], frameId: 2);
        Packet.ParseFrame(packet, new PacketId(1), stack, frame2);
        int countAfterSecond = runCount;

        // Assert: post-parser ran once per parse
        await Assert.That(countAfterFirst).IsEqualTo(1);
        await Assert.That(countAfterSecond).IsEqualTo(2);

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_TryParseFrame_RunsPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        bool ran = false;
        PostParserProto ppProto = new("pp", "PP", () => ran = true);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act
        Frame frame1 = _MakeFrame(stack, new byte[42], frameId: 1);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame1);
        ran = false; // reset after first parse

        Frame frame2 = _MakeFrame(stack, new byte[42], frameId: 2);
        RecycleError? error = Packet.TryParseFrame(packet, new PacketId(1), stack, frame2);

        // Assert
        await Assert.That(error).IsNull();
        await Assert.That(ran).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_ParseFrameWithFirstProtocolOverride_RunsPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        ProtocolId stubId = builder.RegisterProtocol(stub);

        bool ran = false;
        PostParserProto ppProto = new("pp", "PP", () => ran = true);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act: use overload that specifies first protocol explicitly
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, stubId);

        // Assert
        await Assert.That(ran).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_ParseFrameIndexed_RunsPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        bool ran = false;
        PostParserProto ppProto = new("pp", "PP", () => ran = true);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();
        PacketIndex index = new(stack);

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

        // Assert
        await Assert.That(ran).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_RecycleIndexedOverload_RunsPostParsers()
    {
        // Arrange
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        int runCount = 0;
        PostParserProto ppProto = new("pp", "PP", () => runCount++);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();
        PacketIndex index = new(stack);

        // Act
        Frame frame1 = _MakeFrame(stack, new byte[42], frameId: 1);
        Packet packet = Packet.ParseFrameIndexed(new PacketId(0), stack, frame1, index);
        int afterFirst = runCount;

        Frame frame2 = _MakeFrame(stack, new byte[42], frameId: 2);
        RecycleError? error = Packet.TryParseFrameIndexed(packet, new PacketId(1), stack, frame2, index);
        int afterSecond = runCount;

        // Assert
        await Assert.That(error).IsNull();
        await Assert.That(afterFirst).IsEqualTo(1);
        await Assert.That(afterSecond).IsEqualTo(2);

        stack.Dispose();
    }

    // ── Index integration ──────────────────────────────────────────────────────────────

    [Test]
    public async Task PostParsers_RecordProtocolPresence_AppearsInProtocolBitmap()
    {
        // Arrange: post-parser that records its own protocol presence via ParseContext
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        IndexRecordingProto ppProto = new("pp.index", "PPIndex", null, null);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();
        ppProto.SetProtocolId(ppId); // tell the proto its own ID for RecordProtocolPresence

        PacketIndex index = new(stack);

        // Act: two indexed parses
        Frame frame1 = _MakeFrame(stack, new byte[42], frameId: 1);
        Packet.ParseFrameIndexed(new PacketId(0), stack, frame1, index);

        Frame frame2 = _MakeFrame(stack, new byte[42], frameId: 2);
        Packet.ParseFrameIndexed(new PacketId(1), stack, frame2, index);

        // Assert: post-parser's protocol appears in the index bitmaps for both packets
        ReadOnlyRoaringBitmap bm = index.GetProtocolBitmap(ppId);
        await Assert.That(bm.Cardinality).IsEqualTo(2L);
        await Assert.That(bm.Contains(0)).IsTrue();
        await Assert.That(bm.Contains(1)).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_RecordGroupPresence_AppearsInGroupBitmap()
    {
        // Arrange: post-parser that records an index group presence
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        IndexGroupId groupId = default;
        IndexRecordingProto ppProto = new("pp.group", "PPGroup", null, null);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);

        // Register a field in a group and give the post-parser that group ID
        FieldId groupField = builder.RegisterFieldInGroup(ppId, "pp.group.field", "Group Field", FieldType.U64, "pp.group");
        groupId = builder.GetFieldIndexGroup(groupField);
        ppProto.SetGroupId(groupId);

        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        PacketIndex index = new(stack);

        // Act
        Frame frame = _MakeFrame(stack, new byte[42], frameId: 5);
        Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

        // Assert
        ReadOnlyRoaringBitmap bm = index.GetGroupBitmap(groupId);
        await Assert.That(bm.Contains(0)).IsTrue();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_NonIndexedParse_NoIndexContributions()
    {
        // Arrange: post-parser that records protocol presence — should not contribute in non-indexed parse
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        IndexRecordingProto ppProto = new("pp.noindex", "PPNoIndex", null, null);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);
        ppProto.SetProtocolId(ppId);

        Stack stack = builder.Build();

        PacketIndex index = new(stack);

        // Act: non-indexed parse followed by indexed parse to verify separation
        Frame nonIndexed = _MakeFrame(stack, new byte[42], frameId: 99);
        Packet.ParseFrame(new PacketId(0), stack, nonIndexed);

        Frame indexed = _MakeFrame(stack, new byte[42], frameId: 100);
        Packet.ParseFrameIndexed(new PacketId(1), stack, indexed, index);

        // Assert: only the indexed parse (packetId=1) appears in the protocol bitmap
        ReadOnlyRoaringBitmap bm = index.GetProtocolBitmap(ppId);
        await Assert.That(bm.Contains(1)).IsTrue();
        await Assert.That(bm.Contains(0)).IsFalse();

        stack.Dispose();
    }

    [Test]
    public async Task PostParsers_PacketInfoField_AppearsAfterPostParsers()
    {
        // Arrange: ensure packet.info is present only after all post-parsers run.
        // The post-parser sets info; if packet.info was appended BEFORE the post-parser,
        // it would capture the empty info and not the value set by the post-parser.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubFrameProto stub = new();
        builder.RegisterProtocol(stub);

        const string expectedInfo = "set by post-parser";
        InfoSettingProto ppProto = new("info.pp", "InfoPP", expectedInfo);
        ProtocolId ppId = builder.RegisterProtocol(ppProto);
        builder.RegisterPostParser(ppId, priority: 0);

        Stack stack = builder.Build();

        // Act
        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        // Assert: packet.info was captured after post-parser set it
        await Assert.That(packet.Info).IsEqualTo(expectedInfo);

        stack.Dispose();
    }

    // ── Test doubles ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal protocol that does nothing — used as the frame dispatch target
    /// to avoid pulling in real frame/Ethernet parsing side-effects.
    /// </summary>
    private sealed class StubFrameProto : IProtocol
    {
        public string Name => "frame.stub";
        public string UiName => "Frame Stub";
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    /// <summary>
    /// A post-parser protocol that invokes a callback on each parse, enabling order tracking.
    /// </summary>
    private sealed class PostParserProto(string name, string uiName, Action? onParse) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            onParse?.Invoke();
            return 0;
        }
    }

    /// <summary>
    /// A protocol that always returns a <see cref="ParseError"/> with the given message.
    /// Used to simulate main-parse errors and post-parser errors.
    /// </summary>
    private sealed class ErrorProto(string name, string uiName, string errorMessage) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => ParseError.Custom(name, errorMessage);
    }

    /// <summary>
    /// A protocol that throws an exception on parse.
    /// Used to simulate parser exceptions in both main-parse and post-parser paths.
    /// </summary>
    private sealed class ThrowingProto(string name, string uiName, Exception ex) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => throw ex;
    }

    /// <summary>
    /// A post-parser protocol that appends a single U64 field with value 42 under the parent.
    /// Used to verify that post-parser fields appear in the field tree.
    /// </summary>
    private sealed class FieldAppendingProto(string name, string uiName, FieldId fieldIdFromClosure) : IProtocol
    {
        private FieldId _FieldId = fieldIdFromClosure;

        public string Name => name;
        public string UiName => uiName;

        public void SetFieldId(FieldId id) => _FieldId = id;

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            parentField.Append(_FieldId, FieldValue.NewU64(42));
            return 0;
        }
    }

    /// <summary>
    /// A post-parser protocol that sets <see cref="Packet.Info"/> on the packet.
    /// Used to verify that post-parsers can set packet info before packet.info is appended.
    /// </summary>
    private sealed class InfoSettingProto(string name, string uiName, string info) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            parentField.Packet.SetInfo(new LazyString(info));
            return 0;
        }
    }

    /// <summary>
    /// A post-parser protocol that records protocol and/or group presence in the index.
    /// Used to verify that post-parsers contribute to the <see cref="PacketIndex"/> correctly.
    /// </summary>
    private sealed class IndexRecordingProto(string name, string uiName, ProtocolId? protocolId, IndexGroupId? groupId) : IProtocol
    {
        private ProtocolId _ProtocolId = protocolId ?? ProtocolId.Invalid;
        private IndexGroupId _GroupId = groupId ?? IndexGroupId.Invalid;

        public string Name => name;
        public string UiName => uiName;

        public void SetProtocolId(ProtocolId id) => _ProtocolId = id;
        public void SetGroupId(IndexGroupId id) => _GroupId = id;

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            if (_ProtocolId.IsValid)
            {
                context.RecordProtocolPresence(_ProtocolId);
            }
            if (_GroupId.IsValid)
            {
                context.RecordGroupPresence(_GroupId);
            }
            return 0;
        }
    }

}

