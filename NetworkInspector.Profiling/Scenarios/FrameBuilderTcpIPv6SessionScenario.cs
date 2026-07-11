// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-3: Stateful TCP-over-IPv6 conversation throughput.
///
/// <para>
/// <b>Hot path:</b> emit many TCP/IPv6 segments through one
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/> using the
/// stateful <see cref="TcpLayerWithAutoSequence"/>.  Sequence numbers
/// auto-advance per frame; the same session and buffer are reused.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class FrameBuilderTcpIPv6SessionScenario : FrameBuilderScenarioBase
{
    private const int _FrameCount = 500_000;

    /// <inheritdoc/>
    protected override int PayloadSize => 64;
    private StatefulCreatedStack<
        Stack<TcpLayerWithAutoSequence,
            StatelessStack<IPv6Layer,
                StatelessStack<EthernetLayer, StackEnd>>>,
        NoTrailer,
        NoInterceptor> _Stack;

    /// <inheritdoc/>
    public override string Name => "framebuilder-tcp-ipv6-session";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Stream {_FrameCount:N0} TCP/IPv6 segments through a stateful Session with auto-sequence.");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public override string WorkUnitName => "frames";

    /// <inheritdoc/>
    public override void Setup()
    {
        InitializeBuffers();
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
        IPv6Address src = IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
        IPv6Address dst = IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x02]);
        IPv6Layer ip = new(src, dst);
        TcpLayerWithAutoSequence tcp = new(
            srcPort: 12345, dstPort: 80,
            initialSequence: 1000, initialAck: 5000,
            flags: TcpFlags.Ack);

        _Stack = StatefulFrameStack.CreateForSession(
            FrameStack.Start(eth).Then(ip).Then(tcp));
        _Buffer = new byte[_Stack.HeaderSize + PayloadSize];
    }

    /// <inheritdoc/>
    public override void Run()
    {
        Span<byte> dst = _Buffer;
        using Session<Stack<TcpLayerWithAutoSequence, StatelessStack<IPv6Layer, StatelessStack<EthernetLayer, StackEnd>>>, NoTrailer, NoInterceptor> session
            = _Stack.OpenSession();

        for (int i = 0; i < _FrameCount; i++)
        {
            session.NextPacket(_Payload).MoveNext(dst, out _);
        }
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
    }
}
