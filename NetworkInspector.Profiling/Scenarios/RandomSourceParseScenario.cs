// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Same <see cref="RandomFrameSource"/> configuration as <see cref="SessionListenerScenario"/>,
/// parsed on the calling thread with no session.
/// </summary>
/// <remarks>
/// Two variants:
/// <list type="bullet">
///   <item><b>random-source-parse</b> — ParseFrame only. Compare with <c>session-listener</c>.</item>
///   <item>
///     <b>random-source-parse-materialized</b> — parse plus <see cref="Packet.MaterializeAll"/>.
///     Compare with <c>session-listener-materialized</c>.
///   </item>
/// </list>
/// </remarks>
internal sealed class RandomSourceParseScenario : IProfilingScenario
{
    #region Constants

    private const int _FrameCount = 10_000;
    private const ulong _Seed = 42;
    private const int _MinFrameSize = 128;
    private const int _MaxFrameSize = 1024;

    #endregion

    #region Fields

    private readonly bool _Materialize;
    private Stack? _Stack;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a direct-parse scenario using <see cref="RandomFrameSource"/>.
    /// </summary>
    /// <param name="materialize">
    /// When <see langword="true"/>, calls <see cref="Packet.MaterializeAll"/> after each parse.
    /// </param>
    internal RandomSourceParseScenario(bool materialize)
    {
        _Materialize = materialize;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name
    {
        get
        {
            if (_Materialize)
            {
                return "random-source-parse-materialized";
            }

            return "random-source-parse";
        }
    }

    /// <inheritdoc/>
    public string Description => _Materialize
        ? FormattableString.Invariant(
            $"Direct parse: RandomFrameSource(UdpIPv6) -> ParseFrame -> MaterializeAll, {_FrameCount:N0} frames.")
        : FormattableString.Invariant(
            $"Direct parse: RandomFrameSource(UdpIPv6) -> ParseFrame (lazy), {_FrameCount:N0} frames.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _FrameCount;

    /// <inheritdoc/>
    public string WorkUnitName => "frames";

    /// <inheritdoc/>
    public void Setup() => _Stack = StackHelper.CreateStack();

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;

        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = _FrameCount,
            Seed = _Seed,
            Mode = RandomFrameMode.UdpIPv6,
            MinFrameSize = _MinFrameSize,
            MaxFrameSize = _MaxFrameSize,
        });

        FrameSourceId sourceId = stack.FrameInterfaceRegistry.RegisterSource(source);
        source.Start(sourceId, stack.FrameInterfaceRegistry);

        int packetId = 0;
        Frame? next;
        while ((next = source.NextFrame()) is not null)
        {
            Frame frame = next.Value;
            Packet packet = Packet.ParseFrame(new PacketId(packetId++), stack, frame);
            if (_Materialize)
            {
                packet.MaterializeAll();
            }
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
    }

    #endregion
}
