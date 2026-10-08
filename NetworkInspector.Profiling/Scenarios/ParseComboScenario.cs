// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>One recycled batch that varies the field tree, the value cache, and the watch.</summary>
internal enum ParseComboKind
{
    /// <summary>Build, no cache, no watch.</summary>
    Build,

    /// <summary>Skip, no cache, no watch.</summary>
    Skip,

    /// <summary>Build plus one cached port.</summary>
    BuildCachePort,

    /// <summary>Skip plus one cached port.</summary>
    SkipCachePort,

    /// <summary>Build plus a cache of every field.</summary>
    BuildCacheAll,

    /// <summary>Skip plus a cache of every field.</summary>
    SkipCacheAll,

    /// <summary>Skip watching a field the UDP frame does not produce.</summary>
    SkipMiss,

    /// <summary>Skip watching <c>udp.srcport</c>.</summary>
    SkipPort,

    /// <summary>Skip watching both UDP ports.</summary>
    SkipPorts,

    /// <summary>Skip watching every field.</summary>
    SkipAll,

    /// <summary>Build watching <c>udp.srcport</c>.</summary>
    BuildPort,

    /// <summary>Build watching every field.</summary>
    BuildAll,

    /// <summary>Skip watching <c>tcp.seq</c> on a TCP SYN.</summary>
    TcpSkipSeq,

    /// <summary>Skip watching <c>tcp.seq_raw</c>, which does not expand the TCP group.</summary>
    TcpSkipSeqRaw,

    /// <summary>Build plus the field-tree filter.</summary>
    FilterBuild,

    /// <summary>Skip plus the tree-free filter observer.</summary>
    FilterObserver,
}

/// <summary>
/// Recycles one packet across 10_000 frames. Options, watches, and filters are built in
/// <see cref="Setup"/> so <see cref="Run"/> does not allocate them.
/// </summary>
internal sealed class ParseComboScenario : IProfilingScenario
{
    private const int _BatchSize = 10_000;

    private readonly ParseComboKind _Kind;
    private Stack? _Stack;
    private Frame[]? _Frames;
    private Packet? _Recycle;
    private int _Counter;
    private ParseOptions _Options;
    private PacketFilter? _Filter;
    private FilterObserver? _Observer;
    private int _Sink;

    /// <summary>Creates one row of the combination family.</summary>
    internal ParseComboScenario(ParseComboKind kind)
    {
        _Kind = kind;
    }

    /// <inheritdoc />
    public string Name => _Kind switch
    {
        ParseComboKind.Build => "parse-combo-build",
        ParseComboKind.Skip => "parse-combo-skip",
        ParseComboKind.BuildCachePort => "parse-combo-build-cache-port",
        ParseComboKind.SkipCachePort => "parse-combo-skip-cache-port",
        ParseComboKind.BuildCacheAll => "parse-combo-build-cache-all",
        ParseComboKind.SkipCacheAll => "parse-combo-skip-cache-all",
        ParseComboKind.SkipMiss => "parse-combo-skip-miss",
        ParseComboKind.SkipPort => "parse-combo-skip-port",
        ParseComboKind.SkipPorts => "parse-combo-skip-ports",
        ParseComboKind.SkipAll => "parse-combo-skip-all",
        ParseComboKind.BuildPort => "parse-combo-build-port",
        ParseComboKind.BuildAll => "parse-combo-build-all",
        ParseComboKind.TcpSkipSeq => "parse-combo-tcp-skip-seq",
        ParseComboKind.TcpSkipSeqRaw => "parse-combo-tcp-skip-seqraw",
        ParseComboKind.FilterBuild => "parse-combo-filter-build",
        ParseComboKind.FilterObserver => "parse-combo-filter-observer",
        _ => "parse-combo-unknown",
    };

    /// <inheritdoc />
    public string Description => FormattableString.Invariant(
        $"{Name}, {_BatchSize:N0} recycled frames per iteration.");

    /// <inheritdoc />
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc />
    public string WorkUnitName => "packets";

    /// <inheritdoc />
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        bool tcp = _Kind is ParseComboKind.TcpSkipSeq or ParseComboKind.TcpSkipSeqRaw;
        _Frames = tcp ? _TcpFrames(_Stack) : FrameHelper.CreateSharedFrames(_BatchSize, _Stack);
        _Counter = 0;

        FieldId src = _Stack.GetFieldId("udp.srcport")!.Value;
        FieldId dst = _Stack.GetFieldId("udp.dstport")!.Value;
        FieldId tcpSrc = _Stack.GetFieldId("tcp.srcport")!.Value;
        FieldId seq = _Stack.GetFieldId("tcp.seq")!.Value;
        FieldId seqRaw = _Stack.GetFieldId("tcp.seq_raw")!.Value;
        FieldTreeMode tree = _Tree();
        ValueCache? cache = _Cache(src);
        ParseObserver[]? observers = _Observers(src, dst, tcpSrc, seq, seqRaw);
        _Options = new(tree, cache, observers: observers ?? default);

