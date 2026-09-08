// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Session ingest-time record of every stack field.
/// <see cref="PrepareIteration"/> builds a fresh <see cref="Stack"/> so packet ids are first-parses.
/// <see cref="Run"/> waits for ingest.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionValueCacheIngestAllFieldsScenario : IProfilingScenario, IDisposable
{
    #region Fields

    private const int _FrameCount = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Session? _Session;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "session-value-cache-ingest-all-fields";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"PrepareIteration: new Stack+Session (fresh first-parse ids); Run: ingest wait {_FrameCount:N0} frames, ValueCache(RecordAllFields).");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
    }

    /// <inheritdoc/>
    public Action? PrepareIteration => _PrepareIteration;

    private void _PrepareIteration()
    {
        _Stack?.Dispose();
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_FrameCount, _Stack);
        _Session = SessionValueCacheHarness.StartIngest(
            _Stack,
            _Frames,
            new ValueCacheRequest { RecordAllFields = true });
    }

    /// <inheritdoc/>
    public void Run()
    {
        SessionValueCacheHarness.WaitIngest(_Session!, _FrameCount);
        _Session = null;
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Session?.Shutdown();
        _Session?.Dispose();
        _Session = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }

    #endregion
}
