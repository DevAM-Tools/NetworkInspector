// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// On-demand <see cref="ValueCacheRequest.RecordAllFields"/> PullFill.
/// <see cref="PrepareIteration"/> starts ingest; <see cref="Run"/> is TryAddValueCache plus fill wait.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class SessionValueCacheOndemandAllFieldsScenario : IProfilingScenario, IDisposable
{
    #region Fields

    private const int _FrameCount = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Frame _TriggerFrame;
    private Session? _Session;
    private TriggerFrameSource? _Trigger;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "session-value-cache-ondemand-all-fields";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"PrepareIteration: ingest {_FrameCount:N0} frames; Run: TryAddValueCache(RecordAllFields) PullFill.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_FrameCount, _Stack);
        _TriggerFrame = SessionValueCacheHarness.CreateTriggerFrame(_Stack, _Frames);
    }

    /// <inheritdoc/>
    public Action? PrepareIteration => _PrepareIteration;

    private void _PrepareIteration()
    {
        _Session = SessionValueCacheHarness.StartOndemand(
            _Stack!,
            _Frames!,
            _TriggerFrame,
            options: null,
            out _Trigger);
    }

    /// <inheritdoc/>
    public void Run()
    {
        SessionValueCacheHarness.CompleteOndemand(
            _Session!,
            _Trigger!,
            new ValueCacheRequest { RecordAllFields = true },
            "ondemand-all-fields",
            _FrameCount);
        _Session = null;
        _Trigger = null;
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Session?.Shutdown();
        _Session?.Dispose();
        _Session = null;
        _Trigger = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }

    #endregion
}
