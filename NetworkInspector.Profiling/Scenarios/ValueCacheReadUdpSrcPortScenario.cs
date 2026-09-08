// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// SIMD-scans every published <c>udp.srcport</c> payload from a pre-built <see cref="ValueCache"/>.
/// Setup fills the cache once; the timed loop walks value chunks only (no row gather, no parse).
/// Pair with <c>packet-reparse-read-udp-srcport</c> to compare column scan vs re-parse.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ValueCacheReadUdpSrcPortScenario : IProfilingScenario
{
    #region Fields

    private const int _PacketCount = 10_000;

    private Stack? _Stack;
    private ValueCache? _Cache;
    private ValueCacheSeriesHandle<ulong> _Handle;
    private ulong _Sink;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "value-cache-read-udp-srcport";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"SIMD-scan pre-built ValueCache udp.srcport values ({_PacketCount:N0} rows) — no parse, no rows.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _PacketCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(_PacketCount, _Stack);
        FieldId portId = _RequireUdpSrcPort(_Stack);
        _Cache = new ValueCache(_Stack, [new ValueCacheFieldConfig(portId)]);
        Packet recycle = Packet.ParseFrame(new PacketId(0), _Stack, frames[0]);
        for (int i = 0; i < _PacketCount; i++)
        {
            RecycleError? error = Packet.TryParseFrame(
                recycle, new PacketId(i + 1), _Stack, frames[i], FieldTreeMode.Build, _Cache);
            if (error is not null)
            {
                throw new InvalidOperationException(error.ToString());
            }
        }

        ValueCacheSeries<ulong> series = _Cache.GetSeries<ulong>(portId);
        if (series.Count != _PacketCount)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Expected {_PacketCount} udp.srcport rows, got {series.Count}."));
        }

        _Handle = series.Handle;
    }

    /// <inheritdoc/>
    public void Run()
    {
        ulong sink = 0;
        int chunkIndex = 0;
        while (_Handle.TryGetValueChunk(chunkIndex, out ReadOnlySpan<ulong> values))
        {
            sink += _SumPorts(values);
            chunkIndex++;
        }

        _Sink = sink;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _ = _Sink;
        _Stack?.Dispose();
        _Stack = null;
        _Cache = null;
        _Handle = default;
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Horizontal sum of a value-chunk. Widest available SIMD, then scalar.
    /// <see cref="Vector256.IsHardwareAccelerated"/> / <see cref="Vector128.IsHardwareAccelerated"/>
    /// are JIT constants — unsupported ISAs drop out. No-SIMD and short spans use
    /// <see cref="_SumPortsScalar"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SumPorts(ReadOnlySpan<ulong> values)
    {
        if (Vector256.IsHardwareAccelerated && values.Length >= Vector256<ulong>.Count)
        {
            return _SumPortsVector256(values);
        }

        if (Vector128.IsHardwareAccelerated && values.Length >= Vector128<ulong>.Count)
        {
            return _SumPortsVector128(values);
        }

        return _SumPortsScalar(values);
    }

    /// <summary>
    /// Vector256 lanes plus scalar tail. Spans are not 32-byte aligned —
    /// unaligned <see cref="Vector256.Create{T}(ReadOnlySpan{T})"/> loads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SumPortsVector256(ReadOnlySpan<ulong> values)
    {
        int width = Vector256<ulong>.Count;
        Vector256<ulong> acc = Vector256<ulong>.Zero;
        int i = 0;
        for (; i + width <= values.Length; i += width)
        {
            acc += Vector256.Create(values.Slice(i, width));
        }

        ulong vectorSum = Vector256.Sum(acc);
        ulong tail = _SumPortsScalar(values[i..]);
        return vectorSum + tail;
    }

    /// <summary>
    /// Vector128 lanes plus scalar tail. Unaligned <see cref="Vector128.Create{T}(ReadOnlySpan{T})"/> loads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SumPortsVector128(ReadOnlySpan<ulong> values)
    {
        int width = Vector128<ulong>.Count;
        Vector128<ulong> acc = Vector128<ulong>.Zero;
        int i = 0;
        for (; i + width <= values.Length; i += width)
        {
            acc += Vector128.Create(values.Slice(i, width));
        }

        ulong vectorSum = Vector128.Sum(acc);
        ulong tail = _SumPortsScalar(values[i..]);
        return vectorSum + tail;
    }

    /// <summary>Scalar fallback when SIMD is missing or the remaining span is shorter than a vector.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong _SumPortsScalar(ReadOnlySpan<ulong> values)
    {
        ulong sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
        }

        return sum;
    }

    /// <summary>Resolves <c>udp.srcport</c> on the profiling stack.</summary>
    private static FieldId _RequireUdpSrcPort(Stack stack)
    {
        FieldId? portId = stack.GetFieldId("udp.srcport");
        if (portId is null)
        {
            throw new InvalidOperationException("Profiling stack is missing field 'udp.srcport'.");
        }

        return portId.Value;
    }

    #endregion
}
