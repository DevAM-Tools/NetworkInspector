// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The limits of Core: the catalog, the registration as settings, the live holders of a stack, and the owners that read them
/// (stream, fragment, lazy depth, value cache, interface registry).
/// </summary>
internal sealed class CoreLimitsTests
{
    private const long _MiB = 1L << 20;

    /// <summary>Changes a limit setting the way a front end does: a pending value, then apply.</summary>
    private static void _SetLimit(SettingsManager manager, LimitDefinition definition, LimitValue value)
    {
        Setting setting = manager.GetSetting(definition.Key)
            ?? throw new InvalidOperationException("The limit is not registered as a setting.");
        setting.SetPendingValue(SettingValue.Limit(value));
        setting.Apply();
    }

    [Test]
    public async Task CatalogHasNoDuplicateKeys()
    {
        IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check(CoreLimits.All);

        await Assert.That(problems.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TheCatalogListsTheNineLimitsOfCore()
    {
        await Assert.That(CoreLimits.All.Count).IsEqualTo(9);
        await Assert.That(CoreLimits.All.Select(static definition => definition.Key).Distinct().Count()).IsEqualTo(9);
    }

    [Test]
    [Arguments("reassembly.maxStreamBufferSize", 1 * _MiB)]
    [Arguments("reassembly.maxStreamPduSize", 64L << 10)]
    [Arguments("reassembly.maxStreamRebases", 64L)]
    [Arguments("reassembly.maxPendingDatagrams", 1_024L)]
    [Arguments("reassembly.maxFragmentStorage", 64 * _MiB)]
    [Arguments("reassembly.maxFragmentsPerDatagram", 8_192L)]
    [Arguments("packet.maxLazyDepth", 128L)]
    [Arguments("valuecache.maxRows", 100_000_000L)]
    [Arguments("frames.maxInterfaces", 1_048_576L)]
    public async Task EveryLimitHasTheAgreedDefaultAndAllowsOff(string key, long expectedDefault)
    {
        LimitDefinition definition = CoreLimits.All.Single(candidate => candidate.Key == key);

        await Assert.That(definition.Default).IsEqualTo(expectedDefault);
        await Assert.That(definition.AllowOff).IsTrue();
        await Assert.That(definition.TryValidate(LimitValue.Of(definition.Minimum), out _)).IsTrue();
        await Assert.That(definition.TryValidate(LimitValue.Of(definition.Maximum), out _)).IsTrue();
        await Assert.That(definition.TryValidate(LimitValue.Of(definition.Maximum + 1), out _)).IsFalse();
        await Assert.That(definition.TryValidate(LimitValue.Off, out _)).IsTrue();
    }

    [Test]
    public async Task AStackRegistersEveryLimitAsASettingAtItsDefault()
    {
        using SettingsManager manager = new();
        using Stack stack = _PlainStack(manager);

        foreach (LimitDefinition definition in CoreLimits.All)
        {
            Limit? live = stack.Settings.GetLiveLimit(definition.Key);
            await Assert.That(live).IsNotNull();
            await Assert.That(live!.Ceiling).IsEqualTo(definition.Default);
        }
    }

    [Test]
    public async Task TheLiveHoldersOfAStackFollowTheSettings()
    {
        using SettingsManager manager = new();
        using Stack stack = _PlainStack(manager);

        _SetLimit(manager, CoreLimits.MaxStreamRebases, LimitValue.Of(8));
        _SetLimit(manager, CoreLimits.MaxLazyDepth, LimitValue.Off);

        await Assert.That(stack.Limits.MaxStreamRebases.Ceiling).IsEqualTo(8L);
        await Assert.That(stack.Limits.MaxLazyDepth.IsOff).IsTrue();
        await Assert.That(CoreLimitSet.IntCeiling(stack.Limits.MaxLazyDepth)).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task TheDefaultSetHoldsTheFixedDefaults()
    {
        await Assert.That(CoreLimitSet.Defaults.MaxStreamBufferSize.Ceiling).IsEqualTo(1 * _MiB);
        await Assert.That(CoreLimitSet.Defaults.MaxInterfaces.Ceiling).IsEqualTo(1_048_576L);
    }

    [Test]
    public async Task StreamConfigurationsTakeTheLiveLimitUnlessTheyAssignTheirOwn()
    {
        using SettingsManager manager = new();
        using Stack stack = _PlainStack(manager);
        _SetLimit(manager, CoreLimits.MaxStreamBufferSize, LimitValue.Of(8 * _MiB));
        _SetLimit(manager, CoreLimits.MaxStreamPduSize, LimitValue.Of(2 * _MiB));
        StreamReassemblyConfig unassigned = new();
        StreamReassemblyConfig assigned = new() { MaxBufferSize = 4 * 1024 * 1024, MaxPduSize = 100_000 };

        StreamReassemblyConfig effectiveUnassigned = unassigned.WithLimits(stack.Limits);
        StreamReassemblyConfig effectiveAssigned = assigned.WithLimits(stack.Limits);

        await Assert.That(unassigned.MaxBufferSize).IsEqualTo(1024 * 1024);
        await Assert.That(effectiveUnassigned.MaxBufferSize).IsEqualTo(8 * 1024 * 1024);
        await Assert.That(effectiveUnassigned.MaxPduSize).IsEqualTo(2 * 1024 * 1024);
        await Assert.That(effectiveAssigned.MaxBufferSize).IsEqualTo(4 * 1024 * 1024);
        await Assert.That(effectiveAssigned.MaxPduSize).IsEqualTo(100_000);
    }

    [Test]
    public async Task FragmentOptionsTakeTheLiveLimitsAndAReassemblerAppliesThem()
    {
        using SettingsManager manager = new();
        using Stack stack = _PlainStack(manager);
        _SetLimit(manager, CoreLimits.MaxPendingDatagrams, LimitValue.Of(2));
        _SetLimit(manager, CoreLimits.MaxFragmentStorage, LimitValue.Of(5 * _MiB));
        _SetLimit(manager, CoreLimits.MaxFragmentsPerDatagram, LimitValue.Off);

        FragmentReassemblyOptions options = FragmentReassemblyOptions.IPv4.WithLimits(stack.Limits);
        FragmentReassembler<FragmentLimitKey> reassembler = new(FragmentReassemblyOptions.IPv4);
        reassembler.ApplyLimits(stack.Limits);

        await Assert.That(options.MaxPending).IsEqualTo(2);
        await Assert.That(options.MaxBufferedBytes).IsEqualTo(5 * _MiB);
        await Assert.That(options.MaxFragments).IsEqualTo(int.MaxValue);
        await Assert.That(options.MaxDatagramLength).IsEqualTo(65535);
        await Assert.That(options.OverlapPolicy).IsEqualTo(FragmentOverlapPolicy.KeepLargestDuplicate);
    }

    [Test]
    public async Task TheRegistryRefusesTheInterfaceBeyondTheLimitAndNamesIt()
    {
        using SettingsManager manager = new();
        FrameInterfaceRegistry registry = new();
        using Stack stack = new StackBuilder(manager, registry).Build();
        using LimitTestSource source = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        _SetLimit(manager, CoreLimits.MaxInterfaces, LimitValue.Of(3));

        _ = registry.Register(sourceId, "eth0");
        _ = registry.Register(sourceId, "eth1");
        _ = registry.Register(sourceId, "eth2");
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => registry.Register(sourceId, "eth3"));

        await Assert.That(refused.Message).Contains("frames.maxInterfaces");
        await Assert.That(refused.Message).Contains("Maximum frame interface count exceeded");
        await Assert.That(registry.Count).IsEqualTo(3);

        // Raising the limit takes effect at the next registration.
        _SetLimit(manager, CoreLimits.MaxInterfaces, LimitValue.Of(4));
        FrameInterfaceId fourth = registry.Register(sourceId, "eth3");
        await Assert.That(fourth.Value).IsEqualTo(3);
    }

    [Test]
    public async Task AValueCacheStopsAtTheRowLimitSetsTheFlagAndNamesTheLimit()
    {
        using SettingsManager manager = new();
        (Stack stack, LimitTestProtocol protocol, ProtocolId id) = _BuildLimitStack(manager);
        using (stack)
        {
            _SetLimit(manager, CoreLimits.MaxValueCacheRows, LimitValue.Of(3));
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(protocol.Item, ValueCaptureMode.AllOccurrences)]);
            protocol.Appends = 10;
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: id);

            bool parsed = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? packet, out ParseFailure _);

