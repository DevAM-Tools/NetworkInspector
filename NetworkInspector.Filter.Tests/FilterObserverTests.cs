// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Tree-free <see cref="FilterObserver"/> compile and match.</summary>
internal sealed class FilterObserverTests
{
    private static readonly FilterObserverOptions _Options = FilterObserverOptions.Default;

    #region Helpers

    private static Packet _Observe(Stack stack, byte[] data, int id, FilterObserver observer)
    {
        Frame frame = Frame.Create(
            new FrameId(id),
            Timestamp.FromNanos(id),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseObserver[] observers = [new() { Observer = observer, Watch = observer.Watch }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
        bool parsed = Packet.TryParse(new PacketId(id), stack, frame, in options, out Packet? packet, out ParseFailure failure);
        if (!parsed || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    private static Packet _Build(Stack stack, byte[] data, int id) =>
        FilterTestHelper.Parse(stack, data, id, id);

    private static (Stack Stack, ObserverProbe Probe, ProtocolId Id) _Probe()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ObserverProbe probe = new();
        ProtocolId id = builder.RegisterProtocol(probe);
        probe.RegisterFields(builder, id);
        return (builder.Build(), probe, id);
    }

    private static Packet _ProbeParse(Stack stack, ProtocolId protocolId, int id, FilterObserver observer, FieldTreeMode tree)
    {
        Frame frame = Frame.Create(
            new FrameId(id),
            Timestamp.FromNanos(id),
            new byte[8],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseObserver[] observers = [new() { Observer = observer, Watch = observer.Watch }];
        ParseOptions options = new(tree, observers: observers, firstProtocol: protocolId);
        bool parsed = Packet.TryParse(new PacketId(id), stack, frame, in options, out Packet? packet, out ParseFailure failure);
        if (!parsed || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    #endregion

    #region Compile

    [Test]
    public async Task Compile_ReturnsAnObserverAndNotAFilter()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterResult<FilterObserver> observer = Filter.Compile(
            "udp.srcport == 53",
            stack,
            in _Options);
        FilterResult<Filter> filter = Filter.Compile("udp.srcport == 53", stack);

        await Assert.That(observer.TryGetValue(out FilterObserver? compiled)).IsTrue();
        await Assert.That(compiled).IsNotNull();
        await Assert.That(filter.TryGetValue(out Filter? tree)).IsTrue();
        await Assert.That(tree).IsNotNull();
        await Assert.That(compiled!.GetType().Name).IsEqualTo("FilterObserver");
    }

    [Test]
    public async Task EmptyExpression_IsAlwaysMatchAndWatchesNothing()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterResult<FilterObserver> compiled = Filter.Compile("  ", stack, in _Options);
        FilterResult<FilterObserver> noStack = Filter.Compile("", null, in _Options);
        FilterResult<FilterObserver> missing = Filter.Compile("udp", null, in _Options);

        await Assert.That(compiled.TryGetValue(out FilterObserver? observer)).IsTrue();
        await Assert.That(observer).IsSameReferenceAs(FilterObserver.AlwaysMatch);
        await Assert.That(observer!.IsMatch).IsTrue();
        await Assert.That(observer.Watch.IsNone).IsTrue();
        observer.BeginPacket(new PacketId(0), Timestamp.FromSecs(0));
        await Assert.That(noStack.TryGetValue(out FilterObserver? also)).IsTrue();
        await Assert.That(also).IsSameReferenceAs(FilterObserver.AlwaysMatch);
        await Assert.That(missing.TryGetValue(out _)).IsFalse();
        await Assert.That(missing.Error.Kind).IsEqualTo(FilterErrorKind.StackRequired);
    }

    [Test]
    public async Task Reset_DoesNotCrossBetweenTheTwoResults()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;
        Filter filter = Filter.Compile("udp.srcport == 53", stack).Value;
        Packet packet = _Observe(stack, FilterTestHelper.BuildUdpFrame(53, 80), 0, observer);

        await Assert.That(observer.IsMatch).IsTrue();
        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(filter.EvaluatedCount).IsEqualTo(0L);
        observer.ResetState();
        await Assert.That(filter.EvaluatedCount).IsEqualTo(0L);
    }

    #endregion

    #region Latch

    [Test]
    public async Task PortEquality_AgreesWithTheFieldTreeFilter()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;
        Filter filter = Filter.Compile("udp.srcport == 53", stack).Value;
        byte[] yes = FilterTestHelper.BuildUdpFrame(53, 80);
        byte[] no = FilterTestHelper.BuildUdpFrame(54, 80);

        _ = _Observe(stack, yes, 0, observer);
        bool treeYes = filter.TryIsMatch(_Build(stack, yes, 1), out bool matchedYes, out FilterError? errorYes);
        bool observerYes = observer.IsMatch;
        _ = _Observe(stack, no, 2, observer);
        bool treeNo = filter.TryIsMatch(_Build(stack, no, 3), out bool matchedNo, out FilterError? errorNo);

        await Assert.That(treeYes).IsTrue();
        await Assert.That(errorYes).IsNull();
        await Assert.That(matchedYes).IsTrue();
        await Assert.That(observerYes).IsTrue();
        await Assert.That(treeNo).IsTrue();
        await Assert.That(errorNo).IsNull();
        await Assert.That(matchedNo).IsFalse();
        await Assert.That(observer.IsMatch).IsFalse();
    }

    [Test]
    public async Task LaterOccurrence_SavesAFailedFirstOne()
    {
        (Stack stack, ObserverProbe probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            FilterObserver observer = Filter.Compile("probe.a == 53", stack, in _Options).Value;
            probe.Values = [1, 53];
            _ = _ProbeParse(stack, protocolId, 0, observer, FieldTreeMode.Skip);
            bool first = observer.IsMatch;
            probe.Values = [1];
            _ = _ProbeParse(stack, protocolId, 1, observer, FieldTreeMode.Skip);

            await Assert.That(first).IsTrue();
            await Assert.That(observer.IsMatch).IsFalse();
        }
    }

    [Test]
    public async Task NextFrame_DoesNotInheritThePreviousMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;
        _ = _Observe(stack, FilterTestHelper.BuildUdpFrame(53, 80), 0, observer);
        bool first = observer.IsMatch;
        _ = _Observe(stack, FilterTestHelper.BuildUdpFrame(54, 80), 1, observer);

        await Assert.That(first).IsTrue();
        await Assert.That(observer.IsMatch).IsFalse();
    }

    [Test]
    public async Task AndAndOr_FollowTheBits()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver and = Filter.Compile(
            "udp.srcport == 53 && udp.dstport == 80",
            stack,
            in _Options).Value;
        FilterObserver or = Filter.Compile(
            "udp.srcport == 53 || udp.dstport == 80",
            stack,
            in _Options).Value;
        byte[] onlySource = FilterTestHelper.BuildUdpFrame(53, 1);
        byte[] both = FilterTestHelper.BuildUdpFrame(53, 80);

        _ = _Observe(stack, onlySource, 0, and);
        bool andMiss = and.IsMatch;
        _ = _Observe(stack, both, 1, and);
        bool andHit = and.IsMatch;
        _ = _Observe(stack, onlySource, 2, or);

        await Assert.That(andMiss).IsFalse();
        await Assert.That(andHit).IsTrue();
        await Assert.That(or.IsMatch).IsTrue();
    }

