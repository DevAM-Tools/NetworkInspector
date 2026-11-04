// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Pbf;

/// <summary>
/// One open PBF block: the builder of the export's layout (standard or columnar) that the caller fills packet by packet and
/// the writer thread serializes, compresses and writes. Two exist per export in background mode.
/// <para>
/// <b>Why a wrapper:</b> the two layouts have unrelated builder types; the writer pipeline moves one batch type between its
/// threads, so this type carries exactly one of them and forwards the few members the pipeline and the exporter need.
/// </para>
/// <para><b>Thread safety:</b> owned by one thread at a time (see <see cref="IExportBatch"/>).</para>
/// </summary>
internal sealed class PbfBlock : IExportBatch
{
    #region Builder access and forwarding

    /// <summary>Creates a block around exactly one builder.</summary>
    /// <param name="standard">The row-oriented builder, or <see langword="null"/> for the columnar layout.</param>
    /// <param name="columnar">The columnar builder, or <see langword="null"/> for the standard layout.</param>
    internal PbfBlock(StandardBlockBuilder? standard, ColumnarBlockBuilder? columnar)
    {
        Standard = standard;
        Columnar = columnar;
    }

    /// <summary>The row-oriented builder; <see langword="null"/> for the columnar layout.</summary>
    internal StandardBlockBuilder? Standard { get; }

    /// <summary>The columnar builder; <see langword="null"/> for the standard layout.</summary>
    internal ColumnarBlockBuilder? Columnar { get; }

    /// <summary>Packets in the open block.</summary>
    public int PacketCount => Standard is not null
        ? Standard.PacketCount
        : Columnar!.PacketCount; // Invariant: exactly one builder is set.

    /// <summary>
    /// Pending size of the block in bytes: the serialized size for the standard layout, the in-memory estimate of the batch for
    /// the columnar layout (an estimate that ignores compression).
    /// </summary>
    public long EstimatedSizeBytes => Standard is not null
        ? Standard.CurrentSize
        : Columnar!.EstimatedPendingBytes; // Invariant: exactly one builder is set.

    /// <summary>
    /// The stage that receives the fields of a packet while it is parsed: the batch of the columnar layout. <see langword="null"/>
    /// for the standard layout, whose packets are added as finished field trees.
    /// </summary>
    internal IFieldStage? Stage => Columnar?.Batch;

    /// <summary>Adds a packet to the open block.</summary>
    /// <param name="packet">The packet; it must carry a field tree.</param>
    /// <returns><see langword="true"/> when the block reached a limit and should be submitted.</returns>
    internal bool AddPacket(Packet packet) => Standard is not null
        ? Standard.AddPacket(packet)
        : Columnar!.AddPacket(packet); // Invariant: exactly one builder is set.

    /// <summary>Forgets the packets of the block so it can be filled again.</summary>
    public void Reset()
    {
        Standard?.Reset();
        Columnar?.Reset();
    }

    /// <summary>Returns the pooled buffers of the builder.</summary>
    public void Dispose()
    {
        Standard?.Dispose();
        Columnar?.Dispose();
    }

    #endregion
}
