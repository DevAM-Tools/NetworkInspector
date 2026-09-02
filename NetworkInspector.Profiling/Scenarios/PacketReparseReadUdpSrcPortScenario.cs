// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Re-parses each stored frame into a recycled <see cref="Packet"/> and walks the field
/// tree (<see cref="Packet.IterFieldsDfs"/>) for <c>udp.srcport</c>. Setup performs the first
/// parse so the timed loop is a redissect, the same work a session does without a value cache.
/// Pair with <c>value-cache-read-udp-srcport</c>.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:AvoidUninstantiatedInternalClasses",
    Justification = "Instantiated via reflection in ScenarioDiscovery.Discover.")]
internal sealed class PacketReparseReadUdpSrcPortScenario : IProfilingScenario
{
    #region Fields

    private const int _PacketCount = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private Packet? _RecyclePacket;
    private FieldId _PortId;
    private ulong _Sink;

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string Name => "packet-reparse-read-udp-srcport";

    /// <inheritdoc/>
    public string Description => FormattableString.Invariant(
        $"ParseFrame(recycle) + IterFieldsDfs for udp.srcport, {_PacketCount:N0} IPv6/UDP frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _PacketCount;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_PacketCount, _Stack);
        FieldId? portId = _Stack.GetFieldId("udp.srcport")
            ?? throw new InvalidOperationException("Profiling stack is missing field 'udp.srcport'.");
        _PortId = portId.Value;

        // First parse so the timed loop is a redissect with protocol replay state in place.
        for (int i = 0; i < _PacketCount; i++)
        {
            Packet.ParseFrame(new PacketId(i), _Stack, _Frames[i]);
        }

        _RecyclePacket = Packet.ParseFrame(new PacketId(0), _Stack, _Frames[0]);
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        Packet recycle = _RecyclePacket!;
        FieldId portId = _PortId;
        ulong sink = 0;

        for (int i = 0; i < _PacketCount; i++)
        {
            Packet.ParseFrame(recycle, new PacketId(i), stack, frames[i]);
            foreach (Field field in recycle.IterFieldsDfs(materialize: false))
            {
                if (field.FieldId != portId)
                {
                    continue;
                }

                if (field.Value.Data.TryGetAsU64(out ulong port))
                {
                    sink += port;
                }

                break;
            }
        }

        _Sink = sink;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _ = _Sink;
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
        _RecyclePacket = null;
    }

    #endregion
}
