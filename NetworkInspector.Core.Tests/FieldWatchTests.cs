// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Field watches delivered during <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>.</summary>
internal sealed class FieldWatchTests
{
    #region Helpers

    private static Stack _Standard()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Udp(Stack stack, int id) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static Frame _Tcp(Stack stack) =>
        Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            FrameBuilders.Generate_TcpSynFrame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static Frame _Empty(Stack stack, int id) =>
        Frame.Create(
            new FrameId(id),
            Timestamp.FromSecs(id),
            new byte[16],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static (Stack Stack, ProbeProtocol Probe, ProtocolId Id) _Probe()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProbeProtocol probe = new();
        ProtocolId id = builder.RegisterProtocol(probe);
        probe.RegisterFields(builder, id);
        return (builder.Build(), probe, id);
    }

    private static ParseOptions _Watch(FieldTreeMode tree, FieldWatch watch, ValueCache? cache = null)
    {
        ParseObserver[] observers = [new() { Observer = null!, Watch = watch }];
        return new(tree, cache, observers: observers);
    }

    private static bool _Parse(
        Stack stack,
        Frame frame,
        PacketId id,
        ParseOptions options,
        out Packet? packet)
    {
        return Packet.TryParse(id, stack, frame, in options, out packet, out _);
    }

    #endregion

    #region Reports

    [Test]
    public async Task All_ReportsProbeFieldsInOrder()
    {
        (Stack stack, ProbeProtocol probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            probe.Second = 2;
            ListWatch watch = new();
            ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.All }];
            ParseOptions options = new(FieldTreeMode.Skip, observers: observers, firstProtocol: protocolId);

            bool parsed = _Parse(stack, _Empty(stack, 0), new PacketId(0), options, out _);

