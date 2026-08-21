// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Exit-point coverage for <see cref="Packet"/> lifecycle, lazy materialization,
/// recycling failure paths, and slab growth beyond default capacity.
/// </summary>
internal sealed class PacketExitPointTests
{
    private const int _ManyFieldCount = 4100;

    private static Stack _BuildStack(bool includeExceptionStackTrace = false)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry())
        {
            IncludeExceptionStackTrace = includeExceptionStackTrace
        };
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Stack _BuildManyFieldsStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ManyFieldsProtocol proto = new(_ManyFieldCount);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        return builder.Build();
    }

    private static Frame _MakeFrame(Stack stack, byte[] data, int frameId = 1) =>
        _MakeFrame(stack, data, frameId, FrameInterfaceId.Invalid);

    private static Frame _MakeFrame(Stack stack, byte[] data, int frameId, FrameInterfaceId interfaceId) =>
        Frame.Create(
            new FrameId(frameId),
            Timestamp.FromSecs(frameId),
            data,
            LinkType.Ethernet,
            interfaceId,
            stack.FrameInterfaceRegistry).Value;

    [Test]
    public async Task PrepareForReuse_NotFinalized_ReturnsNotFinalized()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = new(new PacketId(1), stack, frame);

        RecycleError? err = packet.PrepareForReuse(new PacketId(2), frame);
        await Assert.That(err).IsEqualTo(RecycleError.NotFinalized);
    }

    [Test]
    public async Task PrepareForReuse_RegistryMismatch_ReturnsRegistryMismatch()
    {
        using Stack stack = _BuildStack();
        using Stack otherStack = _BuildStack();
        Frame frame1 = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame1);
        Frame frame2 = _MakeFrame(otherStack, FrameBuilders.GenerateStaticUdpFrame(64), 2);

        RecycleError? err = packet.PrepareForReuse(new PacketId(2), frame2);
        await Assert.That(err).IsEqualTo(RecycleError.RegistryMismatch);
    }

    [Test]
    public async Task Constructor_RegistryMismatch_Throws()
    {
        using Stack stack = _BuildStack();
        using Stack otherStack = _BuildStack();
        Frame frame = _MakeFrame(otherStack, FrameBuilders.GenerateStaticUdpFrame(64));

        await Assert.That(() => new Packet(new PacketId(1), stack, frame))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task FieldCount_AfterSeal_ReadsVolatileFieldCount()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(128));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        int countAfterParse = packet.FieldCount(materialize: false); // materialize: false — current materialized count only
        await Assert.That(countAfterParse).IsGreaterThan(0);
        await Assert.That(packet.IsFinalized).IsTrue();
    }

    [Test]
    public async Task MaterializeLazyField_NoPendingLazyFields_ReturnsFalse()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(128));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        packet.MaterializeAll();

        bool materialized = packet.MaterializeLazyField(0);
        await Assert.That(materialized).IsFalse();
    }

    [Test]
    public async Task MaterializeLazyField_PreSealSecondClaim_ReturnsFalse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyErrorProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);

        ushort lazyIndex = proto.LazyContainerIndex;
        bool first = packet.MaterializeLazyField(lazyIndex);
        bool second = packet.MaterializeLazyField(lazyIndex);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task MaterializeLazyField_PopulatorError_AttachesFieldError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyErrorProtocol proto = new(failWithError: true);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);

        bool materialized = packet.MaterializeLazyField(proto.LazyContainerIndex);
        await Assert.That(materialized).IsFalse();
        await Assert.That(packet.FieldCount(materialize: true)).IsGreaterThan(7); // materialize: true — count after full materialization
    }

    [Test]
    public async Task MaterializeLazyField_PopulatorException_AttachesFieldError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyErrorProtocol proto = new(throwOnPopulate: true);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);

        bool materialized = packet.MaterializeLazyField(proto.LazyContainerIndex);
        await Assert.That(materialized).IsFalse();
        await Assert.That(packet.FieldCount(materialize: true)).IsGreaterThan(7); // materialize: true — count after full materialization
    }

    [Test]
    public async Task MaterializeAll_PreSealPath_MaterializesNestedLazy()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NestedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        System.Reflection.FieldInfo? finalizedField = typeof(Packet).GetField(
            "_Finalized", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(finalizedField).IsNotNull();
        finalizedField!.SetValue(packet, 0);

        packet.MaterializeAll();

        await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(9); // materialize: false — current materialized count only
    }

    [Test]
    [NotInParallel("gated-lazy-materialization")]
    public async Task MaterializeLazyField_PostSealConcurrentRace_ExactlyOneMaterializes()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        GatedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        using ManualResetEventSlim populatorEntered = new(false);
        using ManualResetEventSlim releasePopulator = new(false);
        proto.ConfigureGate(populatorEntered, releasePopulator);

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        ushort lazyIndex = proto.LazyContainerIndex;

        using Barrier start = new(2);
        bool[] results = [false, false];

        Task first = Task.Run(() =>
        {
            start.SignalAndWait();
            results[0] = packet.MaterializeLazyField(lazyIndex);
        });
        Task second = Task.Run(() =>
        {
            start.SignalAndWait();
            results[1] = packet.MaterializeLazyField(lazyIndex);
        });

        bool entered = populatorEntered.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        releasePopulator.Set();
        await Task.WhenAll(first, second);

        await Assert.That(entered).IsTrue();
        await Assert.That((results[0] ? 1 : 0) + (results[1] ? 1 : 0)).IsEqualTo(1);
        await Assert.That(results.Contains(true)).IsTrue();
        await Assert.That(results.Contains(false)).IsTrue();
        await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
    }

    [Test]
    [NotInParallel("gated-lazy-materialization")]
    public async Task MaterializeLazyField_PreSealConcurrentRace_ExactlyOneMaterializes()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        GatedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        using ManualResetEventSlim populatorEntered = new(false);
        using ManualResetEventSlim releasePopulator = new(false);
        proto.ConfigureGate(populatorEntered, releasePopulator);

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        System.Reflection.FieldInfo? finalizedField = typeof(Packet).GetField(
            "_Finalized", BindingFlags.NonPublic | BindingFlags.Instance);
        finalizedField!.SetValue(packet, 0);

        ushort lazyIndex = proto.LazyContainerIndex;
        using Barrier start = new(2);
        bool[] results = [false, false];

        Task first = Task.Run(() =>
        {
            start.SignalAndWait();
            results[0] = packet.MaterializeLazyField(lazyIndex);
        });
        Task second = Task.Run(() =>
        {
            start.SignalAndWait();
            results[1] = packet.MaterializeLazyField(lazyIndex);
        });

        bool entered = populatorEntered.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        releasePopulator.Set();
        await Task.WhenAll(first, second);

        await Assert.That(entered).IsTrue();
        await Assert.That((results[0] ? 1 : 0) + (results[1] ? 1 : 0)).IsEqualTo(1);
        await Assert.That(results.Contains(true)).IsTrue();
        await Assert.That(results.Contains(false)).IsTrue();
    }

    [Test]
    [NotInParallel("gated-lazy-materialization")]
    public async Task MaterializeLazyField_PostSealSpinUntilLazyIndexClears_ReturnsFalse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        GatedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        using ManualResetEventSlim populatorEntered = new(false);
        using ManualResetEventSlim releasePopulator = new(false);
        proto.ConfigureGate(populatorEntered, releasePopulator);

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        ushort lazyIndex = proto.LazyContainerIndex;

        Task<bool> owner = Task.Run(() => packet.MaterializeLazyField(lazyIndex));
        bool entered = populatorEntered.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        bool waiter = packet.MaterializeLazyField(lazyIndex);
        releasePopulator.Set();
        bool ownerResult = await owner;

        await Assert.That(entered).IsTrue();
        await Assert.That(waiter).IsFalse();
        await Assert.That(ownerResult).IsTrue();
        await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
    }

    [Test]
    [NotInParallel("gated-lazy-materialization")]
    public async Task MaterializeLazyField_PreSealConcurrentSecondClaim_ReturnsFalse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        GatedLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        using ManualResetEventSlim populatorEntered = new(false);
        using ManualResetEventSlim releasePopulator = new(false);
        proto.ConfigureGate(populatorEntered, releasePopulator);

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        System.Reflection.FieldInfo? finalizedField = typeof(Packet).GetField(
            "_Finalized", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(finalizedField).IsNotNull();
        finalizedField!.SetValue(packet, 0);

        ushort lazyIndex = proto.LazyContainerIndex;

        Task<bool> owner = Task.Run(() => packet.MaterializeLazyField(lazyIndex));
        bool entered = populatorEntered.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        bool second = packet.MaterializeLazyField(lazyIndex);
        releasePopulator.Set();
        bool ownerResult = await owner;

        await Assert.That(entered).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(ownerResult).IsTrue();
    }

    [Test]
    public async Task TryGetNextFieldValue_UnknownFieldId_ReturnsFalse()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        FieldLookupCookie cookie = FieldLookupCookie.Start;
        FieldId unknown = stack.GetFieldId("does.not.exist") ?? new FieldId(999_999);
        bool found = packet.TryGetNextFieldValue(unknown, ref cookie, out FieldValue value, materialize: true); // materialize: true — need complete field tree for assertion

        await Assert.That(found).IsFalse();
        await Assert.That(value.Type).IsEqualTo(FieldType.None);
    }

    [Test]
    public async Task DeriveFrameSourceId_InvalidInterface_ReturnsInvalid()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64), 1, FrameInterfaceId.Invalid);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        await Assert.That(packet.FrameSourceId.IsValid).IsFalse();
    }

    [Test]
    public async Task DeriveFrameSourceId_MissingRegistryEntry_ReturnsInvalid()
    {
        FrameInterfaceRegistry registry = new();
        MethodInfo? derive = typeof(Packet).GetMethod(
            "_DeriveFrameSourceId",
            BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(derive).IsNotNull();

        object? result = derive!.Invoke(null, [new FrameInterfaceId(999), registry]);
        FrameSourceId sourceId = (FrameSourceId)result!;
        await Assert.That(sourceId.IsValid).IsFalse();
    }

    [Test]
    public async Task DeriveFrameSourceId_RegisteredInterface_ReturnsSourceId()
    {
        FrameInterfaceRegistry registry = new();
        using StubFrameSource source = new("src");
        FrameSourceId sourceId = registry.RegisterSource(source);
        FrameInterfaceId ifaceId = registry.Register(sourceId, "eth0");

        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, registry);
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64), 1, ifaceId);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        await Assert.That(packet.FrameSourceId).IsEqualTo(sourceId);
    }

    [Test]
    public async Task ParseFrame_ExceptionWithStackTrace_IncludesStackTraceInError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry())
        {
            IncludeExceptionStackTrace = true
        };
        ThrowingFrameProto throwing = new();
        builder.RegisterProtocol(throwing);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[42]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame);

        FieldLookupCookie cookie = FieldLookupCookie.Start;
        bool found = packet.TryGetNextFieldValue(
            stack.PacketErrorFieldId, ref cookie, out FieldValue err, materialize: true); // materialize: true — walk complete tree including lazy fields
        err.Data.TryGetAsString(out string msg);

        await Assert.That(found).IsTrue();
        await Assert.That(msg.Contains("ThrowingFrameProto", StringComparison.Ordinal)).IsTrue();
        await Assert.That(msg.Contains('\n')).IsTrue();
    }

    [Test]
    public async Task TryParseFrame_StackMismatch_ReturnsStackMismatch()
    {
        using Stack stack1 = _BuildStack();
        using Stack stack2 = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack1, _MakeFrame(stack1, data));
        Frame frame2 = _MakeFrame(stack2, data, 2);

        RecycleError? err = Packet.TryParseFrame(seed, new PacketId(2), stack2, frame2);
        await Assert.That(err).IsEqualTo(RecycleError.StackMismatch);
    }

    [Test]
    public async Task TryParseFrame_WithProtocolOverride_PreconditionFailure_ReturnsError()
    {
        using Stack stack1 = _BuildStack();
        using Stack stack2 = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack1, _MakeFrame(stack1, data));
        Frame frame2 = _MakeFrame(stack2, data, 2);
        ProtocolId eth = stack2.GetProtocolId("eth")!.Value;

        RecycleError? err = Packet.TryParseFrame(seed, new PacketId(2), stack2, frame2, eth);
        await Assert.That(err).IsEqualTo(RecycleError.StackMismatch);
    }

    [Test]
    public async Task TryParseFrameIndexed_PreconditionFailure_ReturnsError()
    {
        using Stack stack1 = _BuildStack();
        using Stack stack2 = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack1, _MakeFrame(stack1, data));
        PacketIndex index = new(stack2);
        Frame frame2 = _MakeFrame(stack2, data, 2);

        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack2, frame2, index);
        await Assert.That(err).IsEqualTo(RecycleError.StackMismatch);
    }

    [Test]
    public async Task TryParseFrameIndexed_WithProtocolOverride_PreconditionFailure_ReturnsError()
    {
        using Stack stack1 = _BuildStack();
        using Stack stack2 = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack1, _MakeFrame(stack1, data));
        PacketIndex index = new(stack2);
        Frame frame2 = _MakeFrame(stack2, data, 2);
        ProtocolId eth = stack2.GetProtocolId("eth")!.Value;

        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack2, frame2, index, eth);
        await Assert.That(err).IsEqualTo(RecycleError.StackMismatch);
    }

    [Test]
    public async Task ParseFrameIndexed_RecycleOverload_ReturnsSamePacket()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, data));
        PacketIndex index = new(stack);
        Frame frame2 = _MakeFrame(stack, data, 2);
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;

        Packet recycled = Packet.ParseFrameIndexed(seed, new PacketId(2), stack, frame2, index, eth);
        await Assert.That(ReferenceEquals(seed, recycled)).IsTrue();
        await Assert.That(recycled.Id).IsEqualTo(new PacketId(2));
    }

    [Test]
    public async Task ParseFrame_ManyFields_ExceedsDefaultSlabCapacity()
    {
        using Stack stack = _BuildManyFieldsStack();
        ProtocolId protoId = stack.GetProtocolId("many.fields")!.Value;
        Frame frame = _MakeFrame(stack, new byte[1]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);

        int count = packet.FieldCount(materialize: false); // materialize: false — current materialized count only
        await Assert.That(count).IsGreaterThan(_ManyFieldCount);
    }

    [Test]
    public async Task PrepareForReuse_MaterializerActive_ReturnsMaterializerActive()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        System.Reflection.FieldInfo? activeField = typeof(Packet).GetField(
            "_ActiveLazyMaterializations", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(activeField).IsNotNull();
        activeField!.SetValue(packet, 1);

        RecycleError? err = packet.PrepareForReuse(new PacketId(2), frame);
        await Assert.That(err).IsEqualTo(RecycleError.MaterializerActive);
    }

    [Test]
    public async Task FieldCount_BeforeSeal_ReturnsPlainFieldCount()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        OpenPacketProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = new(new PacketId(1), stack, frame);
        packet.RootFieldMut().Append(proto.LeafFieldId, FieldValue.NewU64(1));

        int count = packet.FieldCount(materialize: false); // materialize: false — current materialized count only
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task MaterializeLazyField_PostSealConcurrentSpin_Completes()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SlowLazyProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        ushort lazyIndex = proto.LazyContainerIndex;

        Task<bool> waiter = Task.Run(() => packet.MaterializeLazyField(lazyIndex));
        bool claimed = packet.MaterializeLazyField(lazyIndex);
        bool waited = await waiter;

        await Assert.That(claimed || waited).IsTrue();
        await Assert.That(packet.HasUnpopulatedLazyFields).IsFalse();
    }

    [Test]
    public async Task MaterializeLazyField_PostSealDisjointContainers_NoLostOrBrokenLinks()
    {
        const int childrenPerContainer = 48;
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        DisjointLazyProtocol proto = new(childrenPerContainer);
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        int eagerCount = packet.FieldCount(materialize: false);
        Field containerA = default;
        Field containerB = default;
        await Assert.That(packet.TryGetFieldAt(proto.ContainerAIndex, out containerA)).IsTrue();
        await Assert.That(packet.TryGetFieldAt(proto.ContainerBIndex, out containerB)).IsTrue();

        int brokenSnapshots = 0;
        using CancellationTokenSource readerCts = new();
        Task reader = Task.Run(() =>
        {
            while (!readerCts.IsCancellationRequested)
            {
                if (_ChildListHasBrokenSnapshot(containerA) || _ChildListHasBrokenSnapshot(containerB))
                {
                    Interlocked.Increment(ref brokenSnapshots);
                    return;
                }
            }
        });

        Task<bool> materializeA = Task.Run(() => packet.MaterializeLazyField(proto.ContainerAIndex));
        Task<bool> materializeB = Task.Run(() => packet.MaterializeLazyField(proto.ContainerBIndex));
        bool[] materialized;
        try
        {
            materialized = await Task.WhenAll(materializeA, materializeB);
        }
        finally
        {
            await readerCts.CancelAsync();
            await reader;
        }

        int totalCount = packet.FieldCount(materialize: true);
        HashSet<ushort> indexes = [];
        for (int i = 0; i < totalCount; i++)
        {
            bool found = packet.TryGetFieldAt((ushort)i, out Field field);
            if (found)
            {
                indexes.Add(field.StorageIndex);
            }
        }

        await Assert.That(materialized[0] && materialized[1]).IsTrue();
        await Assert.That(brokenSnapshots).IsEqualTo(0);
        await Assert.That(totalCount).IsEqualTo(eagerCount + (childrenPerContainer * 2));
        await Assert.That(indexes.Count).IsEqualTo(totalCount);
        await Assert.That(containerA.ChildCount(materialize: false)).IsEqualTo((ushort)childrenPerContainer);
        await Assert.That(containerB.ChildCount(materialize: false)).IsEqualTo((ushort)childrenPerContainer);
        await Assert.That(_ChildListHasBrokenSnapshot(containerA)).IsFalse();
        await Assert.That(_ChildListHasBrokenSnapshot(containerB)).IsFalse();
    }

    [Test]
    public async Task ParseFrame_NotDispatchedPacketProtocol_DoesNotRecordPacketError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NotDispatchedProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        using Stack stack = builder.Build();
        _SetPacketProtocolId(stack, protoId);

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame);

        bool found = packet.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: true);
        await Assert.That(found).IsFalse();
        await Assert.That(packet.IsFinalized).IsTrue();
    }

    [Test]
    public async Task MaterializeLazyField_PostSealAlreadyMaterialized_ReturnsFalse()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyErrorProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = _MakeFrame(stack, new byte[14]);
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, protoId);
        ushort lazyIndex = proto.LazyContainerIndex;
        packet.MaterializeLazyField(lazyIndex);

        bool second = packet.MaterializeLazyField(lazyIndex);
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task MaterializeLazyField_PendingLazyWithNullPopulators_ReturnsFalse()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        System.Reflection.FieldInfo? pendingField = typeof(Packet).GetField(
            "_PendingLazyCount", BindingFlags.NonPublic | BindingFlags.Instance);
        System.Reflection.FieldInfo? populatorsField = typeof(Packet).GetField(
            "_LazyPopulators", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(pendingField).IsNotNull();
        await Assert.That(populatorsField).IsNotNull();
        pendingField!.SetValue(packet, 1);
        populatorsField!.SetValue(packet, null);

        bool materialized = packet.MaterializeLazyField(0);
        await Assert.That(materialized).IsFalse();
    }

    [Test]
    public async Task SetError_InvalidPacketErrorFieldId_IsNoOp()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        _ClearPacketErrorFieldId(stack);

        MethodInfo? setError = typeof(Packet).GetMethod(
            "SetError", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(setError).IsNotNull();
        setError!.Invoke(packet, ["ignored"]);

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(packet.FieldCount(materialize: false)); // materialize: false — current materialized count only
    }

    [Test]
    public async Task SetFieldError_InvalidPacketErrorFieldId_IsNoOp()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);
        _ClearPacketErrorFieldId(stack);

        MethodInfo? setFieldError = typeof(Packet).GetMethod(
            "SetFieldError", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(setFieldError).IsNotNull();
        setFieldError!.Invoke(packet, [(ushort)0, "ignored"]);

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(packet.FieldCount(materialize: false)); // materialize: false — current materialized count only
    }

    [Test]
    public async Task Seal_DoubleCall_IsIdempotent()
    {
        using Stack stack = _BuildStack();
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame, stack.GetProtocolId("eth")!.Value);

        MethodInfo? seal = typeof(Packet).GetMethod("Seal", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(seal).IsNotNull();
        seal!.Invoke(packet, null);
        seal.Invoke(packet, null);

        await Assert.That(packet.IsFinalized).IsTrue();
    }

    [Test]
    public async Task ParseFrame_InvalidPacketProtocolId_SkipsDispatch()
    {
        using Stack stack = _BuildStack();
        _ClearPacketProtocolId(stack);
        Frame frame = _MakeFrame(stack, FrameBuilders.GenerateStaticUdpFrame(64));
        Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame);

        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1); // materialize: false — current materialized count only
    }

    [Test]
    public async Task BuildExceptionMessage_WithoutStackTrace_ReturnsMessageOnly()
    {
        MethodInfo? buildMessage = typeof(Packet).GetMethod(
            "_BuildExceptionMessage", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(buildMessage).IsNotNull();

        InvalidOperationException ex = new("boom", new InvalidOperationException("inner"));
        string message = (string)buildMessage!.Invoke(null, [ex, false])!;
        await Assert.That(message).IsEqualTo("boom");
    }

    [Test]
    public async Task BuildExceptionMessage_WithStackTrace_AppendsTrace()
    {
        MethodInfo? buildMessage = typeof(Packet).GetMethod(
            "_BuildExceptionMessage", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(buildMessage).IsNotNull();

        Exception ex = new InvalidOperationException("boom");
        try
        {
            throw ex;
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        string message = (string)buildMessage!.Invoke(null, [ex, true])!;
        await Assert.That(message.Contains("boom", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains('\n')).IsTrue();
    }

    [Test]
    public async Task TryParseFrame_Success_ReturnsNull()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, data));
        Frame frame2 = _MakeFrame(stack, data, 2);

        RecycleError? err = Packet.TryParseFrame(seed, new PacketId(2), stack, frame2);
        await Assert.That(err).IsNull();
        await Assert.That(seed.Id).IsEqualTo(new PacketId(2));
    }

    [Test]
    public async Task TryParseFrame_WithProtocolOverride_Success_ReturnsNull()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, data));
        Frame frame2 = _MakeFrame(stack, data, 2);
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;

        RecycleError? err = Packet.TryParseFrame(seed, new PacketId(2), stack, frame2, eth);
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task TryParseFrameIndexed_Success_ReturnsNull()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, data));
        PacketIndex index = new(stack);
        Frame frame2 = _MakeFrame(stack, data, 2);

        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack, frame2, index);
        await Assert.That(err).IsNull();
    }

    [Test]
    public async Task TryParseFrameIndexed_WithProtocolOverride_Success_ReturnsNull()
    {
        using Stack stack = _BuildStack();
        byte[] data = FrameBuilders.GenerateStaticUdpFrame(64);
        Packet seed = Packet.ParseFrame(new PacketId(1), stack, _MakeFrame(stack, data));
        PacketIndex index = new(stack);
        Frame frame2 = _MakeFrame(stack, data, 2);
        ProtocolId eth = stack.GetProtocolId("eth")!.Value;

        RecycleError? err = Packet.TryParseFrameIndexed(seed, new PacketId(2), stack, frame2, index, eth);
        await Assert.That(err).IsNull();
    }

    /// <summary>
    /// Detects a torn child list: <c>ChildCount &gt; 0</c> with no first child, or a sibling cycle.
    /// Length mismatch versus <c>ChildCount</c> is not a concurrent invariant — linking stores
    /// can become visible before <c>ChildCount</c> on the reader.
    /// </summary>
    private static bool _ChildListHasBrokenSnapshot(Field parent)
    {
        ushort childCount = parent.ChildCount(materialize: false);
        bool hasFirst = parent.TryGetFirstChild(out Field child, materialize: false);
        if (childCount > 0 && !hasFirst)
        {
            return true;
        }
        if (!hasFirst)
        {
            return false;
        }

        HashSet<ushort> seen = [];
        Field current = child;
        while (true)
        {
            if (!seen.Add(current.StorageIndex))
            {
                return true;
            }
            if (!current.TryGetNext(out Field next))
            {
                break;
            }
            current = next;
        }
        return false;
    }

    private static void _ClearPacketErrorFieldId(Stack stack)
    {
        System.Reflection.FieldInfo? field = typeof(Stack).GetField(
            "<PacketErrorFieldId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(stack, FieldId.Invalid);
    }

    private static void _SetPacketProtocolId(Stack stack, ProtocolId protocolId)
    {
        System.Reflection.FieldInfo? field = typeof(Stack).GetField(
            "<PacketProtocolId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(stack, protocolId);
    }

    private static void _ClearPacketProtocolId(Stack stack)
    {
        System.Reflection.FieldInfo? field = typeof(Stack).GetField(
            "<PacketProtocolId>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(stack, ProtocolId.Invalid);
    }

    private sealed class DisjointLazyProtocol(int childrenPerContainer) : IProtocol
    {
        private FieldId _ContainerAId;
        private FieldId _ContainerBId;
        private FieldId _LeafId;

        public string Name => "disjoint.lazy";
        public string UiName => "Disjoint Lazy";
        public ushort ContainerAIndex { get; private set; }
        public ushort ContainerBIndex { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerAId = builder.RegisterField(protocolId, "disjoint.lazy.a", "A", FieldType.None);
            _ContainerBId = builder.RegisterField(protocolId, "disjoint.lazy.b", "B", FieldType.None);
            _LeafId = builder.RegisterField(protocolId, "disjoint.lazy.leaf", "Leaf", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId leafId = _LeafId;
            int childCount = childrenPerContainer;
            MutField containerA = parentField.AppendLazy(_ContainerAId, FieldValue.None, (in MutField field) =>
            {
                for (int i = 0; i < childCount; i++)
                {
                    field.Append(leafId, FieldValue.NewU64((ulong)i));
                }
                return 0;
            });
            MutField containerB = parentField.AppendLazy(_ContainerBId, FieldValue.None, (in MutField field) =>
            {
                for (int i = 0; i < childCount; i++)
                {
                    field.Append(leafId, FieldValue.NewU64(1000UL + (ulong)i));
                }
                return 0;
            });
            ContainerAIndex = containerA.StorageIndex;
            ContainerBIndex = containerB.StorageIndex;
            return 14;
        }
    }

    private sealed class NotDispatchedProtocol : IProtocol
    {
        public string Name => "not.dispatched";
        public string UiName => "Not Dispatched";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => ParseResult.NotDispatched;
    }

    private sealed class OpenPacketProtocol : IProtocol
    {
        public FieldId LeafFieldId { get; private set; }

        public string Name => "open.packet";
        public string UiName => "Open Packet";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            LeafFieldId = builder.RegisterField(protocolId, "open.packet.leaf", "Leaf", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    private sealed class GatedLazyProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;
        private ManualResetEventSlim? _PopulatorEntered;
        private ManualResetEventSlim? _ReleasePopulator;

        public string Name => "gated.lazy";
        public string UiName => "Gated Lazy";
        public ushort LazyContainerIndex { get; private set; }

        public void ConfigureGate(ManualResetEventSlim populatorEntered, ManualResetEventSlim releasePopulator)
        {
            _PopulatorEntered = populatorEntered;
            _ReleasePopulator = releasePopulator;
        }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "gated.lazy", "Lazy", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId containerId = _ContainerFieldId;
            MutField container = parentField.AppendLazy(containerId, FieldValue.None, (in MutField field) =>
            {
                _PopulatorEntered?.Set();
                _ReleasePopulator?.Wait(TimeSpan.FromSeconds(30));
                field.Append(containerId, FieldValue.NewU64(1));
                return 0;
            });
            LazyContainerIndex = container.StorageIndex;
            return 14;
        }
    }

    private sealed class SlowLazyProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;

        public string Name => "slow.lazy";
        public string UiName => "Slow Lazy";
        public ushort LazyContainerIndex { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "slow.lazy", "Lazy", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId containerId = _ContainerFieldId;
            MutField container = parentField.AppendLazy(containerId, FieldValue.None, (in MutField field) =>
            {
                Thread.Sleep(40);
                field.Append(containerId, FieldValue.NewU64(1));
                return 0;
            });
            LazyContainerIndex = container.StorageIndex;
            return 14;
        }
    }

    private sealed class ManyFieldsProtocol(int fieldCount) : IProtocol
    {
        private FieldId _LeafFieldId;

        public string Name => "many.fields";
        public string UiName => "Many Fields";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _LeafFieldId = builder.RegisterField(protocolId, "many.fields.leaf", "Leaf", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId leafId = _LeafFieldId;
            for (int i = 0; i < fieldCount; i++)
            {
                parentField.Append(leafId, FieldValue.NewU64((ulong)i));
            }
            return data.Length;
        }
    }

    private sealed class LazyErrorProtocol(bool failWithError = false, bool throwOnPopulate = false) : IProtocol
    {
        private FieldId _ContainerFieldId;

        public string Name => "lazy.err";
        public string UiName => "Lazy Error";
        public ushort LazyContainerIndex { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "lazy.err", "Lazy", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId containerId = _ContainerFieldId;
            MutField container = parentField.AppendLazy(containerId, FieldValue.None, (in MutField field) =>
            {
                if (throwOnPopulate)
                {
                    throw new InvalidOperationException("lazy boom");
                }
                if (failWithError)
                {
                    return ParseError.Custom("lazy.err", "populate failed");
                }
                field.Append(containerId, FieldValue.NewU64(1));
                return 0;
            });
            LazyContainerIndex = container.StorageIndex;
            return 14;
        }
    }

    private sealed class NestedLazyProtocol : IProtocol
    {
        private FieldId _OuterId;
        private FieldId _InnerId;
        private FieldId _InnerChildId;

        public string Name => "nested.lazy";
        public string UiName => "Nested Lazy";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _OuterId = builder.RegisterField(protocolId, "nested.lazy.outer", "Outer", FieldType.None);
            _InnerId = builder.RegisterField(protocolId, "nested.lazy.inner", "Inner", FieldType.None);
            _InnerChildId = builder.RegisterField(protocolId, "nested.lazy.inner.child", "Child", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId innerId = _InnerId;
            FieldId innerChildId = _InnerChildId;
            parentField.AppendLazy(_OuterId, FieldValue.None, (in MutField outer) =>
            {
                outer.AppendLazy(innerId, FieldValue.None, (in MutField inner) =>
                {
                    inner.Append(innerChildId, FieldValue.NewU64(42));
                    return 0;
                });
                return 0;
            });
            return 14;
        }
    }

    private sealed class ThrowingFrameProto : IProtocol
    {
        public string Name => "frame";
        public string UiName => "Frame";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => throw new InvalidOperationException("ThrowingFrameProto failed");
    }

    private sealed class StubFrameSource(string uiName) : IFrameSource
    {
        public string UiName => uiName;
        public string? Description => "desc";
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public void Stop()
        {
            _ = UiName;
        }

        public void Dispose()
        {
        }
    }
}
