// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Convenience interceptor that forwards events to caller-supplied
/// <c>delegate*</c> function pointers (concept §6.3).
/// </summary>
/// <remarks>
/// <para>
/// Allocates nothing and avoids the need for callers to define their own
/// struct just to plug into <see cref="IFrameInterceptor"/>.  The trade-off
/// vs. a custom interceptor: the typed <c>TLayer</c> value is discarded —
/// only the raw header slice is forwarded.
/// </para>
/// <para>
/// Either function pointer may be <c>null</c> to skip that hook entirely.
/// </para>
/// <para>Thread safety: stateless apart from the function pointers; safe to share if the targets are.</para>
/// </remarks>
public readonly unsafe struct DelegateInterceptor : IFrameInterceptor
{
    /// <summary>Callback for every header written; receives the header slice only.</summary>
    private readonly delegate*<Span<byte>, void> _OnHeader;

    /// <summary>Callback when the full frame is finished; receives the entire frame.</summary>
    private readonly delegate*<Span<byte>, void> _OnFrame;

    /// <summary>Creates an interceptor with the given function pointers (either may be <c>null</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal DelegateInterceptor(
        delegate*<Span<byte>, void> onHeader,
        delegate*<Span<byte>, void> onFrame)
    {
        _OnHeader = onHeader;
        _OnFrame = onFrame;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
        where TLayer : struct, IProtocolLayer
    {
        if (_OnHeader != null)
        {
            _OnHeader(headerSlice);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnFrameComplete(scoped Span<byte> frame)
    {
        if (_OnFrame != null)
        {
            _OnFrame(frame);
        }
    }
}
