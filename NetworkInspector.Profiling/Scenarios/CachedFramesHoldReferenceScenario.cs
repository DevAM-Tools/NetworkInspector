// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Sequential <see cref="CachedFrameSource"/> drain. The wrapper holds each inner
/// <see cref="Frame"/> (payload memory is not copied).
/// <see cref="PrepareIteration"/> constructs the wrapper; <see cref="Run"/> drains <see cref="IFrameSource.NextFrame"/>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class CachedFramesHoldReferenceScenario : IProfilingScenario, IDisposable
{
    #region Fields

    private const int _FrameCount = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private CachedFrameSource? _Source;
    private long _PayloadBytes;
    private long _IndexBytes;
    private bool _Ran;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "cached-frames-hold-reference";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"PrepareIteration: CachedFrameSource; Run: NextFrame {_FrameCount:N0} sequential frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_FrameCount, _Stack);
    }

    /// <inheritdoc/>
    public Action? PrepareIteration => _PrepareIteration;

    private void _PrepareIteration()
    {
        _Source?.Dispose();
        SequentialMemoryFrameSource inner = new(_Frames!);
        _Source = new CachedFrameSource(inner);
        _Source.Start(new FrameSourceId(0), _Stack!.FrameInterfaceRegistry);
    }

    /// <inheritdoc/>
    public void Run()
    {
        CachedFrameSource source = _Source!;
        while (source.NextFrame() is not null)
        {
        }

        _PayloadBytes = source.EstimatedPayloadBytes;
        _IndexBytes = source.EstimatedIndexBytes;
        _Ran = true;
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_Ran)
        {
            Console.WriteLine(
                FormattableString.Invariant(
                    $"EstimatedPayloadBytes={_PayloadBytes}, EstimatedIndexBytes={_IndexBytes}"));
            _Ran = false;
        }
        _Source?.Dispose();
        _Source = null;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
    }

    #endregion
}
