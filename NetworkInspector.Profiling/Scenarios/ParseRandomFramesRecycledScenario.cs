// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that demonstrates zero-allocation packet parsing by reusing a single
/// <see cref="Packet"/> object across all frames in each <see cref="Run"/> call.
///
/// <para>
/// Two variants exist:
/// <list type="bullet">
///   <item>
///     <b>parse-random-frames-recycled</b> — <see cref="Packet.ParseFrame(Packet, PacketId, Stack, Frame)"/>
///     only (lazy field tree). Equivalent to parse-random-frames but with zero <see cref="Packet"/> heap allocations.
///   </item>
///   <item>
///     <b>parse-random-frames-materialized-recycled</b> — recycled parse +
///     <see cref="Packet.MaterializeAll"/> (fully walks and stores the field tree).
///     Equivalent to parse-random-frames-materialized but with zero <see cref="Packet"/> heap allocations.
///   </item>
/// </list>
/// Comparing these variants with <see cref="ParseRandomFramesScenario"/> isolates
/// the GC pressure and allocation cost of per-packet <c>new Packet(...)</c> calls.
/// </para>
///
/// <para>
/// The recycle packet is initialised once in <see cref="Setup"/>.
/// Each <see cref="Run"/> call re-parses all frames into the same <see cref="Packet"/> object
/// via <see cref="Packet.ParseFrame(Packet, PacketId, Stack, Frame)"/>,
/// completely eliminating the heap allocation of a new <c>Packet</c> on every frame.
/// </para>
/// </summary>
internal sealed class ParseRandomFramesRecycledScenario : IProfilingScenario
{
    /// <summary>Number of frames parsed per <see cref="Run"/> call.</summary>
    private const int _BatchSize = 10_000;

    private readonly bool _Materialize;

    private Stack? _Stack;
    private Frame[]? _Frames;

    /// <summary>
    /// The single <see cref="Packet"/> instance reused across all frames in every iteration.
    /// Initialised in <see cref="Setup"/> from an initial seed parse.
    /// </summary>
    private Packet? _RecyclePacket;

    /// <summary>Counter for unique <see cref="PacketId"/> values across all iterations.</summary>
    private int _PacketCounter;

    /// <summary>Creates a recycled parse profiling scenario.</summary>
    /// <param name="materialize">
    /// When <see langword="true"/>, calls <see cref="Packet.MaterializeAll"/> after each
    /// recycled parse to fully walk and store the field tree. When <see langword="false"/>,
    /// only the recycled parse is performed (lazy field tree).
    /// </param>
    internal ParseRandomFramesRecycledScenario(bool materialize)
    {
        _Materialize = materialize;
    }

    /// <inheritdoc/>
    public string Name => _Materialize
        ? "parse-random-frames-materialized-recycled"
        : "parse-random-frames-recycled";

    /// <inheritdoc/>
    public string Description => _Materialize
        ? FormattableString.Invariant(
            $"ParseFrame(recycle) + MaterializeAll, {_BatchSize:N0} IPv6/UDP frames per iteration — zero Packet allocations.")
        : FormattableString.Invariant(
            $"ParseFrame(recycle) only (lazy field tree), {_BatchSize:N0} IPv6/UDP frames per iteration — zero Packet allocations.");

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

        // Create the initial seed packet that will be recycled throughout all iterations.
        // This is the only Packet heap allocation in the entire scenario.
        _RecyclePacket = Packet.ParseFrame(new PacketId(0), _Stack, _Frames[0]);
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _RecyclePacket!;
        int counter = _PacketCounter;
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(counter + _BatchSize - 1, "packet");

        // Hot path: reuse the same Packet object for every frame.
        // Each call to ParseFrame(recycle, ...) invokes PrepareForReuse internally,
        // clearing the previous parse's state and replacing it with the new frame's data —
        // without allocating a new Packet on the heap.
        if (_Materialize)
        {
            for (int i = 0; i < _BatchSize; i++)
            {
                Packet.ParseFrame(recycle, new PacketId(counter + i), stack, frames[i]);
                recycle.MaterializeAll();
            }
        }
        else
        {
            for (int i = 0; i < _BatchSize; i++)
            {
                Packet.ParseFrame(recycle, new PacketId(counter + i), stack, frames[i]);
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
}
