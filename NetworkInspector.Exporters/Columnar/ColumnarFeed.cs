// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// The one rule of how a packet enters a <see cref="ColumnarPacketBatch"/> in <c>OnPacket</c>, shared by the DuckDB, Parquet, and
/// PBF columnar exporters so the three cannot decide differently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three feeds, one rule each:</b> the hierarchy shape (no collector) walks the packet's field tree
/// (<see cref="ColumnarPacketBatch.AddPacket"/>); a flat shape commits what its observer staged during this packet's parse
/// (<see cref="ColumnarPacketBatch.TryCommit"/>); a flat shape that receives a tree packet it did not observe (the library user did
/// not honor the parse demand, or another consumer needed the tree) walks that tree through the same collector rules first
/// (<see cref="ExportFieldCollector.FeedTree"/>). Both flat feeds share every rule of the collector, which is what keeps the values
/// identical whichever way the fields arrive.
/// </para>
/// <para><b>Thread safety:</b> stateless; the caller's thread is the parse thread.</para>
/// </remarks>
/// <seealso cref="ColumnarExportCore{TBatch}"/>
internal static class ColumnarFeed
{
    #region Feeding a packet

    /// <summary>Adds (hierarchy) or commits (flat shapes) one packet.</summary>
    /// <param name="batch">The batch the exporter is currently filling.</param>
    /// <param name="collector">The exporter's collector, or <see langword="null"/> for the hierarchy shape.</param>
    /// <param name="packet">The packet the exporter was asked to write.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the packet is part of the batch; the batch is unchanged otherwise.</returns>
    internal static bool TryAdd(
        ColumnarPacketBatch batch, ExportFieldCollector? collector, Packet packet, out ExportErrorKind kind, out string? message)
    {
        try
        {
            if (collector is null)
            {
                // All-or-nothing: a throwing AddPacket leaves the batch as it was, so only this packet is skipped.
                batch.AddPacket(packet);
                kind = default;
                message = null;
                return true;
            }

            // A packet whose fields were staged during its own parse has the staged id; any other packet with a tree is fed now.
            // A packet with neither is refused by TryCommit with the message that names the cause.
            if (batch.StagedPacketId != packet.Id.Value && packet.HasFieldTree)
            {
                collector.FeedTree(packet);
            }

            return batch.TryCommit(packet, out kind, out message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Expected failure at the boundary of the batch (a value that does not fit its column, a faulty text): reported as an
            // error event by the exporter, not thrown through the caller's loop.
            kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            message = ex.Message;
            return false;
        }
    }

    #endregion
}
