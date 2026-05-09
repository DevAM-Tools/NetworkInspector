// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Delimiter-based PDU boundary detection.
/// Finds a specific byte sequence that terminates a PDU.
/// </summary>
/// <remarks>Creates a delimiter-based PDU boundary detector.</remarks>
/// <exception cref="ArgumentException">Thrown when <c>delimiter</c> is null or empty.</exception>
public sealed class DelimiterDetector : IPduBoundaryDetector
{
    #region Fields

    private readonly byte[] _Delimiter;

    #endregion

    #region Constructors

    /// <summary>Creates a delimiter-based PDU boundary detector.</summary>
    public DelimiterDetector(byte[] delimiter)
    {
        ArgumentNullException.ThrowIfNull(delimiter);
        if (delimiter.Length == 0)
        {
            throw new ArgumentException("Delimiter must not be empty.", nameof(delimiter));
        }
        _Delimiter = delimiter;
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        int idx = data.IndexOf(_Delimiter.AsSpan());
        return idx >= 0
            ? PduBoundaryResult.Complete(idx + _Delimiter.Length)
            : PduBoundaryResult.Incomplete;
    }

    #endregion
}
