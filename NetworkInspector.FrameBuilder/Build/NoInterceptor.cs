// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Default no-op interceptor.  Empty struct: every method body is empty so
/// the JIT eliminates the calls in specialised generic code.
/// </summary>
public readonly struct NoInterceptor : IFrameInterceptor
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
        where TLayer : struct, IProtocolLayer
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnFrameComplete(scoped Span<byte> frame)
    {
    }
}
