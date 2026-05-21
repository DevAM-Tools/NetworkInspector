// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Intermediate fluent stage produced by <c>WithTrailer(in trailer)</c>.
/// Carries the cons-list plus the trailer; resolves to a final
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/> via <see cref="CreateWithFixedValues"/>.
/// </summary>
/// <typeparam name="TStack">Concrete cons-list shape.</typeparam>
/// <typeparam name="TTrailer">Trailer type.</typeparam>
public readonly ref struct TrailerStack<TStack, TTrailer>
    where TStack : struct, IStackNode, IStatelessStack
    where TTrailer : struct, ITrailerLayer
{
    private readonly TStack _Values;
    private readonly TTrailer _Trailer;

    /// <summary>Creates a new trailer-attached intermediate stack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TrailerStack(scoped in TStack values, scoped in TTrailer trailer)
    {
        _Values = values;
        _Trailer = trailer;
    }

    /// <summary>Finalises with no interceptor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CreatedStack<TStack, TTrailer, NoInterceptor> CreateWithFixedValues()
        => new(in _Values, in _Trailer, default);

    /// <summary>Finalises with an explicit interceptor instance.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CreatedStack<TStack, TTrailer, TInterceptor> CreateWithFixedValues<TInterceptor>(in TInterceptor interceptor)
        where TInterceptor : struct, IFrameInterceptor
        => new(in _Values, in _Trailer, in interceptor);
}