            await Assert.That(parsed).IsTrue();
            await Assert.That(watch.Begins).IsEqualTo(1);
            int indexA = watch.Ids.IndexOf(probe.A);
            int indexB = watch.Ids.IndexOf(probe.B);
            await Assert.That(indexA).IsGreaterThanOrEqualTo(0);
            await Assert.That(indexB).IsEqualTo(indexA + 1);
            await Assert.That(watch.U64[indexA]).IsEqualTo(1UL);
            await Assert.That(watch.U64[indexB]).IsEqualTo(2UL);
        }
    }

    [Test]
    public async Task All_DoesNotReportTheSyntheticRoot()
    {
        (Stack stack, ProbeProtocol probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            ListWatch watch = new();
            ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.All }];
            ParseOptions options = new(FieldTreeMode.Skip, observers: observers, firstProtocol: protocolId);
            bool parsed = _Parse(stack, _Empty(stack, 0), new PacketId(0), options, out Packet? packet);

            await Assert.That(parsed).IsTrue();
            FieldId root = packet!.RootField().FieldId;
            await Assert.That(watch.Ids.Contains(root)).IsFalse();
            await Assert.That(watch.Ids.Contains(probe.A)).IsTrue();
        }
    }

    [Test]
    public async Task Only_ReportsOneUdpPort()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Build, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.Ids.Count).IsEqualTo(1);
        await Assert.That(watch.Ids[0]).IsEqualTo(src);
        await Assert.That(watch.U64[0]).IsEqualTo(12345UL);
    }

    [Test]
    public async Task Only_ReportsNamedFieldsAndUsesTheBitsetPastTheSpan()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        FieldId dst = stack.GetFieldId("udp.dstport")!.Value;
        ListWatch ports = new();
        ParseObserver[] portObservers = [new() { Observer = ports, Watch = FieldWatch.Only(stack, src, dst) }];
        ParseOptions skip = new(FieldTreeMode.Skip, observers: portObservers);
        bool udp = _Parse(stack, _Udp(stack, 0), new PacketId(0), skip, out _);
        await Assert.That(udp).IsTrue();
        await Assert.That(ports.Ids.Count).IsEqualTo(2);
        await Assert.That(ports.Ids).Contains(src);
        await Assert.That(ports.Ids).Contains(dst);

        FieldId tcpSrc = stack.GetFieldId("tcp.srcport")!.Value;
        FieldId seq = stack.GetFieldId("tcp.seq")!.Value;
        FieldId seqRaw = stack.GetFieldId("tcp.seq_raw")!.Value;
        ListWatch tcp = new();
        ParseObserver[] tcpObservers =
        [
            new() { Observer = tcp, Watch = FieldWatch.Only(stack, tcpSrc, seq) },
        ];
        ParseOptions tcpOptions = new(FieldTreeMode.Skip, observers: tcpObservers);
        bool tcpParsed = _Parse(stack, _Tcp(stack), new PacketId(1), tcpOptions, out _);
        await Assert.That(tcpParsed).IsTrue();
        await Assert.That(tcp.Ids.Contains(tcpSrc)).IsTrue();
        await Assert.That(tcp.Ids.Contains(seq)).IsTrue();
        await Assert.That(tcp.Ids.Contains(seqRaw)).IsFalse();

        ListWatch produced = new();
        ParseObserver[] producedObservers = [new() { Observer = produced, Watch = FieldWatch.All }];
        ParseOptions producedOptions = new(FieldTreeMode.Skip, observers: producedObservers);
        bool producedParsed = _Parse(stack, _Udp(stack, 2), new PacketId(2), producedOptions, out _);
        await Assert.That(producedParsed).IsTrue();
        FieldId[] many = new FieldId[FieldWatch.SpanLimit + 1];
        many[0] = src;
        int filled = 1;
        for (int i = 0; i < stack.FieldCount && filled < many.Length; i++)
        {
            FieldId candidate = new(i);
            if (!produced.Ids.Contains(candidate))
            {
                many[filled] = candidate;
                filled++;
            }
        }

        ListWatch bitset = new();
        ParseObserver[] bitObservers = [new() { Observer = bitset, Watch = FieldWatch.Only(stack, many) }];
        ParseOptions bitOptions = new(FieldTreeMode.Skip, observers: bitObservers);
        await Assert.That(filled).IsEqualTo(many.Length);
        bool bitParsed = _Parse(stack, _Udp(stack, 3), new PacketId(3), bitOptions, out _);
        await Assert.That(bitParsed).IsTrue();
        await Assert.That(bitset.Ids.Count).IsEqualTo(1);
        await Assert.That(bitset.Ids[0]).IsEqualTo(src);
    }

    [Test]
    public async Task TwoObservers_SeeOnlyTheirOwnField()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        FieldId dst = stack.GetFieldId("udp.dstport")!.Value;
        ListWatch first = new();
        ListWatch second = new();
        ParseObserver[] observers =
        [
            new() { Observer = first, Watch = FieldWatch.Only(stack, src) },
            new() { Observer = second, Watch = FieldWatch.Only(stack, dst) },
        ];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(first.Ids.Count).IsEqualTo(1);
        await Assert.That(first.Ids[0]).IsEqualTo(src);
        await Assert.That(second.Ids.Count).IsEqualTo(1);
        await Assert.That(second.Ids[0]).IsEqualTo(dst);
    }

    [Test]
    public async Task Miss_DoesNotInvokeOnField()
    {
        using Stack stack = _Standard();
        FieldId tcpSrc = stack.GetFieldId("tcp.srcport")!.Value;
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, tcpSrc) }];
        ParseOptions skip = new(FieldTreeMode.Skip, observers: observers);
        ParseOptions build = new(FieldTreeMode.Build, observers: observers);

        bool skipParsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), skip, out Packet? skipPacket);
        bool buildParsed = _Parse(stack, _Udp(stack, 1), new PacketId(1), build, out Packet? buildPacket);

        await Assert.That(skipParsed).IsTrue();
        await Assert.That(buildParsed).IsTrue();
        await Assert.That(watch.Ids.Count).IsEqualTo(0);
        await Assert.That(watch.Begins).IsEqualTo(2);
        await Assert.That(skipPacket!.HasFieldTree).IsFalse();
        await Assert.That(buildPacket!.HasFieldTree).IsTrue();
    }

    [Test]
    public async Task AbsentWatch_IsNotCalled()
    {
        using Stack stack = _Standard();
        FieldId port = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(port)]);
        int calls = CountingWatch.Calls;
        ParseOptions withCache = new(FieldTreeMode.Build, cache);
        ParseOptions bare = new(FieldTreeMode.Skip);

        bool first = _Parse(stack, _Udp(stack, 0), new PacketId(0), withCache, out Packet? built);
        bool second = _Parse(stack, _Udp(stack, 1), new PacketId(1), bare, out Packet? skipped);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(CountingWatch.Calls).IsEqualTo(calls);
        await Assert.That(built!.HasFieldTree).IsTrue();
        await Assert.That(cache.GetSeries<ulong>(port).Count).IsEqualTo(1);
        await Assert.That(skipped!.HasFieldTree).IsFalse();
    }

    [Test]
    public async Task TwoParses_DoNotShareAWatch()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ListWatch first = new();
        ListWatch second = new();
        ParseObserver[] a = [new() { Observer = first, Watch = FieldWatch.Only(stack, src) }];
        ParseObserver[] b = [new() { Observer = second, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions firstOptions = new(FieldTreeMode.Skip, observers: a);
        ParseOptions secondOptions = new(FieldTreeMode.Skip, observers: b);

        bool parsed0 = _Parse(stack, _Udp(stack, 0), new PacketId(0), firstOptions, out _);
        int afterFirst = first.Ids.Count;
        bool parsed1 = _Parse(stack, _Udp(stack, 1), new PacketId(1), secondOptions, out _);

        await Assert.That(parsed0).IsTrue();
        await Assert.That(parsed1).IsTrue();
        await Assert.That(first.Ids.Count).IsEqualTo(afterFirst);
        await Assert.That(second.Ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BeginPacket_RunsBeforeEachFramesFields()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        OrderWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);
        Packet? packet = null;
        bool first = Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in options, out packet, out _);
        bool second = Packet.TryParse(packet!, new PacketId(1), stack, _Udp(stack, 1), in options, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(watch.Events.Count).IsEqualTo(4);
        await Assert.That(watch.Events[0]).IsEqualTo("begin");
        await Assert.That(watch.Events[1]).IsEqualTo("field");
        await Assert.That(watch.Events[2]).IsEqualTo("begin");
        await Assert.That(watch.Events[3]).IsEqualTo("field");
    }

    [Test]
    public async Task Build_CanSeeParentButNotTheLaterSibling()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        FieldId udp = stack.GetFieldId("udp")!.Value;
        ParentWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Build, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.HasField).IsTrue();
        await Assert.That(watch.ParentId).IsEqualTo(udp);
        await Assert.That(watch.HasNext).IsFalse();
    }

    [Test]
    public async Task Skip_DeliversTheValueWithoutACursor()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ParentWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.Value).IsEqualTo(12345UL);
        await Assert.That(watch.HasField).IsFalse();
        await Assert.That(watch.FieldValid).IsFalse();
    }

    [Test]
    public async Task Skip_KeepsNoTreeWithOrWithoutAWatch()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(src)]);
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions watched = new(FieldTreeMode.Skip, cache, observers: observers);
        ParseOptions bare = new(FieldTreeMode.Skip);

        bool with = _Parse(stack, _Udp(stack, 0), new PacketId(0), watched, out Packet? watchedPacket);
        bool without = _Parse(stack, _Udp(stack, 1), new PacketId(1), bare, out Packet? barePacket);

        await Assert.That(with).IsTrue();
        await Assert.That(without).IsTrue();
        await Assert.That(watchedPacket!.HasFieldTree).IsFalse();
        await Assert.That(barePacket!.HasFieldTree).IsFalse();
        await Assert.That(watchedPacket.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(barePacket.FieldCount(materialize: false)).IsEqualTo(1);
        await Assert.That(watch.Ids.Count).IsEqualTo(1);
        await Assert.That(cache.GetSeries<ulong>(src).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NoCache_DoesNotRecord()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Build, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.Ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EightCells_KeepTreeCacheAndWatchIndependent()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        FieldTreeMode[] trees = [FieldTreeMode.Build, FieldTreeMode.Skip];
        bool[] caches = [true, false];
        bool[] watches = [true, false];
        int id = 0;
        foreach (FieldTreeMode tree in trees)
        {
            foreach (bool useCache in caches)
            {
                foreach (bool useWatch in watches)
                {
                    ValueCache? cache = useCache ? new ValueCache(stack, [new ValueCacheFieldConfig(src)]) : null;
                    ListWatch watch = new();
                    ReadOnlyMemory<ParseObserver> observers = useWatch
                        ? new ParseObserver[] { new() { Observer = watch, Watch = FieldWatch.Only(stack, src) } }
                        : default;
                    ParseOptions options = new(tree, cache, observers: observers);
                    bool parsed = Packet.TryParse(new PacketId(id), stack, _Udp(stack, id), in options, out Packet? packet, out _);
                    id++;

                    await Assert.That(parsed).IsTrue();
                    await Assert.That(packet!.HasFieldTree).IsEqualTo(tree == FieldTreeMode.Build);
                    int cacheCount = cache is null ? 0 : cache.GetSeries<ulong>(src).Count;
                    await Assert.That(cacheCount).IsEqualTo(useCache ? 1 : 0);
                    await Assert.That(watch.Ids.Count).IsEqualTo(useWatch ? 1 : 0);
                }
            }
        }
    }

    [Test]
    public async Task CacheWidth_IsNotTheWatchWidth()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        FieldId dst = stack.GetFieldId("udp.dstport")!.Value;
        ValueCache one = new(stack, [new ValueCacheFieldConfig(src)]);
        ListWatch all = new();
        ParseObserver[] allObservers = [new() { Observer = all, Watch = FieldWatch.All }];
        ParseOptions wideWatch = new(FieldTreeMode.Skip, one, observers: allObservers);
        bool first = _Parse(stack, _Udp(stack, 0), new PacketId(0), wideWatch, out _);
        await Assert.That(first).IsTrue();
        await Assert.That(all.Ids.Count).IsGreaterThan(1);
        await Assert.That(one.GetSeries<ulong>(src).Count).IsEqualTo(1);
        bool dstSeries = false;
        try
        {
            _ = one.GetSeries<ulong>(dst);
            dstSeries = true;
        }
        catch (ArgumentException)
        {
            dstSeries = false;
        }

        await Assert.That(dstSeries).IsFalse();

        ValueCache every = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        ListWatch oneWatch = new();
        ParseObserver[] oneObservers = [new() { Observer = oneWatch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions wideCache = new(FieldTreeMode.Skip, every, observers: oneObservers);
        bool second = _Parse(stack, _Udp(stack, 1), new PacketId(1), wideCache, out _);
        await Assert.That(second).IsTrue();
        await Assert.That(oneWatch.Ids.Count).IsEqualTo(1);
        await Assert.That(every.GetSeries<ulong>(dst).Count).IsEqualTo(1);
    }

    [Test]
    public async Task WatchAndCache_SeeTheSamePort()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(src)]);
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Skip, cache, observers: observers);

        bool parsed = _Parse(stack, _Udp(stack, 0), new PacketId(0), options, out Packet? packet);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.Ids.Count).IsEqualTo(1);
        await Assert.That(cache.GetSeries<ulong>(src)[0].Value).IsEqualTo(watch.U64[0]);
        await Assert.That(packet!.HasFieldTree).IsFalse();
    }

    [Test]
    public async Task Replay_StillReports()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ParseOptions bare = new(FieldTreeMode.Skip);
        bool first = _Parse(stack, _Udp(stack, 0), new PacketId(0), bare, out _);
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions replay = new(FieldTreeMode.Skip, observers: observers);
        bool second = _Parse(stack, _Udp(stack, 0), new PacketId(0), replay, out _);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(watch.Ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TwoThreads_DoNotShareAWatch()
    {
        using Stack left = _Standard();
        using Stack right = _Standard();
        FieldId leftPort = left.GetFieldId("udp.srcport")!.Value;
        FieldId rightPort = right.GetFieldId("udp.srcport")!.Value;
        ListWatch leftWatch = new();
        ListWatch rightWatch = new();
        Frame leftFrame = _Udp(left, 0);
        Frame rightFrame = _Udp(right, 0);
        ParseObserver[] leftObservers = [new() { Observer = leftWatch, Watch = FieldWatch.Only(left, leftPort) }];
        ParseObserver[] rightObservers = [new() { Observer = rightWatch, Watch = FieldWatch.Only(right, rightPort) }];
        ParseOptions leftOptions = new(FieldTreeMode.Skip, observers: leftObservers);
        ParseOptions rightOptions = new(FieldTreeMode.Skip, observers: rightObservers);

        Task leftTask = Task.Run(() =>
        {
            Packet.TryParse(new PacketId(0), left, leftFrame, in leftOptions, out _, out _);
        });
        Task rightTask = Task.Run(() =>
        {
            Packet.TryParse(new PacketId(0), right, rightFrame, in rightOptions, out _, out _);
        });
        await Task.WhenAll(leftTask, rightTask);

        await Assert.That(leftWatch.U64[0]).IsEqualTo(12345UL);
        await Assert.That(rightWatch.U64[0]).IsEqualTo(12345UL);
        await Assert.That(leftWatch.Ids.Count).IsEqualTo(1);
        await Assert.That(rightWatch.Ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Reentry_ReturnsNotFinalized()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        ReentryWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, src) }];
        ParseOptions options = new(FieldTreeMode.Build, observers: observers);

        bool parsed = Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in options, out Packet? packet, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(watch.Failure).IsEqualTo(ParseFailure.NotFinalized);
        await Assert.That(watch.SawPort).IsTrue();
        await Assert.That(packet!.Id).IsEqualTo(new PacketId(0));
    }

    [Test]
    public async Task Only_RejectsIdsTheStackDidNotAssign()
    {
        using Stack stack = _Standard();
        FieldId src = stack.GetFieldId("udp.srcport")!.Value;
        await Assert.That(() => FieldWatch.Only(stack, FieldId.Invalid)).Throws<ArgumentException>();
        await Assert.That(() => FieldWatch.Only(stack, src, FieldId.Invalid)).Throws<ArgumentException>();
        await Assert.That(() => FieldWatch.Only(stack, ReadOnlySpan<FieldId>.Empty)).Throws<ArgumentException>();
        await Assert.That(() => FieldWatch.Only(stack, new FieldId(stack.FieldCount))).Throws<ArgumentException>();
        await Assert.That(default(FieldWatch).IsNone).IsTrue();
        await Assert.That(default(FieldWatch).IsAll).IsFalse();
        await Assert.That(FieldWatch.All.IsAll).IsTrue();
        await Assert.That(FieldWatch.None.Accepts(src)).IsFalse();
    }

    [Test]
    public async Task NullObserver_ThrowsBeforeParse()
    {
        using Stack stack = _Standard();
        ParseObserver[] observers = [new() { Observer = null, Watch = FieldWatch.None }];
        ParseOptions options = new(observers: observers);

        await Assert.That(() => Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in options, out _, out _))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CustomText_DoesNotReportAgain()
    {
        (Stack stack, ProbeProtocol probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            probe.SetText = true;
            ListWatch watch = new();
            ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.All }];
            ParseOptions options = new(FieldTreeMode.Build, observers: observers, firstProtocol: protocolId);
            bool parsed = _Parse(stack, _Empty(stack, 0), new PacketId(0), options, out _);
            int countA = 0;
            for (int i = 0; i < watch.Ids.Count; i++)
            {
                if (watch.Ids[i] == probe.A)
                {
                    countA++;
                }
            }

            await Assert.That(parsed).IsTrue();
            await Assert.That(countA).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TcpSeq_IsReported_AndSeqRawDoesNotExpandIt()
    {
        using Stack stack = _Standard();
        FieldId seq = stack.GetFieldId("tcp.seq")!.Value;
        FieldId seqRaw = stack.GetFieldId("tcp.seq_raw")!.Value;
        ListWatch skip = new();
        ListWatch build = new();
        ListWatch cached = new();
        ListWatch raw = new();
        ParseOptions skipOptions = new(
            FieldTreeMode.Skip,
            observers: new ParseObserver[] { new() { Observer = skip, Watch = FieldWatch.Only(stack, seq) } });
        ParseOptions buildOptions = new(
            FieldTreeMode.Build,
            observers: new ParseObserver[] { new() { Observer = build, Watch = FieldWatch.Only(stack, seq) } });
        ValueCache allCache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        ParseOptions cacheOptions = new(
            FieldTreeMode.Skip,
            allCache,
            observers: new ParseObserver[] { new() { Observer = cached, Watch = FieldWatch.Only(stack, seq) } });
        ParseOptions rawOptions = new(
            FieldTreeMode.Skip,
            observers: new ParseObserver[] { new() { Observer = raw, Watch = FieldWatch.Only(stack, seqRaw) } });

        bool skipParsed = Packet.TryParse(new PacketId(0), stack, _Tcp(stack), in skipOptions, out _, out _);
        bool buildParsed = Packet.TryParse(new PacketId(1), stack, _Tcp(stack), in buildOptions, out _, out _);
        bool cacheParsed = Packet.TryParse(new PacketId(2), stack, _Tcp(stack), in cacheOptions, out _, out _);
        bool rawParsed = Packet.TryParse(new PacketId(3), stack, _Tcp(stack), in rawOptions, out _, out _);

        await Assert.That(skipParsed).IsTrue();
        await Assert.That(buildParsed).IsTrue();
        await Assert.That(cacheParsed).IsTrue();
        await Assert.That(rawParsed).IsTrue();
        await Assert.That(skip.Ids.Contains(seq)).IsTrue();
        await Assert.That(build.Ids.Contains(seq)).IsTrue();
        await Assert.That(cached.Ids.Contains(seq)).IsTrue();
        await Assert.That(raw.Ids.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WatchThrow_RecordsAnErrorAndSkipsTheNextField()
    {
        (Stack stack, ProbeProtocol probe, ProtocolId protocolId) = _Probe();
        using (stack)
        {
            ThrowingWatch watch = new(probe.A);
            ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.All }];
            ParseOptions options = new(FieldTreeMode.Build, observers: observers, firstProtocol: protocolId);
            bool parsed = Packet.TryParse(new PacketId(0), stack, _Empty(stack, 0), in options, out Packet? packet, out ParseFailure failure);

            await Assert.That(parsed).IsTrue();
            await Assert.That(failure).IsEqualTo(ParseFailure.None);
            await Assert.That(watch.SawA).IsTrue();
            await Assert.That(watch.Ids.Contains(probe.B)).IsFalse();
            bool hasError = packet!.TryGetFieldValue(stack.PacketErrorFieldId, out _, materialize: false);
            await Assert.That(hasError).IsTrue();
            await Assert.That(watch.Ids.Contains(stack.PacketErrorFieldId)).IsFalse();
        }
    }

    /// <summary>Per-thread allocation counter. Not parallel.</summary>
    [Test]
    [NotInParallel]
    public async Task ReusedMissWatch_AllocatesNoMoreThanNoWatch()
    {
        using Stack stack = _Standard();
        FieldId tcpSrc = stack.GetFieldId("tcp.srcport")!.Value;
        ListWatch watch = new();
        ParseObserver[] observers = [new() { Observer = watch, Watch = FieldWatch.Only(stack, tcpSrc) }];
        ParseOptions watched = new(FieldTreeMode.Skip, observers: observers);
        ParseOptions bare = new(FieldTreeMode.Skip);
        bool started = Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in watched, out Packet? packet, out _);
        await Assert.That(started).IsTrue();
        const int warmup = 8;
        for (int i = 0; i < warmup; i++)
        {
            Frame frame = _Udp(stack, i + 1);
            Packet.TryParse(packet!, new PacketId(i + 1), stack, frame, in watched, out _);
            Packet.TryParse(packet!, new PacketId(i + 1), stack, frame, in bare, out _);
        }

        Frame measure = _Udp(stack, warmup + 1);
        long beforeWatch = GC.GetAllocatedBytesForCurrentThread();
        Packet.TryParse(packet!, new PacketId(warmup + 1), stack, measure, in watched, out _);
        long watchBytes = GC.GetAllocatedBytesForCurrentThread() - beforeWatch;
        long beforeBare = GC.GetAllocatedBytesForCurrentThread();
        Packet.TryParse(packet!, new PacketId(warmup + 1), stack, measure, in bare, out _);
        long bareBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBare;

        await Assert.That(watchBytes).IsLessThanOrEqualTo(bareBytes);
    }

    /// <summary>Per-thread allocation counter. Not parallel.</summary>
    [Test]
    [NotInParallel]
    public async Task RecycledBuild_AllocatesNoChunkTable()
    {
        using Stack stack = _Standard();
        ParseOptions build = new(FieldTreeMode.Build);
        ParseOptions skip = new(FieldTreeMode.Skip);
        bool started = Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in build, out Packet? buildPacket, out _);
        bool skipStarted = Packet.TryParse(new PacketId(0), stack, _Udp(stack, 0), in skip, out Packet? skipPacket, out _);
        await Assert.That(started).IsTrue();
        await Assert.That(skipStarted).IsTrue();
        const int warmup = 8;
        for (int i = 0; i < warmup; i++)
        {
            Frame frame = _Udp(stack, i + 1);
            Packet.TryParse(buildPacket!, new PacketId(i + 1), stack, frame, in build, out _);
            Packet.TryParse(skipPacket!, new PacketId(i + 1), stack, frame, in skip, out _);
        }

        object? tableBefore = typeof(Packet).GetField("_ChunkTable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(buildPacket);
        Frame measure = _Udp(stack, warmup + 1);
        long beforeBuild = GC.GetAllocatedBytesForCurrentThread();
        Packet.TryParse(buildPacket!, new PacketId(warmup + 1), stack, measure, in build, out _);
        long buildBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBuild;
        object? tableAfter = typeof(Packet).GetField("_ChunkTable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(buildPacket);
        long beforeSecond = GC.GetAllocatedBytesForCurrentThread();
        Packet.TryParse(buildPacket!, new PacketId(warmup + 2), stack, measure, in build, out _);
        long secondBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSecond;
        long beforeSkip = GC.GetAllocatedBytesForCurrentThread();
        Packet.TryParse(skipPacket!, new PacketId(warmup + 1), stack, measure, in skip, out _);
        long skipBytes = GC.GetAllocatedBytesForCurrentThread() - beforeSkip;

        // The descriptor table object is reused. Protocol display text still allocates on a
        // build parse; that cost is steady and is not a new descriptor table per frame.
        await Assert.That(tableAfter).IsSameReferenceAs(tableBefore);
        await Assert.That(secondBytes).IsEqualTo(buildBytes);
        await Assert.That(skipBytes).IsLessThanOrEqualTo(buildBytes);
    }

    #endregion
}

