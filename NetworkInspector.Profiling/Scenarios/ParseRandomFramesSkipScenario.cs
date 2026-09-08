// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Recycled <see cref="FieldTreeMode.Skip"/> parse with no value cache.
/// Counterpart of <c>parse-random-frames-recycled</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class ParseRandomFramesSkipScenario : IProfilingScenario
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
    public string Name => "parse-random-frames-recycled-skip";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"ParseFrame(recycle, FieldTreeMode.Skip) only, {_BatchSize:N0} IPv6/UDP frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_BatchSize, _Stack);
        _PacketCounter = 0;
        _RecyclePacket = Packet.ParseFrame(new PacketId(0), _Stack, _Frames[0], FieldTreeMode.Skip);
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _RecyclePacket!;
        int counter = _PacketCounter;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(counter + _BatchSize - 1, "packet");

        for (int i = 0; i < _BatchSize; i++)
        {
            Packet.ParseFrame(recycle, new PacketId(counter + i), stack, frames[i], FieldTreeMode.Skip);
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
