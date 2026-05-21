// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker interface for cons-lists whose every component is a
/// <see cref="IStatelessLayer"/>.  Carried along the cons-list so the
/// stateless-only <c>CreatedStack&lt;TStack,…&gt;.Build(...)</c> overloads can be statically
/// constrained against it.  Adds the stateless-write walk on top of
/// <see cref="IStackNode"/>.
/// </summary>
public interface IStatelessStack : IStackNode
{
    /// <summary>
    /// Writes every layer's header into <paramref name="dst"/> in outer→inner
    /// order and records each header's start offset into
    /// <paramref name="offsets"/> (outer-most at index 0).
    /// </summary>
    /// <typeparam name="TInterceptor">
    /// Concrete interceptor type — generic so the JIT can specialise and
    /// eliminate the call entirely for <see cref="NoInterceptor"/>.
    /// </typeparam>
    /// <param name="dst">Destination buffer; must be at least <see cref="IStackNode.TotalHeaderSize"/> bytes.</param>
    /// <param name="offset">Offset within <paramref name="dst"/> where the outermost layer is written.</param>
    /// <param name="offsets">Output: per-layer header start offsets, outer→inner.</param>
    /// <param name="interceptor">Interceptor invoked once per layer after its header is written.</param>
    /// <returns>The offset just after the innermost layer's header.</returns>
    int WriteHeaders<TInterceptor>(
        scoped Span<byte> dst,
        int offset,
        scoped Span<int> offsets,
        scoped ref TInterceptor interceptor)
        where TInterceptor : struct, IFrameInterceptor;
}
