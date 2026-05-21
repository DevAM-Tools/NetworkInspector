// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// PS-4: Frame build with a custom interceptor invoked per layer.
///
/// <para>
/// <b>Hot path:</b> repeated <c>Build(payload).MoveNext(...)</c> with a
/// non-empty <see cref="IFrameInterceptor"/> implemented as a
/// <c>readonly struct</c>.  Measures the JIT-specialised interceptor cost
/// (per-layer header callback + per-frame completion callback) compared to
/// PS-1's <see cref="NoInterceptor"/> baseline.
/// </para>
/// </summary>
internal sealed class FrameBuilderCustomInterceptorScenario : IProfilingScenario
{
    private const int FrameCount = 1_000_000;
    private const int PayloadSize = 64;

    /// <summary>
    /// Counting interceptor — exercises the per-header and per-frame callback
    /// hot paths without doing meaningful work.  All counters live in shared
    /// volatile statics so the JIT cannot eliminate the call.
    /// </summary>
    private readonly struct CountingInterceptor : IFrameInterceptor
    {
        internal static long HeaderCalls;
        internal static long FrameCalls;
        internal static long ByteSum;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, IProtocolLayer
        {
            Interlocked.Increment(ref HeaderCalls);
            // Touch the first byte so the call has an observable side effect.
            if (headerSlice.Length > 0)
            {
                Interlocked.Add(ref ByteSum, headerSlice[0]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFrameComplete(scoped Span<byte> frame) => Interlocked.Increment(ref FrameCalls);
    }

    private readonly byte[] _Payload = new byte[PayloadSize];
    private byte[] _Buffer = [];
    private CreatedStack<
        StatelessStack<UdpLayer,
            StatelessStack<IPv4Layer,
                StatelessStack<EthernetLayer, StackEnd>>>,
        NoTrailer,
        CountingInterceptor> _Stack;

    /// <inheritdoc/>
    public string Name => "framebuilder-custom-interceptor";

    /// <inheritdoc/>
    public string Description =>
        $"Build {FrameCount:N0} Eth/IPv4/UDP frames with a custom IFrameInterceptor.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]));
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(srcPort: 12345, dstPort: 53);

        _Stack = FrameStack.CreateWithFixedValues(
            FrameStack.Start(eth).Then(ip).Then(udp),
            new CountingInterceptor());
        _Buffer = new byte[_Stack.HeaderSize + PayloadSize];

        CountingInterceptor.HeaderCalls = 0;
        CountingInterceptor.FrameCalls = 0;
        CountingInterceptor.ByteSum = 0;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Span<byte> dst = _Buffer;
        for (int i = 0; i < FrameCount; i++)
        {
            _Stack.Build(_Payload).MoveNext(dst, out _);
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }
}
