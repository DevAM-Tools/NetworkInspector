// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Recycled skip first-parse record into a new <see cref="ValueCache"/> with
/// <see cref="ValueCacheBuildOptions.RecordAllFields"/> on every <see cref="Run"/> call.
/// Counterpart of <c>value-cache-build-all-fields</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ValueCacheBuildAllFieldsSkipScenario : IProfilingScenario
{
    #region Fields

    private const int _BatchSize = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Packet? _RecyclePacket;
    private int _PacketCounter;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "value-cache-build-all-fields-skip";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"New RecordAllFields ValueCache per Run + TryParseFrame(recycle, Skip, cache), {_BatchSize:N0} IPv6/UDP frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_BatchSize, _Stack);
        _RecyclePacket = Packet.ParseFrame(new PacketId(0), _Stack, _Frames[0], FieldTreeMode.Skip);
        _PacketCounter = 1;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _RecyclePacket!;
        int counter = _PacketCounter;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(counter + _BatchSize - 1, "packet");

        ValueCache cache = new(stack, [], options: new ValueCacheBuildOptions { RecordAllFields = true });
        for (int i = 0; i < _BatchSize; i++)
        {
            RecycleError? error = Packet.TryParseFrame(
                recycle, new PacketId(counter + i), stack, frames[i], FieldTreeMode.Skip, cache);
            if (error is not null)
            {
                throw new InvalidOperationException(error.ToString());
            }
        }

        _PacketCounter = counter + _BatchSize;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
        _RecyclePacket = null;
        _PacketCounter = 0;
    }

    #endregion
}
