// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

#region Composition rules
// ------------------------------------------------------------------
// Capability-typed `Then(...)` composition surface.
//
// The 6 overloads below cover every legal layer stacking.  Two structural
// dimensions combine:
//
//   Stack-shape transition (3):
//     A. StatelessStack + stateless  TNew  ⇒ StatelessStack
//     B. StatelessStack + stateful   TNew  ⇒ Stack (mixed; only StatefulFrameStack
//                                            finalisers apply from here on)
//     C. Stack          + any        TNew  ⇒ Stack
//
//   Pseudo-header pairing (2 mutually-exclusive shapes per transition):
//     "Loose"  — TNew : IPseudoHeaderIndependent.  No additional constraint
//                on the outer.
//     "Strict" — TNew : IRequiresPseudoHeader; TOld : IProvidesPseudoHeader.
//                Enforces at compile time that a checksum-bearing transport
//                (TCP, UDP, ICMPv6) sits on a network-layer that publishes
//                a pseudo-header.
//
// In every overload the outer (TOld) must implement IInteriorLayer; this
// prevents stacking anything onto a terminal payload layer (IPayloadLayer)
// such as SOME/IP.
//
// The next-protocol kind discriminators (EtherTypeKind, IpNextProtocolKind)
// are NOT enforced here.  They live on the IProvidesNextProtocolValue<TKind>
// / IConsumesNextProtocolValue<TKind> markers as namespace documentation
// and as runtime hints for auto-patch dispatch.  A user that stacks across
// namespaces (e.g. IP-in-IP) must pin the relevant next-protocol field
// explicitly because the auto-patch silently writes the inner layer's
// value into whatever slot the outer happens to own.
//
// Implementation note: each overload lives in its OWN static class.  C#
// disallows two methods in the same type sharing a parameter signature
// even when their generic constraints differ (CS0111).  Splitting them
// across distinct extension classes lets the compiler resolve the unique
// applicable overload at the call site through the standard extension-method
// lookup (each class is searched in turn; only the matching one is picked).
// ------------------------------------------------------------------
#endregion

#region A. StatelessStack -> StatelessStack (stateless TNew)

/// <summary>Loose <c>Then(...)</c>: stateless layer without pseudo-header dependency onto a stateless cons-list.</summary>
public static class StatelessThenStatelessExtensions
{
    /// <summary>Appends a stateless, pseudo-header-independent layer onto a stateless cons-list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatelessStack<TNew, StatelessStack<TOld, TTail>> Then<TNew, TOld, TTail>(
        this in StatelessStack<TOld, TTail> prev,
        in TNew next)
        where TNew : struct, IStatelessLayer, IPseudoHeaderIndependent
        where TOld : struct, IStatelessLayer, IInteriorLayer
        where TTail : struct, IStackNode, IStatelessStack
        => new(in next, in prev);
}

/// <summary>Strict <c>Then(...)</c>: stateless transport-class layer (pseudo-header dependent) onto a stateless network cons-list.</summary>
public static class StatelessThenStatelessPseudoHeaderExtensions
{
    /// <summary>Appends a stateless, pseudo-header-requiring layer onto a stateless network cons-list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StatelessStack<TNew, StatelessStack<TOld, TTail>> Then<TNew, TOld, TTail>(
        this in StatelessStack<TOld, TTail> prev,
        in TNew next)
        where TNew : struct, IStatelessLayer, IRequiresPseudoHeader
        where TOld : struct, IStatelessLayer, IInteriorLayer, IProvidesPseudoHeader
        where TTail : struct, IStackNode, IStatelessStack
        => new(in next, in prev);
}

#endregion

#region B. StatelessStack -> Stack (stateful TNew, switches to mixed shape)

/// <summary>Loose <c>Then(...)</c>: stateful layer without pseudo-header dependency onto a stateless cons-list.</summary>
public static class StatelessThenStatefulExtensions
{
    /// <summary>Appends a stateful, pseudo-header-independent layer onto a stateless cons-list (switches to mixed shape).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stack<TNew, StatelessStack<TOld, TTail>> Then<TNew, TOld, TTail>(
        this in StatelessStack<TOld, TTail> prev,
        in TNew next)
        where TNew : struct, IStatefulLayer, IPseudoHeaderIndependent
        where TOld : struct, IStatelessLayer, IInteriorLayer
        where TTail : struct, IStackNode, IStatelessStack
        => new(in next, in prev);
}

/// <summary>Strict <c>Then(...)</c>: stateful transport-class layer (pseudo-header dependent) onto a stateless network cons-list.</summary>
public static class StatelessThenStatefulPseudoHeaderExtensions
{
    /// <summary>Appends a stateful, pseudo-header-requiring layer onto a stateless network cons-list (switches to mixed shape).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stack<TNew, StatelessStack<TOld, TTail>> Then<TNew, TOld, TTail>(
        this in StatelessStack<TOld, TTail> prev,
        in TNew next)
        where TNew : struct, IStatefulLayer, IRequiresPseudoHeader
        where TOld : struct, IStatelessLayer, IInteriorLayer, IProvidesPseudoHeader
        where TTail : struct, IStackNode, IStatelessStack
        => new(in next, in prev);
}

#endregion

#region C. Stack -> Stack (mixed shape continues)

/// <summary>Loose <c>Then(...)</c>: any layer without pseudo-header dependency onto a mixed cons-list.</summary>
public static class StackThenAnyExtensions
{
    /// <summary>Appends any pseudo-header-independent layer onto a mixed cons-list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stack<TNew, Stack<TOld, TInner>> Then<TNew, TOld, TInner>(
        this in Stack<TOld, TInner> prev,
        in TNew next)
        where TNew : struct, IProtocolLayer, IPseudoHeaderIndependent
        where TOld : struct, IProtocolLayer, IInteriorLayer
        where TInner : struct, IStackNode
        => new(in next, in prev);
}

/// <summary>Strict <c>Then(...)</c>: any transport-class layer (pseudo-header dependent) onto a mixed network cons-list.</summary>
public static class StackThenAnyPseudoHeaderExtensions
{
    /// <summary>Appends any pseudo-header-requiring layer onto a mixed network cons-list.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stack<TNew, Stack<TOld, TInner>> Then<TNew, TOld, TInner>(
        this in Stack<TOld, TInner> prev,
        in TNew next)
        where TNew : struct, IProtocolLayer, IRequiresPseudoHeader
        where TOld : struct, IProtocolLayer, IInteriorLayer, IProvidesPseudoHeader
        where TInner : struct, IStackNode
        => new(in next, in prev);
}

#endregion



#region Trailer attachment (orthogonal)

/// <summary>
/// Extensions to attach an <see cref="ITrailerLayer"/> trailer to a stateless
/// cons-list (concept §4.4 / §11.2).  Orthogonal to the <c>Then(...)</c>
/// composition surface above.
/// </summary>
public static class StatelessStackTrailerExtensions
{
    /// <summary>
    /// Attaches the given trailer to this stateless cons-list and returns an
    /// intermediate <see cref="TrailerStack{TStack,TTrailer}"/> on which
    /// <c>CreateWithFixedValues()</c> can be called.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TrailerStack<StatelessStack<THead, TTail>, TTrailer> WithTrailer<THead, TTail, TTrailer>(
        this StatelessStack<THead, TTail> stack,
        in TTrailer trailer)
        where THead : struct, IStatelessLayer
        where TTail : struct, IStackNode, IStatelessStack
        where TTrailer : struct, ITrailerLayer
        => new(in stack, in trailer);
}

#endregion
