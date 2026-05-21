// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Optional per-frame interceptor invoked once after every frame has been
/// fully written and post-fixed.
/// </summary>
/// <remarks>
/// <para>
/// Implement as a <c>readonly struct</c> for zero-overhead specialisation:
/// when the type is an empty struct (such as <see cref="NoInterceptor"/>),
/// the JIT eliminates the call entirely.
/// </para>
/// <para>
/// <see cref="OnHeaderWritten{TLayer}"/> receives the typed layer value
/// directly so callers can pattern-match on <c>typeof(TLayer)</c> — that
/// comparison is a JIT-time constant inside the specialised code, so the
/// non-matching branches are dropped as dead code.
/// </para>
/// </remarks>
public interface IFrameInterceptor
{
    /// <summary>
    /// Called immediately after a layer's header has been written, before any
    /// post-fix runs.  The <paramref name="headerSlice"/> is exactly
    /// <c>layer.HeaderSize</c> bytes long.
    /// </summary>
    void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
        where TLayer : struct, IProtocolLayer;

    /// <summary>Called once after the whole frame is finalised.</summary>
    void OnFrameComplete(scoped Span<byte> frame);
}
