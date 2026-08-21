// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Result of PDU boundary detection.
/// <para>
/// <b>Important — do not compare <see cref="Length"/> directly against integers.</b>
/// Negative values are reserved sentinels for the <see cref="IsIncomplete"/> and
/// <see cref="IsInvalid"/> states. Always use the <see cref="IsComplete"/>,
/// <see cref="IsIncomplete"/>, and <see cref="IsInvalid"/> properties to discriminate
/// states, or build instances exclusively via <see cref="Complete"/>,
/// <see cref="Incomplete"/>, and <see cref="Invalid"/>.
/// </para>
/// </summary>
public readonly record struct PduBoundaryResult
{
    #region Constants

    /// <summary>Sentinel value indicating incomplete data (need more data).</summary>
    private const int _IncompleteSentinel = -1;

    /// <summary>Sentinel value indicating invalid data (trigger resync).</summary>
    private const int _InvalidSentinel = -2;

    #endregion

    #region Properties

    /// <summary>The PDU length when complete, or a reserved sentinel otherwise.</summary>
    public int Length { get; }

    /// <summary>Whether the data contains a complete PDU.</summary>
    public bool IsComplete => Length >= 0;

    /// <summary>Whether the data is incomplete (need more data).</summary>
    public bool IsIncomplete => Length == _IncompleteSentinel;

    /// <summary>Whether the data is invalid (resync needed).</summary>
    public bool IsInvalid => Length == _InvalidSentinel;

    #endregion

    #region Constructors

    private PduBoundaryResult(int length) => Length = length;

    #endregion

    #region Factory Methods

    /// <summary>PDU boundary not yet determined — need more data.</summary>
    public static PduBoundaryResult Incomplete => new(_IncompleteSentinel);

    /// <summary>Data is invalid at current position — trigger resynchronization.</summary>
    public static PduBoundaryResult Invalid => new(_InvalidSentinel);

    /// <summary>A complete PDU of the given length was found.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative.</exception>
    public static PduBoundaryResult Complete(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(length);
    }

    #endregion
}

/// <summary>
/// Detects PDU (Protocol Data Unit) boundaries in a byte stream.
/// </summary>
public interface IPduBoundaryDetector
{
    /// <summary>Attempts to find a complete PDU in the provided data.</summary>
    PduBoundaryResult Detect(ReadOnlySpan<byte> data);
}
