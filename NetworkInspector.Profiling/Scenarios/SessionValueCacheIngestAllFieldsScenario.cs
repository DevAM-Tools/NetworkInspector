// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Session ingest-time tee of every stack field via <see cref="ValueCacheRequest.RecordAllFields"/>.
/// New stack and session per <see cref="Run"/> so packet ids 0..N-1 are first-parses.
/// Pair with <c>session-value-cache-ondemand-all-fields</c>. Compare tee cost against
/// <c>value-cache-build-all-fields</c> and session overhead against <c>session-listener</c> (lazy).
/// Do not compare to <c>session-listener-materialized</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionValueCacheIngestAllFieldsScenario : IProfilingScenario
{
    #region Fields

    private const int _FrameCount = 10_000;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "session-value-cache-ingest-all-fields";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"New Stack+Session per Run: SessionOptions.ValueCache(RecordAllFields) ingest {_FrameCount:N0} frames.");

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
            new ValueCacheRequest { RecordAllFields = true },
            _FrameCount);
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }

    #endregion
}
