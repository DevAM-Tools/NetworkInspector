// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Content;

/// <summary>
/// The one rule of how a packet enters a <see cref="RowFieldStage"/> in <c>OnPacket</c>, shared by CSV, JSON, Text, and PBF
/// Standard so the four cannot decide differently (the counterpart of <c>ColumnarFeed</c> for the columnar formats).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why guarded:</b> a tree packet that the exporter's observer did not stage is walked through the collector, which reads the
/// custom text of the selected fields; a text factory of a plugin dissector may throw. An expected per-packet failure must
/// become a skipped packet with a message (Section 4.1 of the repository rules, exporter guide §9.1), not an exception in the
/// caller's loop. The stage is emptied again so the next packet starts clean.
/// </para>
/// <para><b>Thread safety:</b> stateless; the caller's thread is the parse thread.</para>
/// </remarks>
internal static class RowFeed
{
    #region Feeding a packet

    /// <summary>Walks a tree packet nobody staged, then commits the staging of this very packet.</summary>
    /// <param name="stage">The exporter's row stage.</param>
    /// <param name="collector">The exporter's collector, bound to <paramref name="stage"/>.</param>
    /// <param name="packet">The packet the exporter was asked to write.</param>
    /// <param name="kind">The error kind when the method returns <see langword="false"/>.</param>
    /// <param name="message">The reason when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the staging is committed and its entries can be formatted; otherwise the stage is empty again
    /// and <paramref name="message"/> names the cause.
    /// </returns>
    internal static bool TryPrepare(
        RowFieldStage stage, ExportFieldCollector collector, Packet packet, out ExportErrorKind kind, out string? message)
    {
        try
        {
            // A packet whose fields were staged during its own parse has the staged id; any other packet with a tree is fed now.
            // A packet with neither falls through to TryCommit, which refuses it with the cause.
            if (stage.StagedPacketId != packet.Id.Value && packet.HasFieldTree)
            {
                collector.FeedTree(packet);
            }

            return stage.TryCommit(packet, out kind, out message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A faulty text factory or a value the stage cannot hold fails this packet only; nothing of it is written.
            stage.Discard();
            kind = ex is IOException
                ? ExportErrorKind.IoError
                : ExportErrorKind.SerializationError;
            message = Invariant($"Packet {packet.Id.Value}: {ex.Message}");
            return false;
        }
    }

    #endregion
}
