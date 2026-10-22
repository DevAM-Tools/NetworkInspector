// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>Which CAN parse a <see cref="ParseCanScenario"/> measures.</summary>
internal enum ParseCanKind
{
    /// <summary>Fresh packet per classic CAN frame (Build mode).</summary>
    Build,

    /// <summary>Fresh packet per classic CAN frame plus <see cref="Packet.MaterializeAll"/>.</summary>
    Materialized,

    /// <summary>Fresh packet per CAN FD frame (64 data bytes).</summary>
    FdBuild,

    /// <summary>One recycled packet for all classic CAN frames.</summary>
    Recycled,
}

/// <summary>
/// Parse throughput and allocation of classic CAN and CAN FD frames: 10 000 SocketCAN frames per iteration on
/// <see cref="StackHelper.CreateStorageStack"/>. CAN frames dominate automotive captures, and a classic CAN packet
/// needs 10 field slots, so these scenarios expose per-packet field-storage overhead that the 512-byte UDP
/// scenarios hide.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class ParseCanScenario : IProfilingScenario
{
    #region Constants

    /// <summary>Number of frames parsed per <see cref="Run"/> call.</summary>
    private const int _BatchSize = 10_000;

    #endregion

    #region Fields

    private readonly ParseCanKind _Kind;
    private Stack? _Stack;
    private Frame[]? _Frames;

    /// <summary>The packet reused by <see cref="ParseCanKind.Recycled"/>; null for the other kinds.</summary>
    private Packet? _Recycle;

    /// <summary>Next packet id; ids stay dense and ascending across iterations.</summary>
    private int _PacketCounter;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="kind">Which CAN parse to measure.</param>
    internal ParseCanScenario(ParseCanKind kind) => _Kind = kind;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => _Kind switch
    {
        ParseCanKind.Build => "parse-can-build",
        ParseCanKind.Materialized => "parse-can-materialized",
        ParseCanKind.FdBuild => "parse-canfd-build",
        _ => "parse-can-recycled",
    };

    /// <inheritdoc/>
    public string Description => _Kind switch
    {
        ParseCanKind.Build => Invariant($"TryParse, {_BatchSize:N0} classic CAN frames per iteration (fresh packets)."),
        ParseCanKind.Materialized => Invariant($"TryParse + MaterializeAll, {_BatchSize:N0} classic CAN frames per iteration."),
        ParseCanKind.FdBuild => Invariant($"TryParse, {_BatchSize:N0} CAN FD frames (64 data bytes) per iteration."),
        _ => Invariant($"TryParse(recycle), {_BatchSize:N0} classic CAN frames per iteration into one packet."),
    };

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStorageStack();
        _Frames = _Kind == ParseCanKind.FdBuild
            ? SampleFrames.CreateCanFdFrames(_BatchSize, _Stack)
            : SampleFrames.CreateCanFrames(_BatchSize, _Stack);
        _PacketCounter = 0;
        if (_Kind != ParseCanKind.Recycled)
        {
            return;
        }

        // The seed parse is the only Packet allocation of the recycled variant.
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
        int counter = _PacketCounter;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(counter + _BatchSize - 1, "packet");

        // Dense, ascending ids across iterations: every parse is a first parse in id order.
        ParseOptions options = new();
        for (int i = 0; i < _BatchSize; i++)
        {
            PacketId id = new(counter + i);
            if (_Kind == ParseCanKind.Recycled)
            {
                if (!Packet.TryParse(_Recycle!, id, stack, frames[i], in options, out ParseFailure recycleFailure))
                {
                    throw new InvalidOperationException(recycleFailure.ToString());
                }

                continue;
            }

            if (!Packet.TryParse(id, stack, frames[i], in options, out Packet? packet, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            if (_Kind == ParseCanKind.Materialized)
            {
                packet!.MaterializeAll();
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
