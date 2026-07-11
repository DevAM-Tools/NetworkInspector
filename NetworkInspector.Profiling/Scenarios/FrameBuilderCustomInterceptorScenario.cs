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
[SuppressMessage("Performance", "CA1812:AvoidUninstantiatedInternalClasses", Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class FrameBuilderCustomInterceptorScenario : FrameBuilderScenarioBase
{
    private const int _FrameCount = 1_000_000;

    /// <inheritdoc/>
    protected override int PayloadSize => 64;

    /// <summary>
    /// Counting interceptor — exercises the per-header and per-frame callback
    /// hot paths without doing meaningful work.  All counters live in shared
    /// volatile statics so the JIT cannot eliminate the call.
    /// </summary>
    /// <remarks>
    /// Thread-safety: <see cref="HeaderCalls"/>, <see cref="FrameCalls"/>, and
    /// <see cref="ByteSum"/> are declared volatile — every read/write must go through
    /// <see cref="Interlocked"/>. Plain field access (e.g. <c>++HeaderCalls</c>) is a
    /// data-race defect.
    /// </remarks>
    private readonly struct CountingInterceptor : IFrameInterceptor
    {
        /// <summary>Volatile — every read/write must use <see cref="Interlocked"/>. Counts header callbacks.</summary>
        internal static long HeaderCalls;  // volatile
        /// <summary>Volatile — every read/write must use <see cref="Interlocked"/>. Counts frame-complete callbacks.</summary>
        internal static long FrameCalls;   // volatile
        /// <summary>Volatile — every read/write must use <see cref="Interlocked"/>. Accumulates first-byte values to prevent dead-code elimination.</summary>
        internal static long ByteSum;      // volatile

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

    private CreatedStack<
        StatelessStack<UdpLayer,
            StatelessStack<IPv4Layer,
                StatelessStack<EthernetLayer, StackEnd>>>,
        NoTrailer,
        CountingInterceptor> _Stack;

    /// <inheritdoc/>
    public override string Name => "framebuilder-custom-interceptor";

    /// <inheritdoc/>
    public override string Description =>
        FormattableString.Invariant(
            $"Build {_FrameCount:N0} Eth/IPv4/UDP frames with a custom IFrameInterceptor.");

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
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        UdpLayer udp = new(srcPort: 12345, dstPort: 53);

        CountingInterceptor interceptor = new();
        _Stack = FrameStack.CreateWithFixedValues(
            FrameStack.Start(eth).Then(ip).Then(udp),
            interceptor);
        _Buffer = new byte[_Stack.HeaderSize + PayloadSize];

        CountingInterceptor.HeaderCalls = 0;
        CountingInterceptor.FrameCalls = 0;
        CountingInterceptor.ByteSum = 0;
    }

    /// <inheritdoc/>
    public override void Run()
    {
        Span<byte> dst = _Buffer;
        for (int i = 0; i < _FrameCount; i++)
        {
            _Stack.Build(_Payload).MoveNext(dst, out _);
        }
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
    }
}
