// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Context-aware PDU boundary detector for stream reassembly.
/// Extends <see cref="IPduBoundaryDetector"/> with stream context and per-stream state reset.
/// <para>
/// <b>Lifecycle contract:</b>
/// <list type="number">
///   <item>
///     The owner calls <see cref="Detect"/> repeatedly with the same
///     <see cref="StreamDetectionContext.StreamId"/> until either a complete PDU is
///     reported (consume the prefix and re-call) or <see cref="PduBoundaryResult.Invalid"/>
///     is reported (engage a resync heuristic).
///   </item>
///   <item>
///     <see cref="ResetStream"/> MUST be called whenever the owner discards or rebuilds the
///     stream state for a given <c>streamId</c> (for example after a successful resync, a
///     protocol upgrade, or a stream teardown). Implementations MUST treat <c>ResetStream</c>
///     as idempotent — calling it for an unknown id is a no-op.
///   </item>
///   <item>
///     Implementations MAY assume that <see cref="Detect"/> for a given <c>streamId</c> is
///     called from a single thread at a time. Callers that interleave streams across threads
///     MUST provide their own synchronization around the detector instance.
///   </item>
/// </list>
/// </para>
/// </summary>
public interface IStreamPduBoundaryDetector : IPduBoundaryDetector
{
    #region Methods

    /// <summary>
    /// Attempts to find a complete PDU using stream context (e.g., stream ID, handshake status).
    /// </summary>
    /// <param name="data">The buffered stream data to inspect.</param>
    /// <param name="context">Stream detection context with identity and protocol metadata.</param>
    /// <returns>
    /// <see cref="PduBoundaryResult.Complete"/> with the PDU length,
    /// <see cref="PduBoundaryResult.Incomplete"/> if more data is needed, or
    /// <see cref="PduBoundaryResult.Invalid"/> if the data is corrupt and resync is needed.
    /// </returns>
    PduBoundaryResult Detect(ReadOnlySpan<byte> data, in StreamDetectionContext context);

    /// <summary>
    /// Resets per-stream detector state (e.g., after protocol upgrade or stream teardown).
    /// </summary>
    /// <param name="streamId">The stream to reset state for.</param>
    void ResetStream(ulong streamId);

    #endregion
}