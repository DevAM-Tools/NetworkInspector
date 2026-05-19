// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
public readonly struct PduBoundaryResult
{
    #region Constants

    /// <summary>Sentinel value indicating incomplete data (need more data).</summary>
    private const int IncompleteSentinel = -1;

    /// <summary>Sentinel value indicating invalid data (trigger resync).</summary>
    private const int InvalidSentinel = -2;

    #endregion

    #region Properties

    /// <summary>
    /// Length of the complete PDU when <see cref="IsComplete"/> is <c>true</c>.
    /// Otherwise carries an internal sentinel value — callers MUST inspect
    /// <see cref="IsComplete"/>/<see cref="IsIncomplete"/>/<see cref="IsInvalid"/>
    /// rather than comparing this value directly.
    /// </summary>
    public int Length
    {
        get; init;
    }

    /// <summary>Whether the data contains a complete PDU.</summary>
    public bool IsComplete => Length >= 0;

    /// <summary>Whether the data is incomplete (need more data).</summary>
    public bool IsIncomplete => Length == IncompleteSentinel;

    /// <summary>Whether the data is invalid (resync needed).</summary>
    public bool IsInvalid => Length == InvalidSentinel;

    #endregion

    #region Factory Methods

    /// <summary>PDU boundary not yet determined — need more data.</summary>
    public static PduBoundaryResult Incomplete => new() { Length = IncompleteSentinel };

    /// <summary>Data is invalid at current position — trigger resynchronization.</summary>
    public static PduBoundaryResult Invalid => new() { Length = InvalidSentinel };

    /// <summary>A complete PDU of the given length was found.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative.</exception>
    public static PduBoundaryResult Complete(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new()
        {
            Length = length
        };
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