// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Configuration for stream reassembly.
/// <para>
/// <b>Thread-safety:</b> Configuration is exposed through <c>get; init;</c> properties — all
/// fields are populated during initialization and are immutable thereafter. Instances are
/// therefore safe to share across any number of threads after construction.
/// </para>
/// </summary>
public sealed class StreamReassemblyConfig
{
    #region Fields

    private readonly int _MaxPduSize = 65536;

    private readonly int _MaxBufferSize = 1048576; // 1 MiB

    #endregion

    #region Properties

    /// <summary>
    /// Maximum PDU size in bytes (default 64 KiB).
    /// Limit record: threat is a boundary detector (driven by untrusted stream bytes) that announces a huge PDU.
    /// Enforced by the stream's segment buffer: a complete PDU longer than this moves the stream to its error state
    /// instead of being copied out. Configurable by the code that registers the stream (initializer, range: positive
    /// int); an assigned value of zero or less is rejected. Posture: secure by default (small default, raising it is
    /// explicit). The default value itself has no recorded derivation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is zero or negative.</exception>
    public int MaxPduSize
    {
        get => _MaxPduSize;
        init
        {
            // A zero or negative limit would make every PDU "too large" and the stream would fail on its first segment, so a
            // mistaken configuration is rejected where it is written and not discovered later as a stream error.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _MaxPduSize = value;
        }
    }

    /// <summary>
    /// Maximum buffer size per direction in bytes (default 1 MiB).
    /// Limit record: threat is memory exhaustion by a peer that never completes a PDU or leaves holes that hold later
    /// segments. Enforced by <see cref="SequenceStreamBuffer"/> on the sum of the contiguous prefix and the held
    /// segments; an append beyond it is refused and the stream goes to its error state, never truncated silently.
    /// Configurable by the code that registers the stream (initializer, range: positive int, and at least
    /// <see cref="MaxPduSize"/> for a maximal PDU to be able to complete; that relation is not checked here, because the two
    /// initializers run in any order); an assigned value of zero or less is rejected. Posture: secure by default.
    /// The default value itself has no recorded derivation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is zero or negative.</exception>
    public int MaxBufferSize
    {
        get => _MaxBufferSize;
        init
        {
            // Same reason as MaxPduSize: with zero or less every append would be refused.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _MaxBufferSize = value;
        }
    }

    /// <summary>PDU boundary detector.</summary>
    public IPduBoundaryDetector? BoundaryDetector
    {
        get; init;
    }

    /// <summary>Optional resynchronization heuristic.</summary>
    public IResyncHeuristic? ResyncHeuristic
    {
        get; init;
    }

    /// <summary>
    /// When <see langword="true"/>, segment data is copied into a separate buffer.
    /// Required for live-capture sources where the underlying packet buffer may be recycled.
    /// File-based sources can leave this <see langword="false"/> for zero-copy operation.
    /// The flag only affects segments that are held back behind a hole (those keep a reference to the caller's memory
    /// when it is false); bytes that extend the in-order prefix are always copied into the contiguous array.
    /// </summary>
    public bool CopySegments
    {
        get; init;
    }

    #endregion
}