/// <summary>Appends two U64 fields, optionally with display text on the first.</summary>
internal sealed class ProbeProtocol : IProtocol
{
    private int _Resets;

    /// <summary>First field.</summary>
    public FieldId A;

    /// <summary>Second field.</summary>
    public FieldId B;

    /// <summary>Value written for <see cref="B"/>.</summary>
    public ulong Second = 2;

    /// <summary>When true, the first field also gets display text.</summary>
    public bool SetText;

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
    }

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (SetText)
        {
            parentField.AppendWithCustomText(A, FieldValue.NewU64(1), new LazyString("shown"));
        }
        else
        {
            parentField.Append(A, FieldValue.NewU64(1));
        }

        parentField.Append(B, FieldValue.NewU64(Second));
        return data.Length;
    }
}

/// <summary>Records field ids and U64 values.</summary>
internal sealed class ListWatch : IFieldObserver
{
    /// <summary>Reported identities, in order.</summary>
    public List<FieldId> Ids { get; } = [];

    /// <summary>U64 values parallel to <see cref="Ids"/>, or 0 when the value is not a U64.</summary>
    public List<ulong> U64 { get; } = [];

    /// <summary>How many frames started.</summary>
    public int Begins { get; private set; }

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
        Begins++;
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        Ids.Add(visit.FieldId);
        ulong value = 0;
        if (visit.Value.Data.TryGetAsU64(out ulong number))
        {
            value = number;
        }

