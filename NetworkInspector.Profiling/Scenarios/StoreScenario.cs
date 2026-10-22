// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>Which packets a <see cref="StoreScenario"/> parses and keeps.</summary>
internal enum StoreKind
{
    /// <summary>10 000 classic CAN packets, all kept.</summary>
    Can,

    /// <summary>10 000 classic CAN packets after <see cref="Packet.MaterializeAll"/>, all kept.</summary>
    CanMaterialized,

    /// <summary>10 000 CAN FD packets, all kept.</summary>
    CanFd,

    /// <summary>10 000 Ethernet/IPv6/UDP packets (512 bytes), all kept.</summary>
    Udp,

    /// <summary>10 000 Ethernet/IPv6/UDP packets after <see cref="Packet.MaterializeAll"/>, all kept.</summary>
    UdpMaterialized,

    /// <summary>10 000 packets of the mixed stream (CAN, CAN, CAN FD, UDP), all kept.</summary>
    Mixed,

    /// <summary>200 000 packets of the mixed stream parsed, every 1 000th kept (200 packets).</summary>
    Sparse,
}

/// <summary>
/// Retained managed memory per stored packet. One <see cref="FootprintScenarioBase.Run"/> parses the workload with
/// fresh packets, keeps them (or every 1 000th for <see cref="StoreKind.Sparse"/>), and measures the live heap with
/// them reachable. The sparse variant shows how much memory a few kept packets pin through shared slabs.
/// </summary>
/// <remarks>
/// <para>The value also contains the thread-local slab being filled and, with a chunk pool, the thread-local pool
/// (each at most 64 KiB per measurement). Divided by 10 000 packets that is noise; for the 200 kept sparse packets it
/// is up to about 650 bytes per packet. The report states this.</para>
/// <para><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</para>
/// </remarks>
internal sealed class StoreScenario : FootprintScenarioBase
{
    #region Constants

    private const int _KeptPackets = 10_000;
    private const int _SparseFrames = 200_000;
    private const int _SparsePeriod = 1_000;

    #endregion

    #region Fields

    private readonly StoreKind _Kind;
    private Stack? _Stack;
    private Frame[] _Frames = [];

    /// <summary>Next packet id; ids stay dense and ascending across iterations, so every parse is a first parse.</summary>
    private int _NextPacketId;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="kind">Which packets to parse and keep.</param>
    internal StoreScenario(StoreKind kind) => _Kind = kind;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public override string Name => _Kind switch
    {
        StoreKind.Can => "store-can",
        StoreKind.CanMaterialized => "store-can-materialized",
        StoreKind.CanFd => "store-canfd",
        StoreKind.Udp => "store-udp",
        StoreKind.UdpMaterialized => "store-udp-materialized",
        StoreKind.Mixed => "store-mixed",
        _ => "store-sparse",
    };

    /// <inheritdoc/>
    public override string Description => _Kind == StoreKind.Sparse
        ? Invariant($"Retained bytes per kept packet: {_SparseFrames:N0} mixed packets parsed, every {_SparsePeriod:N0}th kept.")
        : Invariant($"Retained bytes per stored packet: {_KeptPackets:N0} packets ({Name}).");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => _Kind == StoreKind.Sparse
        ? _SparseFrames / _SparsePeriod
        : _KeptPackets;

    /// <inheritdoc/>
    public override string WorkUnitName => "packets";

    /// <inheritdoc/>
    public override void Setup()
    {
        _Stack = StackHelper.CreateStorageStack();
        _NextPacketId = 0;
        _Frames = _Kind switch
        {
            StoreKind.Can or StoreKind.CanMaterialized => SampleFrames.CreateCanFrames(_KeptPackets, _Stack),
            StoreKind.CanFd => SampleFrames.CreateCanFdFrames(_KeptPackets, _Stack),
            StoreKind.Udp or StoreKind.UdpMaterialized => SampleFrames.CreateUniformEthernet(_KeptPackets, _Stack, 512),
            StoreKind.Mixed => SampleFrames.CreateMixedStorageFrames(_KeptPackets, _Stack, fanoutPeriod: 0, fanoutItems: 0),
            _ => SampleFrames.CreateMixedStorageFrames(_SparseFrames, _Stack, fanoutPeriod: 0, fanoutItems: 0),
        };
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
        _Frames = [];
    }

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected override IDisposable OpenAndScan()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames;
        bool materialize = _Kind is StoreKind.CanMaterialized or StoreKind.UdpMaterialized;
        bool sparse = _Kind == StoreKind.Sparse;
        Packet[] kept = new Packet[WorkUnitsPerIteration];
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(_NextPacketId + frames.Length - 1, "packet");

        // Fresh packets in dense id order; only kept packets stay reachable through the holder.
        ParseOptions options = new();
        for (int i = 0; i < frames.Length; i++)
        {
            if (!Packet.TryParse(new PacketId(_NextPacketId + i), stack, frames[i], in options, out Packet? packet, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }

            if (materialize)
            {
                packet!.MaterializeAll();
            }

            if (!sparse)
            {
                kept[i] = packet!;
            }
            else if (i % _SparsePeriod == 0)
            {
                kept[i / _SparsePeriod] = packet!;
            }
        }

        _NextPacketId += frames.Length;
        return new KeptPackets(kept);
    }

    #endregion

    #region Nested types

    /// <summary>Keeps the stored packets reachable until the base class measured; Dispose releases them.</summary>
    private sealed class KeptPackets(Packet[] packets) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose() => Array.Clear(packets);
    }

    #endregion
}
