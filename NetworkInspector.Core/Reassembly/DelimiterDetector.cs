// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Delimiter-based PDU boundary detection.
/// Finds a specific byte sequence that terminates a PDU; the PDU includes the delimiter.
/// <para>
/// The detector enforces no size limit of its own: it only scans the bytes the owner has buffered, so the size of a
/// PDU is bounded by the stream reassembly limits (<see cref="StreamReassemblyConfig.MaxPduSize"/> and
/// <see cref="StreamReassemblyConfig.MaxBufferSize"/>).
/// </para>
/// <para><b>Thread-safety:</b> Immutable after construction; safe to share across threads.</para>
/// </summary>
public sealed class DelimiterDetector : IPduBoundaryDetector
{
    #region Fields

    private readonly byte[] _Delimiter;

    #endregion

    #region Constructors

    /// <summary>Creates a delimiter-based PDU boundary detector. The array is copied defensively.</summary>
    /// <param name="delimiter">The byte sequence that terminates a PDU.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="delimiter"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="delimiter"/> is empty.</exception>
    public DelimiterDetector(byte[] delimiter)
    {
        ArgumentNullException.ThrowIfNull(delimiter);

        // An empty delimiter would match at index 0 and report a zero-length PDU forever; the reassembly treats a
        // zero-length PDU as an error, so the configuration is rejected up front instead.
        if (delimiter.Length == 0)
        {
            throw new ArgumentException("Delimiter must not be empty.", nameof(delimiter));
        }

        // Defensive copy: later mutation of the caller's array must not change a detector that may be shared.
        _Delimiter = (byte[])delimiter.Clone();
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        // Vectorized span search over the buffered bytes, no allocation. The scan restarts at the buffer start on every
        // call; the owner consumes a completed PDU before it calls again. A delimiter split across two segments is
        // found once the later segment arrives (Incomplete until then).
        int idx = data.IndexOf(_Delimiter.AsSpan());
        return idx >= 0
            ? PduBoundaryResult.Complete(idx + _Delimiter.Length)
            : PduBoundaryResult.Incomplete;
    }

    #endregion
}
