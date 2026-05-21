// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-1: Single-frame build throughput.
///
/// <para>
/// <b>Hot path:</b> repeated <c>Build(payload).MoveNext(...)</c> on a cached
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>
/// (Eth + IPv4 + UDP + 64 byte payload).  Measures the per-frame cost of the
/// new <see cref="FrameStack"/> API on the steady-state path.
/// </para>
/// </summary>
internal sealed class FrameBuilderSingleFrameScenario : IProfilingScenario
{
    private const int FrameCount = 1_000_000;
    private const int PayloadSize = 64;

    private readonly byte[] _Payload = new byte[PayloadSize];
    private byte[] _Buffer = [];
    private CreatedStack<
        StatelessStack<UdpLayer,
            StatelessStack<IPv4Layer,
                StatelessStack<EthernetLayer, StackEnd>>>,
        NoTrailer,
        NoInterceptor> _Stack;

    /// <inheritdoc/>
    public string Name => "framebuilder-single-frame";

    /// <inheritdoc/>
    public string Description =>
        $"Build {FrameCount:N0} Eth/IPv4/UDP frames with a {PayloadSize}-byte payload via the new FrameStack API.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        // Cache layer values + buffer once; the stack is reused across all builds (R12).
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(srcPort: 12345, dstPort: 53);

        _Stack = FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues();
        _Buffer = new byte[_Stack.HeaderSize + PayloadSize];
    }

    /// <inheritdoc/>
    public void Run()
    {
        Span<byte> dst = _Buffer;
        for (int i = 0; i < FrameCount; i++)
        {
            FrameSequence<StatelessStack<UdpLayer, StatelessStack<IPv4Layer, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> seq
                = _Stack.Build(_Payload);
            seq.MoveNext(dst, out _);
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }
}
