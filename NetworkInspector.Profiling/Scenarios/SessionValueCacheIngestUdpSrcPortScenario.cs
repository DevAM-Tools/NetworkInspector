// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Session ingest-time tee of <c>udp.srcport</c> via <see cref="SessionOptions.ValueCache"/>.
/// New stack and session per <see cref="Run"/> so packet ids 0..N-1 are first-parses (replays do not tee).
/// Pair with <c>session-value-cache-ondemand-udp-srcport</c>. Compare session overhead against
/// <c>session-listener</c> (lazy pull, no tee) and tee cost against
/// <c>parse-random-frames-recycled-recorded</c>. Do not compare to
/// <c>session-listener-materialized</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionValueCacheIngestUdpSrcPortScenario : IProfilingScenario
{
    #region Fields

    private const int _FrameCount = 10_000;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "session-value-cache-ingest-udp-srcport";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"New Stack+Session per Run: SessionOptions.ValueCache(udp.srcport) ingest {_FrameCount:N0} frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        // Stack and frames are created inside Run so each iteration first-parses packet ids 0..N-1.
    }

    /// <inheritdoc/>
    public void Run()
    {
        using Stack stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(_FrameCount, stack);
        SessionValueCacheHarness.RunIngest(
            stack,
            frames,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            _FrameCount);
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }

    #endregion
}
