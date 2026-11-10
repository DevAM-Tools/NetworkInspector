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
/// <para>
/// A signed length with sentinels keeps the result a 4-byte value (no allocation per detection, no extra flag).
/// Caveat: <c>default(PduBoundaryResult)</c> has length 0 and therefore reads as <see cref="IsComplete"/> with a
/// zero-length PDU, which the stream buffer rejects as an error; detectors must never return <c>default</c>.
/// </para>
/// </summary>
public readonly record struct PduBoundaryResult
{
    /// <summary>Sentinel value indicating incomplete data (need more data).</summary>
    private const int _IncompleteSentinel = -1;

    /// <summary>Sentinel value indicating invalid data (trigger resync).</summary>
    private const int _InvalidSentinel = -2;

    /// <summary>The PDU length when complete, or a reserved sentinel otherwise.</summary>
    public int Length { get; }

    /// <summary>Whether the data contains a complete PDU.</summary>
    public bool IsComplete => Length >= 0;

    /// <summary>Whether the data is incomplete (need more data).</summary>
    public bool IsIncomplete => Length == _IncompleteSentinel;

    /// <summary>Whether the data is invalid (resync needed).</summary>
    public bool IsInvalid => Length == _InvalidSentinel;

    /// <summary>Creates a result from a raw length or sentinel; only the factories call it, with a checked value.</summary>
    /// <param name="length">A PDU length of at least 0, or one of the two negative sentinels.</param>
    private PduBoundaryResult(int length) => Length = length;

    /// <summary>PDU boundary not yet determined — need more data.</summary>
    public static PduBoundaryResult Incomplete => new(_IncompleteSentinel);

    /// <summary>Data is invalid at current position — trigger resynchronization.</summary>
    public static PduBoundaryResult Invalid => new(_InvalidSentinel);

    /// <summary>A complete PDU of the given length was found.</summary>
    /// <param name="length">PDU length in bytes, including delimiters or headers the PDU contains; not negative.</param>
    /// <returns>A complete result that carries <paramref name="length"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative.</exception>
    public static PduBoundaryResult Complete(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(length);
    }
}

/// <summary>
/// Detects PDU (Protocol Data Unit) boundaries in a byte stream.
/// The stream buffer calls <see cref="Detect"/> on the contiguous in-order prefix of the stream and consumes a complete
/// PDU before it calls again; implementations must not keep the span after returning.
/// </summary>
public interface IPduBoundaryDetector
{
    /// <summary>Attempts to find a complete PDU in the provided data.</summary>
    /// <param name="data">The contiguous buffered bytes, starting at the first byte not yet consumed.</param>
    /// <returns>
    /// Complete with the PDU length (which must be positive and at most the data length), Incomplete when more bytes are
    /// needed, or Invalid when the data at the start cannot begin a PDU and a resync heuristic should run.
    /// </returns>
    PduBoundaryResult Detect(ReadOnlySpan<byte> data);
}
