// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-5: Layer-value reuse across many independent stacks (R12).
///
/// <para>
/// <b>Hot path:</b> a single shared Eth/IPv4 prefix is reused as the base of
/// many distinct UDP stacks (different destination ports).  Each iteration
/// builds <see cref="StackCount"/> frames using the same cached layer values
/// — measures whether layer values can in fact be shared zero-copy across
/// many <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> instances
/// without per-build allocation overhead.
/// </para>
/// </summary>
internal sealed class FrameBuilderValueReuseScenario : IProfilingScenario
{
    private const int StackCount = 100;
    private const int FramesPerStack = 10_000;
    private const int PayloadSize = 32;

    private readonly byte[] _Payload = new byte[PayloadSize];
    private byte[] _Buffer = [];
    private EthernetLayer _Eth;
    private IPv4Layer _Ip;

    /// <inheritdoc/>
    public string Name => "framebuilder-value-reuse";

    /// <inheritdoc/>
    public string Description =>
        $"Build {StackCount:N0} × {FramesPerStack:N0} = {(long)StackCount * FramesPerStack:N0} frames reusing the same Eth/IPv4 layer values across distinct UDP stacks.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => (long)StackCount * FramesPerStack;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Eth = new EthernetLayer(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
        _Ip = new IPv4Layer(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));

        // Buffer sized for Eth(14) + IPv4(20) + UDP(8) + payload.
        _Buffer = new byte[14 + 20 + 8 + PayloadSize];
    }

    /// <inheritdoc/>
    public void Run()
    {
        Span<byte> dst = _Buffer;
        for (int s = 0; s < StackCount; s++)
        {
            // New UDP layer per stack (different dst port) — shares Eth/IPv4 values.
            UdpLayer udp = new(srcPort: 12345, dstPort: (ushort)(10000 + s));
            CreatedStack<
                StatelessStack<UdpLayer,
                    StatelessStack<IPv4Layer,
                        StatelessStack<EthernetLayer, StackEnd>>>,
                NoTrailer,
                NoInterceptor> stack = FrameStack.Start(_Eth).Then(_Ip).Then(udp).CreateWithFixedValues();

            for (int f = 0; f < FramesPerStack; f++)
            {
                stack.Build(_Payload).MoveNext(dst, out _);
            }
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }
}
