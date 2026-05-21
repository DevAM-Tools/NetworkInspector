// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Validated, ready-to-use frame stack containing at least one
/// <see cref="IStatefulLayer"/>.  Produced by
/// <see cref="StatefulFrameStack.CreateForSession{TStack}(in TStack)"/>
/// and the related entry points; can only be driven through a
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> this type
/// does <em>not</em> expose a <c>Build(...)</c> method — frames produced from a
/// stateful stack must update per-frame state, which is the
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/>'s responsibility.  Trying to
/// emit a stateful stack outside a session is therefore a compile-time error.
/// </para>
/// </remarks>
/// <typeparam name="TStack">Concrete (mixed stateful/stateless) cons-list shape.</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public readonly struct StatefulCreatedStack<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    private readonly TStack _Values;
    private readonly TTrailer _Trailer;
    private readonly TInterceptor _Interceptor;

    /// <summary>Sum of every layer's header size in bytes (no trailer).</summary>
    public int HeaderSize => _Values.TotalHeaderSize;

    /// <summary>Trailer length in bytes (0 for <see cref="NoTrailer"/>).</summary>
    public int TrailerSize => _Trailer.TrailerSize;

    /// <summary>Number of layers in the stack.</summary>
    public int Depth => _Values.Depth;

    /// <summary>Smallest MTU asserted along the cons-list, in bytes.</summary>
    public int MaxFrameSize => _Values.MaxFrameLength;

    /// <summary>Internal — produced by <see cref="StatefulFrameStack"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal StatefulCreatedStack(in TStack values, in TTrailer trailer, in TInterceptor interceptor)
    {
        _Values = values;
        _Trailer = trailer;
        _Interceptor = interceptor;
    }

    /// <summary>
    /// Opens a new <see cref="Session{TStack,TTrailer,TInterceptor}"/> over this stack.
    /// The session owns the per-stack <see cref="SessionState"/>; dispose the
    /// session when it is no longer needed (returns it to the internal pool).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Session<TStack, TTrailer, TInterceptor> OpenSession()
        => Session<TStack, TTrailer, TInterceptor>.Open(in _Values, in _Trailer, in _Interceptor);
}

