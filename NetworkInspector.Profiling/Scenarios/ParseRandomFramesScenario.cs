// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that parses a batch of synthetic IPv6/UDP frames
/// on every <see cref="Run"/> call.
///
/// <para>
/// Two variants exist:
/// <list type="bullet">
///   <item><b>parse-random-frames</b> — <see cref="Packet.ParseFrame(PacketId, Stack, Frame)"/> only (lazy field tree).</item>
///   <item><b>parse-random-frames-materialized</b> — <see cref="Packet.ParseFrame(PacketId, Stack, Frame)"/> +
///     <see cref="Packet.MaterializeAll"/> (fully walks and stores the field tree).</item>
/// </list>
/// Comparing the two isolates the cost of field-tree materialisation.
/// </para>
///
/// <para>
/// Stack construction and frame generation happen in <see cref="Setup"/>.
/// </para>
/// </summary>
internal sealed class ParseRandomFramesScenario : IProfilingScenario
{
    /// <summary>Number of frames parsed per <see cref="Run"/> call.</summary>
    private const int BatchSize = 10_000;

    private readonly bool _Materialize;

    private Stack? _Stack;
    private Frame[]? _Frames;

    /// <summary>Counter for unique <see cref="PacketId"/> values across all iterations.</summary>
    private long _PacketCounter;

    /// <summary>Creates a parse-random-frames profiling scenario.</summary>
    /// <param name="materialize">
    /// When <see langword="true"/>, calls <see cref="Packet.MaterializeAll"/> after each parse
    /// to fully walk and store the field tree. When <see langword="false"/>, only
    /// <see cref="Packet.ParseFrame(PacketId, Stack, Frame)"/> is called (the field tree is built lazily).
    /// </param>
    internal ParseRandomFramesScenario(bool materialize)
    {
        _Materialize = materialize;
    }

    /// <inheritdoc/>
    public string Name => _Materialize ? "parse-random-frames-materialized" : "parse-random-frames";

    /// <inheritdoc/>
    public string Description => _Materialize
        ? $"ParseFrame + MaterializeAll, {BatchSize:N0} IPv6/UDP frames per iteration."
        : $"ParseFrame only (lazy field tree), {BatchSize:N0} IPv6/UDP frames per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(BatchSize, _Stack);
        _PacketCounter = 0;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        long counter = _PacketCounter;

        if (_Materialize)
        {
            for (int i = 0; i < BatchSize; i++)
            {
                Packet packet = Packet.ParseFrame(checked((int)(counter + i)), stack, frames[i]);
                packet.MaterializeAll();
            }
        }
        else
        {
            for (int i = 0; i < BatchSize; i++)
            {
                // Hot path: parse only — the field tree is built but not walked.
                Packet.ParseFrame(checked((int)(counter + i)), stack, frames[i]);
            }
        }

        _PacketCounter = counter + BatchSize;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
        _PacketCounter = 0;
    }
}
