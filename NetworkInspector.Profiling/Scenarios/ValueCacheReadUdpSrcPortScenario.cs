// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Reads every published <c>udp.srcport</c> value from a pre-built <see cref="ValueCache"/>
/// via chunk spans. Setup fills the cache once; the timed loop does not parse.
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
    private ValueCacheSeries<ulong>? _Series;
    private ulong _Sink;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "value-cache-read-udp-srcport";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"Scan pre-built ValueCache series udp.srcport ({_PacketCount:N0} rows) via TryGetValueChunk — no parse.");

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
            RecycleError? error = Packet.TryParseFrameRecorded(
                recycle, new PacketId(i + 1), _Stack, frames[i], _Cache);
            if (error is not null)
            {
                throw new InvalidOperationException(error.ToString());
            }
        }

        _Series = _Cache.GetSeries<ulong>(portId);
        if (_Series.Count != _PacketCount)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Expected {_PacketCount} udp.srcport rows, got {_Series.Count}."));
        }
    }

    /// <inheritdoc/>
    public void Run()
    {
        ValueCacheSeries<ulong> series = _Series!;
        int observed = series.Count;
        ulong sink = 0;
        int chunkIndex = 0;
        while (series.TryGetValueChunk(chunkIndex, observed, out ReadOnlySpan<ulong> span))
        {
            for (int i = 0; i < span.Length; i++)
            {
                sink += span[i];
            }

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
        _Series = null;
    }

    #endregion

    #region Private helpers

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
