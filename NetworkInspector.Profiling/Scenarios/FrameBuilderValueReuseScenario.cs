// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-5: Layer-value reuse across many independent stacks (R12).
///
/// <para>
/// <b>Hot path:</b> a single shared Eth/IPv4 prefix is reused as the base of
/// many distinct UDP stacks (different destination ports).  Each iteration
/// builds <see cref="_StackCount"/> frames using the same cached layer values
/// — measures whether layer values can in fact be shared zero-copy across
/// many <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> instances
/// without per-build allocation overhead.
/// </para>
/// </summary>
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class FrameBuilderValueReuseScenario : FrameBuilderScenarioBase
{
    private const int _StackCount = 100;
    private const int _FramesPerStack = 10_000;

    /// <inheritdoc/>
    protected override int PayloadSize => 32;
    private EthernetLayer _Eth;
    private IPv4Layer _Ip;

    /// <inheritdoc/>
    public override string Name => "framebuilder-value-reuse";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Build {_StackCount:N0} × {_FramesPerStack:N0} = {(long)_StackCount * _FramesPerStack:N0} frames reusing the same Eth/IPv4 layer values across distinct UDP stacks.");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => (long)_StackCount * _FramesPerStack;

    /// <inheritdoc/>
    public override string WorkUnitName => "frames";

    /// <inheritdoc/>
    public override void Setup()
    {
        InitializeBuffers();
        _Eth = new EthernetLayer(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
        _Ip = new IPv4Layer(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));

        // Buffer sized for Eth(14) + IPv4(20) + UDP(8) + payload.
        _Buffer = new byte[14 + 20 + 8 + PayloadSize];
    }

    /// <inheritdoc/>
    public override void Run()
    {
        Span<byte> dst = _Buffer;
        for (int s = 0; s < _StackCount; s++)
        {
            // New UDP layer per stack (different dst port) — shares Eth/IPv4 values.
            UdpLayer udp = new(srcPort: 12345, dstPort: (ushort)(10000 + s));
            CreatedStack<
                StatelessStack<UdpLayer,
                    StatelessStack<IPv4Layer,
                        StatelessStack<EthernetLayer, StackEnd>>>,
                NoTrailer,
                NoInterceptor> stack = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues();

            for (int f = 0; f < _FramesPerStack; f++)
            {
                stack.Build(_Payload).MoveNext(dst, out _);
            }
        }
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
    }
}
