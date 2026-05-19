// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Static finaliser entry points for cons-lists that contain at least one
/// <see cref="IStatefulLayer"/>.  Mirrors
/// <see cref="FrameStack.CreateWithFixedValues{TStack}(in TStack)"/> for the
/// stateful path; produces a <see cref="StatefulCreatedStack{TStack,TTrailer,TInterceptor}"/>
/// whose only emit path is via <see cref="Session{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
public static class StatefulFrameStack
{
    /// <summary>Finalises a mixed stateful/stateless cons-list with no trailer and no interceptor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatefulCreatedStack<TStack, NoTrailer, NoInterceptor> CreateForSession<TStack>(in TStack stack)
        where TStack : struct, IStackNode
        => new(in stack, default, default);

    /// <summary>Finalises a mixed cons-list with an explicit interceptor instance and no trailer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatefulCreatedStack<TStack, NoTrailer, TInterceptor> CreateForSession<TStack, TInterceptor>(
        in TStack stack,
        in TInterceptor interceptor)
        where TStack : struct, IStackNode
        where TInterceptor : struct, IFrameInterceptor
        => new(in stack, default, in interceptor);
}

