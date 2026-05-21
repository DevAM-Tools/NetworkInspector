// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Static fluent entry point for composing protocol layers into a typed
/// cons-list and finalising it as a <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>Typical usage:</para>
/// <code>
/// var stack = FrameStack
///     .Start(new EthernetLayer(dstMac, srcMac))
///     .Then(new IPv4Layer(srcIp, dstIp))
///     .Then(new UdpLayer(srcPort, dstPort))
///     .CreateWithFixedValues();
/// </code>
/// </remarks>
public static partial class FrameStack
{
    /// <summary>
    /// Begins a fluent build with a stateless root-layer.  Returns a single-element
    /// cons-list containing only <paramref name="link"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatelessStack<TLink, StackEnd> Start<TLink>(in TLink link)
        where TLink : struct, IStatelessLayer, IRootLayer
        => new(in link, default);

    /// <summary>
    /// Alias of <see cref="Start{TLink}(in TLink)"/> for the value-reuse pattern
    /// (concept §3.3 / §11.4): build a template once with placeholder values, then
    /// supply concrete values per <c>Build(in values, payload)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatelessStack<TLink, StackEnd> Values<TLink>(in TLink link)
        where TLink : struct, IStatelessLayer, IRootLayer
        => new(in link, default);

    /// <summary>
    /// Finalises a stateless cons-list into a <see cref="CreatedStack{TStack,TTrailer,TInterceptor}"/>
    /// with no trailer and no interceptor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CreatedStack<TStack, NoTrailer, NoInterceptor> CreateWithFixedValues<TStack>(in TStack stack)
        where TStack : struct, IStackNode, IStatelessStack
        => new(in stack, default, default);

    /// <summary>
    /// Finalises a stateless cons-list with an explicit interceptor instance and no trailer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CreatedStack<TStack, NoTrailer, TInterceptor> CreateWithFixedValues<TStack, TInterceptor>(
        in TStack stack,
        in TInterceptor interceptor)
        where TStack : struct, IStackNode, IStatelessStack
        where TInterceptor : struct, IFrameInterceptor
        => new(in stack, default, in interceptor);
}