    [Test]
    public async Task Presence_FollowsTheContainer()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver udp = Filter.Compile("udp", stack, in _Options).Value;
        FilterObserver tcp = Filter.Compile("tcp", stack, in _Options).Value;
        byte[] frame = FilterTestHelper.BuildUdpFrame(53, 80);

        _ = _Observe(stack, frame, 0, udp);
        bool udpMatch = udp.IsMatch;
        _ = _Observe(stack, frame, 1, tcp);

        await Assert.That(udpMatch).IsTrue();
        await Assert.That(tcp.IsMatch).IsFalse();
    }

    #endregion

    #region Text flank and scope

    [Test]
    public async Task Scope_IsRefusedAndStillMatchesOnATree()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterResult<FilterObserver> observer = Filter.Compile(
            "$udp { udp.srcport == 53 }",
            stack,
            in _Options);
        Filter filter = Filter.Compile("$udp { udp.srcport == 53 }", stack).Value;
        Packet built = _Build(stack, FilterTestHelper.BuildUdpFrame(53, 80), 0);
        bool tree = filter.TryIsMatch(built, out bool matched, out FilterError? error);

        await Assert.That(observer.TryGetValue(out _)).IsFalse();
        await Assert.That(observer.Error.Kind).IsEqualTo(FilterErrorKind.NeedsFieldTree);
        await Assert.That(tree).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(matched).IsTrue();
    }

    [Test]
    public async Task SkipPacket_IsNotASilentNonMatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = Filter.Compile("udp.srcport == 53", stack).Value;
        Filter empty = Filter.Compile("").Value;
        Packet skip = _Observe(
            stack,
            FilterTestHelper.BuildUdpFrame(53, 80),
            0,
            FilterObserver.AlwaysMatch);

        bool ran = filter.TryIsMatch(skip, out bool matched, out FilterError? error);
        bool emptyRan = empty.TryIsMatch(skip, out bool emptyMatched, out FilterError? emptyError);

        await Assert.That(ran).IsFalse();
        await Assert.That(error!.Kind).IsEqualTo(FilterErrorKind.NoFieldTree);
        await Assert.That(matched).IsFalse();
        await Assert.That(emptyRan).IsTrue();
        await Assert.That(emptyError).IsNull();
        await Assert.That(emptyMatched).IsTrue();
    }

    [Test]
    public async Task ContainsAndSlice_AgreeWithTheFieldTreeFilter()
    {
        (Stack stack, ObserverProbe probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            probe.Text = "hello.example";
            probe.Bytes = [9, 8, 7];
            FilterObserver contains = Filter.Compile(
                "probe.host contains \"example\"",
                stack,
                in _Options).Value;
            Filter treeContains = Filter.Compile("probe.host contains \"example\"", stack).Value;
            _ = _ProbeParse(stack, protocolId, 0, contains, FieldTreeMode.Skip);
            bool observerContains = contains.IsMatch;
            Packet built = _ProbeParse(stack, protocolId, 0, FilterObserver.AlwaysMatch, FieldTreeMode.Build);
            bool tree = treeContains.TryIsMatch(built, out bool treeMatched, out _);

            FilterObserver slice = Filter.Compile(
                "probe.bytes[0:2] == 09:08",
                stack,
                in _Options).Value;
            Filter treeSlice = Filter.Compile("probe.bytes[0:2] == 09:08", stack).Value;
            _ = _ProbeParse(stack, protocolId, 1, slice, FieldTreeMode.Skip);
            bool observerSlice = slice.IsMatch;
            Packet builtAgain = _ProbeParse(stack, protocolId, 1, FilterObserver.AlwaysMatch, FieldTreeMode.Build);
            bool treeSliceRan = treeSlice.TryIsMatch(builtAgain, out bool treeSliceMatched, out _);

            await Assert.That(observerContains).IsTrue();
            await Assert.That(tree).IsTrue();
            await Assert.That(treeMatched).IsTrue();
            await Assert.That(observerSlice).IsTrue();
            await Assert.That(treeSliceRan).IsTrue();
            await Assert.That(treeSliceMatched).IsTrue();
        }
    }

    [Test]
    public async Task RegexTimeout_StaysOnTheObserver()
    {
        (Stack stack, ObserverProbe probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            probe.Text = new string('a', 28) + "!";
            FilterObserverOptions options = new()
            {
                Compile = new FilterCompileOptions { RegexTimeout = TimeSpan.FromMilliseconds(1) },
            };
            FilterObserver observer = Filter.Compile("probe.host matches \"^(a+)+$\"", stack, in options).Value;
            Filter filter = Filter.Compile("probe.host matches \"a\"", stack).Value;
            bool parsed = true;
            ParseFailure failure = ParseFailure.None;
            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                new byte[4],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            ParseObserver[] observers = [new() { Observer = observer, Watch = observer.Watch }];
            ParseOptions parse = new(FieldTreeMode.Skip, observers: observers, firstProtocol: protocolId);
            parsed = Packet.TryParse(new PacketId(0), stack, frame, in parse, out _, out failure);
            bool read = observer.TryReadMatch(out bool matched, out FilterError? error);
            probe.Text = "a";
            Frame second = Frame.Create(
                new FrameId(1),
                Timestamp.FromSecs(1),
                new byte[4],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            bool again = Packet.TryParse(new PacketId(1), stack, second, in parse, out _, out _);
            bool secondRead = observer.TryReadMatch(out bool secondMatched, out FilterError? secondError);

            await Assert.That(parsed).IsTrue();
            await Assert.That(failure).IsEqualTo(ParseFailure.None);
            await Assert.That(read).IsFalse();
            await Assert.That(matched).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(filter.IsPoisoned).IsFalse();
            await Assert.That(again).IsTrue();
            await Assert.That(secondRead).IsTrue();
            await Assert.That(secondError).IsNull();
            await Assert.That(secondMatched).IsTrue();
        }
    }

    [Test]
    public async Task Observer_TwoFlanks_ReportsSyntaxError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterResult<FilterObserver> result = Filter.Compile(
            "flank(ip.ttl, changed, within: 1s) && flank(udp.srcport, changed, within: 1s)",
            stack,
            in _Options);

        await Assert.That(result.TryGetValue(out _)).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
        await Assert.That(result.Error.Message).Contains("at most one flank");
    }

    [Test]
    public async Task Observer_ByOnStringField_ReportsTypeMismatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterResult<FilterObserver> result = Filter.Compile(
            "flank(dns.qry.name, by: 1, within: 1s)",
            stack,
            in _Options);

        await Assert.That(result.TryGetValue(out _)).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.TypeMismatch);
    }

    [Test]
    public async Task Observer_ProtocolWithoutContainer_ReportsNeedsFieldTree()
    {
        using Stack stack = FilterTestHelper.BuildStackWithContainerlessProtocol();
        FilterResult<FilterObserver> result = Filter.Compile("noctr", stack, in _Options);

        await Assert.That(result.TryGetValue(out _)).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.NeedsFieldTree);
    }

    [Test]
    public async Task Flank_IsMatch_Throws()
    {
        (Stack stack, ObserverProbe probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            FilterObserver observer = Filter.Compile(
                "flank(probe.a, changed, within: 1s)",
                stack,
                in _Options).Value;
            probe.Values = [1];
            _ = _ProbeParse(stack, protocolId, 0, observer, FieldTreeMode.Skip);

            await Assert.That(observer.HasFlank).IsTrue();
            await Assert.That(() => _ = observer.IsMatch).Throws<InvalidOperationException>();
            bool read = observer.TryReadMatch(out bool matched, out FilterError? error);
            await Assert.That(read).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(matched).IsFalse();
        }
    }

    [Test]
    public async Task Flank_AgreesAndSurvivesBeginPacket()
    {
        (Stack left, ObserverProbe leftProbe, ProtocolId leftId) = _Probe();
        (Stack right, ObserverProbe rightProbe, ProtocolId rightId) = _Probe();
        using (left)
        using (right)
        {
            const string expression = "flank(probe.a, from: 1, to: 2, within: 10packets)";
            FilterObserver observer = Filter.Compile(expression, left, in _Options).Value;
            Filter filter = Filter.Compile(expression, right).Value;
            leftProbe.Values = [1];
            rightProbe.Values = [1];
            _ = _ProbeParse(left, leftId, 0, observer, FieldTreeMode.Skip);
            Packet firstTree = _ProbeParse(right, rightId, 0, FilterObserver.AlwaysMatch, FieldTreeMode.Build);
            bool firstTreeRan = filter.TryIsMatch(firstTree, out bool firstTreeMatched, out _);
            bool firstRead = observer.TryReadMatch(out bool firstMatched, out _);

            leftProbe.Values = [2];
            rightProbe.Values = [2];
            _ = _ProbeParse(left, leftId, 1, observer, FieldTreeMode.Skip);
            Packet secondTree = _ProbeParse(right, rightId, 1, FilterObserver.AlwaysMatch, FieldTreeMode.Build);
            bool secondTreeRan = filter.TryIsMatch(secondTree, out bool secondTreeMatched, out _);
            bool secondRead = observer.TryReadMatch(out bool secondMatched, out _);

            await Assert.That(observer.HasFlank).IsTrue();
            await Assert.That(firstTreeRan).IsTrue();
            await Assert.That(firstTreeMatched).IsFalse();
            await Assert.That(firstRead).IsTrue();
            await Assert.That(firstMatched).IsFalse();
            await Assert.That(secondTreeRan).IsTrue();
            await Assert.That(secondTreeMatched).IsTrue();
            await Assert.That(secondRead).IsTrue();
            await Assert.That(secondMatched).IsTrue();

            Filter other = Filter.Compile(expression, left).Value;
            other.ResetState();
            leftProbe.Values = [1];
            _ = _ProbeParse(left, leftId, 2, observer, FieldTreeMode.Skip);
            leftProbe.Values = [2];
            _ = _ProbeParse(left, leftId, 3, observer, FieldTreeMode.Skip);
            bool afterOtherReset = observer.TryReadMatch(out bool afterMatched, out _);
            await Assert.That(afterOtherReset).IsTrue();
            await Assert.That(afterMatched).IsTrue();

            observer.ResetState();
            leftProbe.Values = [2];
            _ = _ProbeParse(left, leftId, 4, observer, FieldTreeMode.Skip);
            bool cleared = observer.TryReadMatch(out bool clearedMatched, out _);
            await Assert.That(cleared).IsTrue();
            await Assert.That(clearedMatched).IsFalse();
        }
    }

    [Test]
    public async Task WhenGate_DoesNotConsumeTheSample()
    {
        (Stack stack, ObserverProbe probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            const string expression = "flank(probe.a, from: 1, to: 2, within: 10packets, when: probe.b == 1)";
            FilterObserver observer = Filter.Compile(expression, stack, in _Options).Value;
            Filter filter = Filter.Compile(expression, stack).Value;
            probe.Values = [1];
            probe.Gate = 1;
            _ = _ProbeParse(stack, protocolId, 0, observer, FieldTreeMode.Skip);
            Packet tree0 = _BuildProbe(stack, protocolId, 0);
            filter.TryIsMatch(tree0, out bool tree0Matched, out _);

            probe.Values = [2];
            probe.Gate = 0;
            _ = _ProbeParse(stack, protocolId, 1, observer, FieldTreeMode.Skip);
            bool middle = observer.TryReadMatch(out bool middleMatched, out _);
            Packet tree1 = _BuildProbe(stack, protocolId, 1);
            filter.TryIsMatch(tree1, out bool tree1Matched, out _);

            probe.Values = [2];
            probe.Gate = 1;
            _ = _ProbeParse(stack, protocolId, 2, observer, FieldTreeMode.Skip);
            bool last = observer.TryReadMatch(out bool lastMatched, out _);
            Packet tree2 = _BuildProbe(stack, protocolId, 2);
            filter.TryIsMatch(tree2, out bool tree2Matched, out _);

            await Assert.That(tree0Matched).IsFalse();
            await Assert.That(middle).IsTrue();
            await Assert.That(middleMatched).IsFalse();
            await Assert.That(tree1Matched).IsFalse();
            await Assert.That(last).IsTrue();
            await Assert.That(lastMatched).IsTrue();
            await Assert.That(tree2Matched).IsTrue();
        }
    }

    [Test]
    public async Task Converter_KeepsOnlyTheMatchingFrame()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;
        byte[][] frames = [FilterTestHelper.BuildUdpFrame(53, 80), FilterTestHelper.BuildUdpFrame(54, 80)];
        List<byte[]> kept = [];
        ParseObserver[] observers = [new() { Observer = observer, Watch = observer.Watch }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
        Packet? recycle = null;
        for (int i = 0; i < frames.Length; i++)
        {
            Frame frame = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i),
                frames[i],
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;
            bool parsed = recycle is null
                ? Packet.TryParse(new PacketId(i), stack, frame, in options, out recycle, out _)
                : Packet.TryParse(recycle, new PacketId(i), stack, frame, in options, out _);
            if (!parsed)
            {
                continue;
            }

            if (observer.IsMatch)
            {
                kept.Add(frames[i]);
            }

            await Assert.That(recycle!.HasFieldTree).IsFalse();
        }

        await Assert.That(kept.Count).IsEqualTo(1);
        await Assert.That(kept[0]).IsSameReferenceAs(frames[0]);
    }

    private static Packet _BuildProbe(Stack stack, ProtocolId protocolId, int id)
    {
        Frame frame = Frame.Create(
            new FrameId(id),
            Timestamp.FromNanos(id),
            new byte[8],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(FieldTreeMode.Build, firstProtocol: protocolId);
        bool parsed = Packet.TryParse(new PacketId(id), stack, frame, in options, out Packet? packet, out _);
        if (!parsed || packet is null)
        {
            throw new InvalidOperationException("Build parse failed.");
        }

        return packet;
    }

    #endregion
}

