// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The end-of-packet call of field observers: one <see cref="IFieldObserver.EndPacket"/> for every
/// <see cref="IFieldObserver.BeginPacket"/> that ran, before <c>TryParse</c> returns, with <c>complete</c> telling whether the
/// observer received every field. A consumer that stages fields during the parse (a tree-free export) relies on exactly this
/// pairing to close or discard what it staged.
/// </summary>
internal sealed class FieldObserverEndPacketTests
{
    #region Helpers

    private static (Stack Stack, ProbeProtocol Probe, ProtocolId Id) _Probe()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProbeProtocol probe = new();
        ProtocolId id = builder.RegisterProtocol(probe);
        probe.RegisterFields(builder, id);
        return (builder.Build(), probe, id);
    }

    private static Frame _Frame(Stack stack, int id) =>
        Frame.Create(
            id,
            Timestamp.FromSecs(id),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static ParseOptions _Options(FieldTreeMode tree, ProtocolId first, params IFieldObserver[] observers)
    {
        ParseObserver[] entries = [.. observers.Select(static o => new ParseObserver { Observer = o, Watch = FieldWatch.All })];
        return new ParseOptions(tree, observers: entries, firstProtocol: first);
    }

    private static bool _Parse(Stack stack, int id, ParseOptions options, out Packet? packet) =>
        Packet.TryParse(id, stack, _Frame(stack, id), in options, out packet, out _);

    #endregion

    #region Tests

    [Test]
    [Arguments(FieldTreeMode.Build)]
    [Arguments(FieldTreeMode.Skip)]
    public async Task EveryBeginIsFollowedByOneEndAfterAllFields(FieldTreeMode tree)
    {
        (Stack stack, ProbeProtocol probe, ProtocolId id) = _Probe();
        using (stack)
        {
            EndRecorder recorder = new("r");
            ParseOptions options = _Options(tree, id, recorder);

            bool parsed = _Parse(stack, 0, options, out _);

            await Assert.That(parsed).IsTrue();
            await Assert.That(recorder.Events.First()).IsEqualTo("r:begin:0");
            await Assert.That(recorder.Events.Last()).IsEqualTo("r:end:0:True");
            await Assert.That(recorder.Events.Count(static e => e.Contains(":end:", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(recorder.Events.Count(static e => e.Contains(":begin:", StringComparison.Ordinal))).IsEqualTo(1);
            await Assert.That(recorder.FieldIds).Contains(probe.A);
            await Assert.That(recorder.FieldIds).Contains(probe.B);
        }
    }

    [Test]
    public async Task ADissectorErrorStillEndsCompleteBecauseErrorsAreFields()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        EndRecorder recorder = new("r");
        ParseObserver[] entries = [new() { Observer = recorder, Watch = FieldWatch.All }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: entries);

        // Three bytes are shorter than an Ethernet header: the dissector records an error field and the parse still succeeds.
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[3], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        bool parsed = Packet.TryParse(0, stack, frame, in options, out _, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(recorder.Events.Last()).IsEqualTo("r:end:0:True");
    }

    [Test]
    public async Task AnObserverThatThrowsMutesBothAndBothEndIncompleteThenTheNextPacketStartsClean()
    {
        (Stack stack, ProbeProtocol _, ProtocolId id) = _Probe();
        using (stack)
        {
            EndRecorder thrower = new("t") { ThrowOnFieldNumber = 2 };
            EndRecorder bystander = new("b");
            ParseOptions options = _Options(FieldTreeMode.Build, id, thrower, bystander);

            _ = _Parse(stack, 0, options, out Packet? first);
            _ = _Parse(stack, 1, options, out _);

            // Packet 0: the first observer throws on its second field. The mute stops every observer, so the second one
            // received fewer fields, and both are told the packet is incomplete.
            await Assert.That(thrower.Events.Last(static e => e.Contains(":end:0:", StringComparison.Ordinal))).IsEqualTo("t:end:0:False");
            await Assert.That(bystander.Events.Last(static e => e.Contains(":end:0:", StringComparison.Ordinal))).IsEqualTo("b:end:0:False");

            // The caught error is a field of the packet.
            FieldId errorId = stack.GetFieldId("packet.error")!.Value;
            await Assert.That(first!.IterFieldsDfs(materialize: true).Any(field => field.FieldId == errorId)).IsTrue();

            // Packet 1: the mute did not leak. Begin, every field, End complete.
            await Assert.That(thrower.Events.Last()).IsEqualTo("t:end:1:True");
            await Assert.That(bystander.Events.Last()).IsEqualTo("b:end:1:True");
            await Assert.That(bystander.Events.Count(static e => e.StartsWith("b:field:1", StringComparison.Ordinal))).IsGreaterThan(1);
        }
    }

    [Test]
    public async Task AThrowingBeginSkipsTheParseAndEndsOnlyTheObserversThatBegan()
    {
        (Stack stack, ProbeProtocol _, ProtocolId id) = _Probe();
        using (stack)
        {
            EndRecorder before = new("a");
            EndRecorder thrower = new("b") { ThrowInBegin = true };
            EndRecorder after = new("c");
            ParseOptions options = _Options(FieldTreeMode.Skip, id, before, thrower, after);

            bool parsed = _Parse(stack, 0, options, out _);

            // Parse still succeeds; the observers that began are ended incomplete, the one that never began gets nothing.
            await Assert.That(parsed).IsTrue();
            await Assert.That(before.Events.Last()).IsEqualTo("a:end:0:False");
            await Assert.That(thrower.Events.Last()).IsEqualTo("b:end:0:False");
            await Assert.That(after.Events).IsEmpty();
        }
    }

    [Test]
    public async Task AReplayCallsBeginFieldsAndEndAgain()
    {
        (Stack stack, ProbeProtocol _, ProtocolId id) = _Probe();
        using (stack)
        {
            EndRecorder recorder = new("r");
            ParseOptions options = _Options(FieldTreeMode.Skip, id, recorder);

            _ = _Parse(stack, 0, options, out _);
            int firstRunEvents = recorder.Events.Count;
            _ = _Parse(stack, 0, options, out _); // the same id again is a replay

            await Assert.That(recorder.Events.Count).IsEqualTo(firstRunEvents * 2);
            await Assert.That(recorder.Events.Count(static e => e == "r:begin:0")).IsEqualTo(2);
            await Assert.That(recorder.Events.Count(static e => e == "r:end:0:True")).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AFieldExpandedInEndPacketReachesTheTreeButNoObserver()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyEndProtocol proto = new();
        ProtocolId id = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, id);
        using Stack stack = builder.Build();

        // B watches the lazy child only (no index group, so the parse does not expand the container for it) and is listed first,
        // so its packet has ended when A expands the container in its own end call.
        EndRecorder bystander = new("b");
        ExpandingObserver expander = new(proto.ChildId);
        ParseObserver[] entries =
        [
            new() { Observer = bystander, Watch = FieldWatch.Only(stack, proto.ChildId) },
            new() { Observer = expander, Watch = FieldWatch.Only(stack, proto.PlainId) },
        ];
        ParseOptions options = new(FieldTreeMode.Build, observers: entries, firstProtocol: id);

        bool parsed = _Parse(stack, 0, options, out Packet? packet);

        bool childInTree = packet!.TryGetFieldValue(proto.ChildId, out _, materialize: false);
        await Assert.That(parsed).IsTrue();
        await Assert.That(expander.Expanded).IsTrue();
        await Assert.That(childInTree).IsTrue();
        await Assert.That(bystander.Events).IsEquivalentTo(["b:begin:0", "b:end:0:True"]);
    }

    [Test]
    public async Task AThrowingEndPacketOfATreeFreeParseIsRecordedInTheCache()
    {
        (Stack stack, ProbeProtocol _, ProtocolId id) = _Probe();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(stack.PacketErrorFieldId, ValueCaptureMode.AllOccurrences)]);
            EndRecorder thrower = new("t") { ThrowInEnd = true };
            ParseObserver[] entries = [new() { Observer = thrower, Watch = FieldWatch.All }];
            ParseOptions options = new(FieldTreeMode.Skip, cache, observers: entries, firstProtocol: id);

            bool parsed = _Parse(stack, 0, options, out _);

            ValueCacheSeries<string> errors = cache.GetSeries<string>(stack.PacketErrorFieldId);
            await Assert.That(parsed).IsTrue();
            await Assert.That(errors.Count).IsEqualTo(1);
            await Assert.That(errors[0].Value).Contains("end failed");
        }
    }

    #endregion

    #region Recorder

    /// <summary>Records begin, field, and end calls as words; can throw in a chosen place.</summary>
    private sealed class EndRecorder(string name) : IFieldObserver
    {
        private int _FieldNumber;

        public List<string> Events { get; } = [];

        public List<FieldId> FieldIds { get; } = [];

        /// <summary>Throws from <c>OnField</c> at this 1-based field of the first packet only (0 = never).</summary>
        public int ThrowOnFieldNumber { get; init; }

        public bool ThrowInBegin { get; init; }

        public bool ThrowInEnd { get; init; }

        public void BeginPacket(PacketId id, Timestamp timestamp)
        {
            Events.Add($"{name}:begin:{id.Value}");
            _FieldNumber = 0;
            if (ThrowInBegin)
            {
                throw new InvalidOperationException("begin failed");
            }
        }

        public void OnField(in FieldVisit visit)
        {
            _FieldNumber++;
            Events.Add($"{name}:field:{visit.PacketId.Value}");
            FieldIds.Add(visit.FieldId);

            // Only packet 0 throws, so the following packet proves that the mute does not leak.
            if (ThrowOnFieldNumber != 0 && visit.PacketId.Value == 0 && _FieldNumber == ThrowOnFieldNumber)
            {
                throw new InvalidOperationException("field failed");
            }
        }

        public void EndPacket(PacketId id, bool complete)
        {
            Events.Add($"{name}:end:{id.Value}:{complete}");
            if (ThrowInEnd)
            {
                throw new InvalidOperationException("end failed");
            }
        }
    }

    /// <summary>Keeps the cursor of the plain field and, in its end call, expands the lazy container through a tree lookup.</summary>
    private sealed class ExpandingObserver(FieldId childId) : IFieldObserver
    {
        private Packet? _Packet;

        public bool Expanded { get; private set; }

        public void BeginPacket(PacketId id, Timestamp timestamp) => _Packet = null;

        public void OnField(in FieldVisit visit) => _Packet = visit.Field.Packet;

        public void EndPacket(PacketId id, bool complete) =>
            Expanded = _Packet is not null && _Packet.TryGetFieldValue(childId, out _, materialize: true);
    }

    /// <summary>Appends a plain field and a lazy container with one child outside any index group.</summary>
    private sealed class LazyEndProtocol : IProtocol
    {
        private FieldId _ContainerId;
        private int _Resets;

        public FieldId PlainId { get; private set; }

        public FieldId ChildId { get; private set; }

        public string Name => "lzend";
        public string UiName => "Lazy End";

        public void ResetParseState() => _Resets++;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            PlainId = builder.RegisterField(protocolId, "lzend.plain", "Plain", FieldType.U64);
            _ContainerId = builder.RegisterField(protocolId, "lzend.container", "Container", FieldType.None);
            ChildId = builder.RegisterField(protocolId, "lzend.child", "Child", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId childId = ChildId;
            _ = parentField.Append(PlainId, FieldValue.NewU64(1));
            parentField.AppendLazy(_ContainerId, FieldValue.None, (in container) =>
            {
                _ = container.Append(childId, FieldValue.NewU64(2));
                return 0;
            });
            return data.Length;
        }
    }

    #endregion
}