            // Only the rows up to the limit were recorded, the flag is set for good, and the message names the limit.
            ValueCacheSeries<ulong> item = cache.GetSeries<ulong>(protocol.Item);
            await Assert.That(parsed).IsTrue();
            await Assert.That(packet?.ErrorCount ?? -1).IsEqualTo(0);
            await Assert.That(cache.IsRecordingIncomplete).IsTrue();
            await Assert.That(item.Count).IsLessThanOrEqualTo(3);
            await Assert.That(cache.RecordingLimitMessage).IsNotNull();
            await Assert.That(cache.RecordingLimitMessage!).Contains("valuecache.maxRows");
        }
    }

    [Test]
    public async Task AValueCacheUnderTheLimitIsComplete()
    {
        using SettingsManager manager = new();
        (Stack stack, LimitTestProtocol protocol, ProtocolId id) = _BuildLimitStack(manager);
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(protocol.Item, ValueCaptureMode.AllOccurrences)]);
            protocol.Appends = 5;
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: id);

            _ = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out _, out ParseFailure _);

            await Assert.That(cache.IsRecordingIncomplete).IsFalse();
            await Assert.That(cache.RecordingLimitMessage).IsNull();
            await Assert.That(cache.GetSeries<ulong>(protocol.Item).Count).IsEqualTo(5);
        }
    }

    [Test]
    public async Task ASwitchedOffRowLimitRecordsEverything()
    {
        using SettingsManager manager = new();
        (Stack stack, LimitTestProtocol protocol, ProtocolId id) = _BuildLimitStack(manager);
        using (stack)
        {
            _SetLimit(manager, CoreLimits.MaxValueCacheRows, LimitValue.Off);
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(protocol.Item, ValueCaptureMode.AllOccurrences)]);
            protocol.Appends = 50;
            ParseOptions options = new(FieldTreeMode.Build, cache, firstProtocol: id);

            _ = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out _, out ParseFailure _);

            await Assert.That(cache.IsRecordingIncomplete).IsFalse();
            await Assert.That(cache.GetSeries<ulong>(protocol.Item).Count).IsEqualTo(50);
        }
    }

    [Test]
    public async Task ATargetedLazyLookupStopsAtTheDepthLimitAndReportsItOnce()
    {
        using SettingsManager manager = new();
        (Stack stack, LimitTestProtocol protocol, ProtocolId id) = _BuildLimitStack(manager);
        using (stack)
        {
            _SetLimit(manager, CoreLimits.MaxLazyDepth, LimitValue.Of(4));
            protocol.NestedLazyLevels = 10;
            ParseOptions options = new(FieldTreeMode.Build, firstProtocol: id);
            bool parsed = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? packet, out ParseFailure _);
            Packet found = packet ?? throw new InvalidOperationException("The parse failed.");

            // The item sits at the tenth level, so the lookup needs ten expansion rounds; the limit allows four.
            FieldLookupCookie first = FieldLookupCookie.Start;
            bool foundFirst = found.TryGetNextField(protocol.Item, ref first, out _, materialize: true);
            FieldLookupCookie second = FieldLookupCookie.Start;
            bool foundSecond = found.TryGetNextField(protocol.Item, ref second, out _, materialize: true);

            // The cut is a counted packet error that names the limit, made once however often the lookup runs.
            await Assert.That(parsed).IsTrue();
            await Assert.That(foundFirst).IsFalse();
            await Assert.That(foundSecond).IsFalse();
            await Assert.That(found.ErrorCount).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ASwitchedOffDepthLimitFollowsEveryLevel()
    {
        using SettingsManager manager = new();
        (Stack stack, LimitTestProtocol protocol, ProtocolId id) = _BuildLimitStack(manager);
        using (stack)
        {
            _SetLimit(manager, CoreLimits.MaxLazyDepth, LimitValue.Off);
            protocol.NestedLazyLevels = 10;
            ParseOptions options = new(FieldTreeMode.Build, firstProtocol: id);
            Packet found = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? packet, out ParseFailure _)
                ? packet ?? throw new InvalidOperationException("The parse failed.")
                : throw new InvalidOperationException("The parse failed.");

            FieldLookupCookie cookie = FieldLookupCookie.Start;
            bool foundItem = found.TryGetNextField(protocol.Item, ref cookie, out _, materialize: true);

            await Assert.That(foundItem).IsTrue();
            await Assert.That(found.ErrorCount).IsEqualTo(0);
        }
    }

    #region Helpers

    /// <summary>A stack without protocols of its own; its settings hold the limits of Core.</summary>
    private static Stack _PlainStack(SettingsManager manager)
    {
        StackBuilder builder = new(manager, new FrameInterfaceRegistry());
        return builder.Build();
    }

    /// <summary>A key type for a fragment reassembler built by hand.</summary>
    private readonly record struct FragmentLimitKey(int Value) : IEquatable<FragmentLimitKey>;

    private static Frame _Frame(Stack stack, int id) => Frame.Create(
        id, Timestamp.FromSecs(id), new byte[] { 0x01 }, LinkType.Ethernet,
        FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

    private static (Stack Stack, LimitTestProtocol Protocol, ProtocolId Id) _BuildLimitStack(SettingsManager manager)
    {
        StackBuilder builder = new(manager, new FrameInterfaceRegistry());
        LimitTestProtocol protocol = new();
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        return (builder.Build(), protocol, id);
    }

    /// <summary>Appends a number of items, or nested lazy containers, so the tests can reach the limits of one packet.</summary>
    private sealed class LimitTestProtocol : IProtocol
    {
        public FieldId Item { get; private set; }

        public FieldId Container { get; private set; }

        public int Appends { get; set; }

        public int NestedLazyLevels { get; set; }

        public string Name => "limit.test";

        public string UiName => "Limit Test";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            Container = builder.RegisterField(protocolId, "limit.test.container", "Container", FieldType.None);
            Item = builder.RegisterField(protocolId, "limit.test.item", "Item", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            for (int index = 0; index < Appends; index++)
            {
                _ = parentField.Append(Item, FieldValue.NewU64((ulong)index));
            }

            if (NestedLazyLevels > 0)
            {
                _ = parentField.AppendLazy(Container, FieldValue.None, _Nest(NestedLazyLevels));
            }

            return data.Length;
        }

        private LazyPopulator _Nest(int levels) =>
            (in MutField container) =>
            {
                if (levels > 1)
                {
                    _ = container.AppendLazy(Container, FieldValue.None, _Nest(levels - 1));
                }
                else
                {
                    _ = container.Append(Item, FieldValue.NewU64(1));
                }

                return 0;
            };
    }

    /// <summary>A frame source that only exists to own interfaces of a registry.</summary>
    private sealed class LimitTestSource : IFrameSource
    {
        public string UiName => "limit test";

        public string? Description => null;

        public int? EstimatedFrameCount => null;

        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public void Stop() => _ = UiName; // Intentional: prevents CA1822 since this is an interface implementation.

        public void Dispose()
        {
        }
    }

    #endregion
}