/// <summary>Appends caller-chosen values for observer tests.</summary>
internal sealed class ObserverProbe : IProtocol
{
    private int _Resets;

    /// <summary>Numeric field.</summary>
    public FieldId A;

    /// <summary>Gate field.</summary>
    public FieldId B;

    /// <summary>String field.</summary>
    public FieldId Host;

    /// <summary>Byte field.</summary>
    public FieldId BytesId;

    /// <summary>Values appended for <see cref="A"/>, in order.</summary>
    public ulong[] Values { get; set; } = [1];

    /// <summary>Value appended for <see cref="B"/>.</summary>
    public ulong Gate { get; set; } = 1;

    /// <summary>String appended for <see cref="Host"/>.</summary>
    public string Text { get; set; } = "hello.example";

    /// <summary>Bytes appended for <see cref="BytesId"/>.</summary>
    public byte[] Bytes { get; set; } = [9, 8, 7];

    /// <inheritdoc />
    public string Name => "probe";

    /// <inheritdoc />
    public string UiName => "Probe";

    /// <inheritdoc />
    public void ResetParseState()
    {
        _Resets++;
    }

    /// <inheritdoc />
    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        A = builder.RegisterField(protocolId, "probe.a", "A", FieldType.U64);
        B = builder.RegisterField(protocolId, "probe.b", "B", FieldType.U64);
        Host = builder.RegisterField(protocolId, "probe.host", "Host", FieldType.String);
        BytesId = builder.RegisterField(protocolId, "probe.bytes", "Bytes", FieldType.Bytes);
    }

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        for (int i = 0; i < Values.Length; i++)
        {
            parentField.Append(A, FieldValue.NewU64(Values[i]));
        }

        parentField.Append(B, FieldValue.NewU64(Gate));
        parentField.Append(Host, FieldValue.NewString(Text));
        parentField.Append(BytesId, FieldValue.NewBytes(Bytes));
        return data.Length;
    }
}
