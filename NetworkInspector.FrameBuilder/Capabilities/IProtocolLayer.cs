// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Base interface for every protocol layer used in the cons-list frame stack.
/// </summary>
/// <remarks>
/// <para>
/// Layers are <c>readonly struct</c>s.  They expose their fixed
/// <see cref="HeaderSize"/> (in bytes) and a single
/// <see cref="ApplyPostFix"/> entry point that the cons-list walks for every
/// <see cref="FixPhase"/>.  Layers that have nothing to do for a given phase
/// must return immediately to keep the call cheap.
/// </para>
/// <para>
/// Layers implement either <see cref="IStatelessLayer"/> (no cross-frame
/// state) or <see cref="IStatefulLayer"/> (carries per-session state
/// like an IP-Identification counter or a TCP sequence number) — never both.
/// </para>
/// </remarks>
public interface IProtocolLayer
{
    /// <summary>Size of this layer's header in bytes.  Constant per instance.</summary>
    int HeaderSize
    {
        get;
    }

    /// <summary>
    /// Applies a post-fix step to this layer's bytes.  Called once per
    /// <see cref="FixPhase"/> per layer; the layer no-ops phases it does not
    /// participate in.
    /// </summary>
    /// <param name="phase">The post-fix phase being processed.</param>
    /// <param name="frame">The complete frame (already fully written).</param>
    /// <param name="myOffset">Offset where this layer's header starts.</param>
    /// <param name="myLength">
    /// Number of bytes from <paramref name="myOffset"/> to the end of the
    /// frame (this layer's header + everything written after it).
    /// </param>
    /// <param name="ctx">Mutable cross-layer post-fix context.</param>
    void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx);
}

