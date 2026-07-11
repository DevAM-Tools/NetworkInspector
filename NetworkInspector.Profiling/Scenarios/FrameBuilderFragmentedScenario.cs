// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-2: IP-layer fragmentation throughput.
///
/// <para>
/// <b>Hot path:</b> repeated <c>Build(payload)</c> on a cached
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>
/// (Eth + IPv4(DF=false) + UDP + 8 KiB payload).  Every build emits six
/// fragments — exercises the new
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/> fragment
/// loop (ThreadStatic scratch + per-fragment header cache) and the
/// outer→inner walker (<c>PatchFragmentable</c> + cut-off
/// <c>ApplyPostFixUpTo</c>).
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class FrameBuilderFragmentedScenario : FrameBuilderScenarioBase
{
    /// <summary>Number of full datagrams to build per timed run.</summary>
    private const int _DatagramCount = 250_000;

    /// <summary>Frame buffer size (link MTU).</summary>
    private const int _FrameBufferSize = 1500;

    /// <inheritdoc/>
    protected override int PayloadSize => 8000;

    private CreatedStack<
        StatelessStack<UdpLayer,
            StatelessStack<IPv4Layer,
                StatelessStack<EthernetLayer, StackEnd>>>,
        NoTrailer,
        NoInterceptor> _Stack;

    /// <inheritdoc/>
    public override string Name => "framebuilder-fragmented";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Build {_DatagramCount:N0} Eth/IPv4/UDP datagrams with a {PayloadSize}-byte payload, emitting six IP fragments per datagram via the new FrameStack fragmentation API.");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => _DatagramCount;

    /// <inheritdoc/>
    public override string WorkUnitName => "datagrams";

    /// <inheritdoc/>
    public override void Setup()
    {
        InitializeBuffers();

        // Cache layer values + frame buffer once; the stack is reused across
        // all builds (R12).  DF cleared so the IPv4 layer can fragment.
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]),
            maxFrameSize: _FrameBufferSize);
        IPv4Layer ip = new(
            new IPv4Address(0x0A000001),
            new IPv4Address(0x0A000002),
            dontFragment: false);
        UdpLayer udp = new(srcPort: 12345, dstPort: 53);

        _Stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();
        _Buffer = new byte[_FrameBufferSize];
    }

    /// <inheritdoc/>
    public override void Run()
    {
        Span<byte> dst = _Buffer;
        for (int i = 0; i < _DatagramCount; i++)
        {
            // One full datagram = N fragment frames.  Drain the iterator.
            FrameSequence<StatelessStack<UdpLayer, StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> seq
                = _Stack.Build(_Payload);
            while (seq.MoveNext(dst, out _))
            {
                // Per-fragment work happens inside MoveNext; nothing to do here.
            }
        }
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
    }
}
