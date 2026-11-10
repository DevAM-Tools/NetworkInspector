// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Configuration for stream reassembly.
/// <para>
/// <b>Limits:</b> a size that the registering code does not assign reads as the default of its limit
/// (<see cref="CoreLimits.MaxStreamPduSize"/>, <see cref="CoreLimits.MaxStreamBufferSize"/>,
/// <see cref="CoreLimits.MaxStreamRebases"/>), and <see cref="WithLimits"/> replaces such an unassigned size by the live value
/// of the setting, so a user changes the default of every stream that has no value of its own. A size that the registering code
/// assigns is that protocol's own choice and stays.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Configuration is exposed through <c>get; init;</c> properties — all
/// fields are populated during initialization and are immutable thereafter. Instances are
/// therefore safe to share across any number of threads after construction.
/// </para>
/// </summary>
public sealed class StreamReassemblyConfig
{
    /// <summary>Backing store of <see cref="MaxPduSize"/>; <see langword="null"/> until an initializer assigns a positive value.</summary>
    private readonly int? _MaxPduSize;

    /// <summary>Backing store of <see cref="MaxBufferSize"/>; <see langword="null"/> until an initializer assigns a positive value.</summary>
    private readonly int? _MaxBufferSize;

    /// <summary>Backing store of <see cref="MaxRebases"/>; <see langword="null"/> until an initializer assigns a positive value.</summary>
    private readonly int? _MaxRebases;

    /// <summary>
    /// Maximum PDU size in bytes. Unassigned: the default of <see cref="CoreLimits.MaxStreamPduSize"/> (64 KiB, the largest DNS
    /// message over TCP with its length prefix).
    /// Limit record: threat is a boundary detector (driven by untrusted stream bytes) that announces a huge PDU.
    /// Enforced by the stream's segment buffer: a complete PDU longer than this moves the stream to its error state
    /// instead of being copied out. Configurable by the code that registers the stream (initializer, range: positive
    /// int; a user changes the unassigned default with the setting <c>reassembly.maxStreamPduSize</c>); an assigned value of
    /// zero or less is rejected. Posture: secure by default (small default, raising it is explicit).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is zero or negative.</exception>
    public int MaxPduSize
    {
        get => _MaxPduSize
            ?? (int)CoreLimits.MaxStreamPduSize.Default;
        init
        {
            // A zero or negative limit would make every PDU "too large" and the stream would fail on its first segment, so a
            // mistaken configuration is rejected where it is written and not discovered later as a stream error.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _MaxPduSize = value;
        }
    }

    /// <summary>
    /// Maximum buffer size per direction in bytes. Unassigned: the default of <see cref="CoreLimits.MaxStreamBufferSize"/>
    /// (1 MiB, 16 times the default PDU size).
    /// Limit record: threat is memory exhaustion by a peer that never completes a PDU or leaves holes that hold later
    /// segments. Enforced by <see cref="SequenceStreamBuffer"/> on the sum of the contiguous prefix and the held
    /// segments; an append beyond it is refused and the stream goes to its error state, never truncated silently.
    /// Configurable by the code that registers the stream (initializer, range: positive int, and at least
    /// <see cref="MaxPduSize"/> for a maximal PDU to be able to complete; that relation is not checked here, because the two
    /// initializers run in any order, but <see cref="StackBuilder.RegisterStreamReassemblyConfig"/> refuses a pair that
    /// breaks it); an assigned value of zero or less is rejected. A user changes the unassigned default with the setting
    /// <c>reassembly.maxStreamBufferSize</c>. Posture: secure by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is zero or negative.</exception>
    public int MaxBufferSize
    {
        get => _MaxBufferSize
            ?? (int)CoreLimits.MaxStreamBufferSize.Default;
        init
        {
            // Same reason as MaxPduSize: with zero or less every append would be refused.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _MaxBufferSize = value;
        }
    }

    /// <summary>
    /// The most times one direction rebuilds its buffer because a segment starts before the first byte seen. Unassigned: the
    /// default of <see cref="CoreLimits.MaxStreamRebases"/> (64). Threat and posture: see that limit; a user changes the
    /// unassigned default with the setting <c>reassembly.maxStreamRebases</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is zero or negative.</exception>
    public int MaxRebases
    {
        get => _MaxRebases
            ?? (int)CoreLimits.MaxStreamRebases.Default;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _MaxRebases = value;
        }
    }

    /// <summary>PDU boundary detector; <see langword="null"/> puts the stream in its error state at the first segment.</summary>
    public IPduBoundaryDetector? BoundaryDetector
    {
        get; init;
    }

    /// <summary>Optional resynchronization heuristic; <see langword="null"/> means the stream does not resynchronize.</summary>
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

    /// <summary>
    /// Returns a configuration in which every size that this one leaves unassigned is the current value of its limit. An assigned
    /// size, the detector, the heuristic, and <see cref="CopySegments"/> are copied unchanged. A limit that is switched off reads as
    /// the largest <see cref="int"/>.
    /// </summary>
    /// <param name="limits">The limits of the stack (<see cref="Stack.Limits"/>).</param>
    /// <returns>A new configuration; this instance is not changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
    /// <remarks>Cost: one small allocation; call it when a stream is created, not per segment.</remarks>
    public StreamReassemblyConfig WithLimits(CoreLimitSet limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new StreamReassemblyConfig
        {
            BoundaryDetector = BoundaryDetector,
            ResyncHeuristic = ResyncHeuristic,
            CopySegments = CopySegments,
            MaxPduSize = _MaxPduSize ?? CoreLimitSet.IntCeiling(limits.MaxStreamPduSize),
            MaxBufferSize = _MaxBufferSize ?? CoreLimitSet.IntCeiling(limits.MaxStreamBufferSize),
            MaxRebases = _MaxRebases ?? CoreLimitSet.IntCeiling(limits.MaxStreamRebases),
        };
    }
}
