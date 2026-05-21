// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// A validated, ready-to-use frame stack.  Produced by
/// <see cref="FrameStack.CreateWithFixedValues{TStack}(in TStack)"/> and
/// related entry points; carries the cons-list of fully-configured layers
/// plus an optional interceptor.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Build(System.ReadOnlySpan{byte})"/> is only available when the cons-list is fully stateless
/// (the <c>where TStack : struct, IStatelessStack</c> constraint at the call
/// site makes the method invisible for stacks that contain stateful layers —
/// those must go through a <see cref="StatefulCreatedStack{TStack,TTrailer,TInterceptor}"/>
/// and a <see cref="Session{TStack,TTrailer,TInterceptor}"/>).
/// </para>
/// </remarks>
/// <typeparam name="TStack">Concrete cons-list shape.</typeparam>
/// <typeparam name="TTrailer">
/// Trailer type (use <see cref="NoTrailer"/> for none, <see cref="EthernetFcs"/>
/// for an Ethernet FCS, …).
/// </typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public readonly struct CreatedStack<TStack, TTrailer, TInterceptor>
    where TStack : struct, IStackNode, IStatelessStack
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

    /// <summary>
    /// Smallest MTU asserted along the cons-list, in bytes; <see cref="int.MaxValue"/>
    /// when no layer asserts an MTU.  Use this to size destination buffers.
    /// </summary>
    public int MaxFrameSize => _Values.MaxFrameLength;

    /// <summary>Creates a created stack.  Internal — produced by <see cref="FrameStack"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CreatedStack(in TStack values, in TTrailer trailer, in TInterceptor interceptor)
    {
        _Values = values;
        _Trailer = trailer;
        _Interceptor = interceptor;
    }

    /// <summary>
    /// Starts a build for the given <paramref name="payload"/>.  Returns an
    /// iterator that yields one frame per <see cref="FrameSequence{TStack,TTrailer,TInterceptor}.MoveNext"/>
    /// call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FrameSequence<TStack, TTrailer, TInterceptor> Build(ReadOnlySpan<byte> payload)
        => new(_Values, _Trailer, _Interceptor, payload);

    /// <summary>
    /// Starts a build with caller-supplied values overriding the stored ones.
    /// Enables the value-reuse pattern from new_concept.md §3.3 / §11.4.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FrameSequence<TStack, TTrailer, TInterceptor> Build(in TStack values, ReadOnlySpan<byte> payload)
        => new(values, _Trailer, _Interceptor, payload);
}