        if (_Kind == ParseComboKind.FilterBuild)
        {
            FilterResult<PacketFilter> compiled = PacketFilter.Compile("udp.srcport == 12345", _Stack);
            if (!compiled.TryGetValue(out PacketFilter? filter))
            {
                throw new InvalidOperationException(compiled.Error.Message);
            }

            _Filter = filter;
        }

        if (_Kind == ParseComboKind.FilterObserver)
        {
            FilterObserverOptions observerOptions = FilterObserverOptions.Default;
            FilterResult<FilterObserver> compiled = PacketFilter.Compile(
                "udp.srcport == 12345",
                _Stack,
                in observerOptions);
            if (!compiled.TryGetValue(out FilterObserver? observer))
            {
                throw new InvalidOperationException(compiled.Error.Message);
            }

            _Observer = observer;
            ParseObserver[] bound = [new() { Observer = observer, Watch = observer.Watch }];
            _Options = new(FieldTreeMode.Skip, observers: bound);
        }

        if (!Packet.TryParse(new PacketId(0), _Stack, _Frames[0], in _Options, out Packet? seeded, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        _Recycle = seeded;
        _Counter = 1;
    }

    /// <inheritdoc />
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _Recycle!;
        int counter = _Counter;
        ParseOptions options = _Options;
        int sink = 0;
        for (int i = 0; i < _BatchSize; i++)
        {
            PacketId id = new(counter + i);
            if (!Packet.TryParse(recycle, id, stack, frames[i], in options, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            if (_Filter is not null)
            {
                _ = _Filter.TryIsMatch(recycle, out bool matched, out _);
                if (matched)
                {
                    sink++;
                }
            }

            if (_Observer is not null && _Observer.IsMatch)
            {
                sink++;
            }
        }

        _Counter = counter + _BatchSize;
        _Sink = sink;
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        _ = _Sink;
        _Stack?.Dispose();
        _Stack = null;
    }

    private FieldTreeMode _Tree()
    {
        return _Kind switch
        {
            ParseComboKind.Build or ParseComboKind.BuildCachePort or ParseComboKind.BuildCacheAll
                or ParseComboKind.BuildPort or ParseComboKind.BuildAll or ParseComboKind.FilterBuild
                => FieldTreeMode.Build,
            _ => FieldTreeMode.Skip,
        };
    }

    private ValueCache? _Cache(FieldId src)
    {
        Stack stack = _Stack!;
        return _Kind switch
        {
            ParseComboKind.BuildCachePort or ParseComboKind.SkipCachePort
                => new ValueCache(stack, [new ValueCacheFieldConfig(src)]),
            ParseComboKind.BuildCacheAll or ParseComboKind.SkipCacheAll
                => new ValueCache(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true }),
            _ => null,
        };
    }

    private ParseObserver[]? _Observers(FieldId src, FieldId dst, FieldId tcpSrc, FieldId seq, FieldId seqRaw)
    {
        Stack stack = _Stack!;
        ComboWatch watch = new();
        FieldWatch fieldWatch = _Kind switch
        {
            ParseComboKind.SkipMiss => FieldWatch.Only(stack, tcpSrc),
            ParseComboKind.SkipPort or ParseComboKind.BuildPort => FieldWatch.Only(stack, src),
            ParseComboKind.SkipPorts => FieldWatch.Only(stack, src, dst),
            ParseComboKind.SkipAll or ParseComboKind.BuildAll => FieldWatch.All,
            ParseComboKind.TcpSkipSeq => FieldWatch.Only(stack, seq),
            ParseComboKind.TcpSkipSeqRaw => FieldWatch.Only(stack, seqRaw),
            _ => FieldWatch.None,
        };
        if (fieldWatch.IsNone)
        {
            return null;
        }

        return [new ParseObserver { Observer = watch, Watch = fieldWatch }];
    }

    private static Frame[] _TcpFrames(Stack stack)
    {
        byte[] syn = _TcpSyn();
        Frame[] frames = new Frame[_BatchSize];
        for (int i = 0; i < frames.Length; i++)
        {
            ParseResult<Frame> created = Frame.Create(
                new FrameId(i),
                Timestamp.FromSecs(i),
                syn,
                LinkType.Ethernet,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry);
            if (!created.TryGetValue(out Frame frame))
            {
                throw new InvalidOperationException(created.Error.Message);
            }

            frames[i] = frame;
        }

        return frames;
    }

    private static byte[] _TcpSyn()
    {
        byte[] frame = new byte[54];
        frame[12] = 0x08;
        frame[13] = 0x00;
        frame[14] = 0x45;
        frame[16] = 0x00;
        frame[17] = 40;
        frame[22] = 64;
        frame[23] = 6;
        frame[34] = 0x00;
        frame[35] = 80;
        frame[47] = 0x50;
        frame[48] = 0x02;
        return frame;
    }
}

/// <summary>Counts reports so a watch call cannot be removed.</summary>
internal sealed class ComboWatch : IFieldObserver
{
    /// <summary>Reports seen in the current process.</summary>
    public int Count;

    /// <inheritdoc />
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
    }

    /// <inheritdoc />
    public void OnField(in FieldVisit visit)
    {
        Count++;
    }
}