        U64.Add(value);
    }
}

/// <summary>Records begin and field as words so order is visible.</summary>
internal sealed class OrderWatch : IFieldObserver
{
    /// <summary>begin or field, in call order.</summary>
    public List<string> Events { get; } = [];

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
        Events.Add("begin");
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        Events.Add("field");
    }
}

/// <summary>Reads the parent and the next sibling during the report.</summary>
internal sealed class ParentWatch : IFieldObserver
{
    /// <summary>Whether the visit had a cursor.</summary>
    public bool HasField { get; private set; }

    /// <summary>Whether <see cref="Field.IsValid"/> was true.</summary>
    public bool FieldValid { get; private set; }

    /// <summary>Parent id when navigation succeeded.</summary>
    public FieldId ParentId { get; private set; }

    /// <summary>Whether a next sibling was already linked.</summary>
    public bool HasNext { get; private set; }

    /// <summary>U64 value.</summary>
    public ulong Value { get; private set; }

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        HasField = visit.HasField;
        FieldValid = visit.Field.IsValid;
        if (visit.Value.Data.TryGetAsU64(out ulong number))
        {
            Value = number;
        }

        if (visit.HasField && visit.Field.TryGetParent(out Field parent))
        {
            ParentId = parent.FieldId;
        }

        if (visit.HasField)
        {
            HasNext = visit.Field.TryGetNext(out _);
        }
    }
}

