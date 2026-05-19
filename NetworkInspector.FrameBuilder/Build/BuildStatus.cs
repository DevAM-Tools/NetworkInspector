// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Outcome of a frame-build operation. Surfaced via
/// <see cref="FrameSequence{TStack,TTrailer,TInterceptor}.Status"/> so the
/// caller can distinguish success from expected runtime situations
/// <em>without</em> using exceptions in the hot path.
/// </summary>
public enum BuildStatus : byte
{
    /// <summary>The frame was built successfully.</summary>
    Success = 0,

    /// <summary>The provided destination buffer is too small to hold a single frame.</summary>
    BufferTooSmall = 1,

    /// <summary>
    /// The payload (with all layer headers) does not fit into one frame and the
    /// stack contains no <see cref="IFragmentable"/> layer to split it.
    /// </summary>
    FragmentationRequired = 2,

    /// <summary>
    /// The fragmentation scratch could not be acquired or the active
    /// fragmentable layer reported a non-power-of-two / negative alignment.
    /// Indicates an internal layer state that the dispatcher considers invalid;
    /// no bytes are written.
    /// </summary>
    InvalidLayerState = 3,

    /// <summary>
    /// The cons-list depth exceeds the static stackalloc budget.  No
    /// frame is written; raising the budget requires a library change.
    /// </summary>
    StackTooDeep = 4,
}
