// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Recycled parse of a mixed stream (CAN, CAN, CAN FD, UDP) with one 600-field fanout frame per 1 000 frames:
/// 10 000 frames per iteration into one packet. Measures what the recycle policy costs when a large frame
/// occasionally inflates the packet and the next small frames shrink it again.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class ParseMixedRecycledScenario : IProfilingScenario
{
    #region Constants

    /// <summary>Number of frames parsed per <see cref="Run"/> call.</summary>
    private const int _BatchSize = 10_000;

    /// <summary>Distance between fanout frames.</summary>
    private const int _FanoutPeriod = 1_000;

    /// <summary>Items per fanout frame (601 slots with its container, plus root and frame node).</summary>
    private const int _FanoutItems = 600;

    #endregion

    #region Fields

    private Stack? _Stack;
    private Frame[]? _Frames;

    /// <summary>The packet reused for every frame.</summary>
    private Packet? _Recycle;

    /// <summary>Next packet id; ids stay dense and ascending across iterations.</summary>
    private int _PacketCounter;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => "parse-mixed-recycled";

    /// <inheritdoc/>
    public string Description =>
        Invariant($"TryParse(recycle), {_BatchSize:N0} mixed CAN/CAN FD/UDP frames, one {_FanoutItems}-field frame per {_FanoutPeriod:N0}.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStorageStack();
        _Frames = SampleFrames.CreateMixedStorageFrames(_BatchSize, _Stack, _FanoutPeriod, _FanoutItems);

        // The seed parse is the only Packet allocation of this scenario.
        ParseOptions seedOptions = new();
        if (!Packet.TryParse(new PacketId(0), _Stack, _Frames[0], in seedOptions, out Packet? seeded, out ParseFailure seedFailure))
        {
            throw new InvalidOperationException(seedFailure.ToString());
        }

        _Recycle = seeded;
        _PacketCounter = 1;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _Recycle!;
        int counter = _PacketCounter;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(counter + _BatchSize - 1, "packet");

        // Dense, ascending ids across iterations: every parse is a first parse in id order.
        ParseOptions options = new();
        for (int i = 0; i < _BatchSize; i++)
        {
            PacketId id = new(counter + i);
            if (!Packet.TryParse(recycle, id, stack, frames[i], in options, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
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
        _Recycle = null;
        _PacketCounter = 0;
    }

    #endregion
}