/// <summary>Tries to recycle the packet being parsed.</summary>
internal sealed class ReentryWatch : IFieldObserver
{
    /// <summary>Failure returned by the nested parse.</summary>
    public ParseFailure Failure { get; private set; }

    /// <summary>Whether the port was visible.</summary>
    public bool SawPort { get; private set; }

    private Packet? _Packet;

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        SawPort = visit.Value.Data.TryGetAsU64(out _);
        if (visit.HasField)
        {
            _Packet = visit.Field.Packet;
            ParseOptions options = new();
            Packet.TryParse(_Packet, _Packet.Id, _Packet.Stack, _Packet.Frame, in options, out ParseFailure failure);
            Failure = failure;
        }
    }
}

/// <summary>Throws when field A is reported.</summary>
internal sealed class ThrowingWatch : IFieldObserver
{
    private readonly FieldId _ThrowId;

    /// <summary>Creates a watch that throws on <paramref name="throwId"/>.</summary>
    public ThrowingWatch(FieldId throwId)
    {
        _ThrowId = throwId;
    }

    /// <summary>Reported ids before the throw.</summary>
    public List<FieldId> Ids { get; } = [];

    /// <summary>Whether the throwing field was seen.</summary>
    public bool SawA { get; private set; }

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        Ids.Add(visit.FieldId);
        if (visit.FieldId == _ThrowId)
        {
            SawA = true;
            throw new InvalidOperationException("watch failed");
        }
    }
}

/// <summary>Static counter that must stay put when no watch is installed.</summary>
internal static class CountingWatch
{
    /// <summary>Incremented only by a watch. Tests assert it does not move.</summary>
    public static int Calls { get; set; }
}
