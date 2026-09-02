// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Session store ingest without a value cache, then on-demand
/// <see cref="ISession.TryAddValueCache"/> for <c>udp.srcport</c>.
/// New stack and session per <see cref="Run"/>. Pair with
/// <c>session-value-cache-ingest-udp-srcport</c>. The PullFill walk is closer to
/// <c>packet-reparse-read-udp-srcport</c> than to ingest tee.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionValueCacheOndemandUdpSrcPortScenario : IProfilingScenario
{
    #region Fields

    private const int _FrameCount = 10_000;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "session-value-cache-ondemand-udp-srcport";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"New Stack+Session per Run: store {_FrameCount:N0} frames, then TryAddValueCache(udp.srcport) PullFill.");

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
        Frame trigger = SessionValueCacheHarness.CreateTriggerFrame(stack, frames);
        SessionValueCacheHarness.RunOndemand(
            stack,
            frames,
            trigger,
            new ValueCacheRequest { FieldNames = ["udp.srcport"] },
            "ondemand-udp-srcport",
            _FrameCount);
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
    }

    #endregion
}
