// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Allocation-free capability probes for the cons-list walks in
/// <see cref="Stack{THead,TTail}"/> and <see cref="StatelessStack{THead,TTail}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every probe has the same two-step shape and must keep it:
/// </para>
/// <list type="number">
///   <item><c>value is IFoo</c> without a pattern variable.  For a concrete struct
///   <c>TValue</c> the JIT folds the box-and-test into a constant, so the
///   non-matching branch is dead code and no box exists.  Because <c>TValue</c>
///   and <c>IFoo</c> are unrelated type parameters here, CA1508 cannot prove the
///   test constant.</item>
///   <item><c>((IFoo)value).Member</c> for every member access.  The box feeds the
///   interface call directly, so the JIT devirtualises the call to the struct
///   method and drops the box.</item>
/// </list>
/// <para>
/// Never store the interface reference in a local or an <c>out</c> parameter and
/// never route the value through <see cref="object"/> or a
/// <see cref="MethodImplOptions.NoInlining"/> method: the box then outlives the
/// call and becomes a heap allocation per frame.  The default interface members
/// <see cref="IFragmentable.FragmentationKind"/> and <see cref="IFragmentable.FragmentAlignment"/>
/// return constants, which the JIT inlines after devirtualisation; keep such
/// default bodies trivial or implement the member on the struct.
/// <c>AllocationTests</c> in the test project guards the zero-allocation claim
/// for the single-frame, the fragmenting and the session path.
/// </para>
/// </remarks>
internal static class StackHelpers
{
    #region Link and protocol-type capabilities

    /// <summary>Reads <see cref="IProvidesMtu.LinkMtu"/> when <paramref name="value"/> provides one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetLinkMtu<TValue>(in TValue value, out ushort linkMtu)
        where TValue : struct
    {
        if (value is IProvidesMtu)
        {
            linkMtu = ((IProvidesMtu)value).LinkMtu;
            return true;
        }

        linkMtu = 0;
        return false;
    }

    /// <summary>Reads <see cref="IProvidesProtocolType.ProtocolType"/> when <paramref name="value"/> provides one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetProtocolType<TValue>(in TValue value, out ushort protocolType)
        where TValue : struct
    {
        if (value is IProvidesProtocolType)
        {
            protocolType = ((IProvidesProtocolType)value).ProtocolType;
            return true;
        }

        protocolType = 0;
        return false;
    }

    /// <summary>
    /// Calls <see cref="IConsumesNextProtocolValue.PatchNextProtocol"/> when
    /// <paramref name="value"/> owns a next-protocol field; otherwise does nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PatchNextProtocol<TValue>(in TValue value, scoped Span<byte> frame, int myOffset, ushort nextProtocol)
        where TValue : struct
    {
        if (value is IConsumesNextProtocolValue)
        {
            ((IConsumesNextProtocolValue)value).PatchNextProtocol(frame, myOffset, nextProtocol);
        }
    }

    #endregion

    #region Fragmentation capabilities

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> implements <see cref="IFragmentable"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsFragmentable<TValue>(in TValue value)
        where TValue : struct
        => value is IFragmentable;

    /// <summary>
    /// Reads the fragmentation parameters of <paramref name="value"/> when it
    /// implements <see cref="IFragmentable"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetFragmentation<TValue>(in TValue value, out bool canFragment, out FragmentationKind kind, out int alignment)
        where TValue : struct
    {
        if (value is IFragmentable)
        {
            canFragment = ((IFragmentable)value).CanFragment;
            kind = ((IFragmentable)value).FragmentationKind;
            alignment = ((IFragmentable)value).FragmentAlignment;
            return true;
        }

        canFragment = false;
        kind = FragmentationKind.NetworkLayer;
        alignment = 0;
        return false;
    }

    /// <summary>
    /// Calls <see cref="IFragmentable.PatchFragmentHeader"/> when <paramref name="value"/>
    /// is fragmentable and its <see cref="IFragmentable.FragmentationKind"/> equals
    /// <paramref name="activeKind"/>; otherwise does nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void PatchFragmentHeader<TValue>(
        in TValue value, scoped Span<byte> frame, int myOffset, int myLength,
        int fragmentPayloadOffset, bool moreFragments, FragmentationKind activeKind)
        where TValue : struct
    {
        if (value is IFragmentable && ((IFragmentable)value).FragmentationKind == activeKind)
        {
            ((IFragmentable)value).PatchFragmentHeader(frame, myOffset, myLength, fragmentPayloadOffset, moreFragments);
        }
    }

    #endregion

    #region Stateful / stateless dispatch

    /// <summary>
    /// Writes the header of <paramref name="value"/>: the stateful overload for an
    /// <see cref="IStatefulLayer"/>, the stateless overload for an
    /// <see cref="IStatelessLayer"/>.  A layer that is neither writes nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteHeader<TValue>(in TValue value, scoped Span<byte> dst, ref SessionState state)
        where TValue : struct
    {
        if (value is IStatefulLayer)
        {
            ((IStatefulLayer)value).WriteHeader(dst, ref state);
        }
        else if (value is IStatelessLayer)
        {
            ((IStatelessLayer)value).WriteHeader(dst);
        }
    }

    /// <summary>Calls <see cref="IStatefulLayer.InitializeState"/> when <paramref name="value"/> is stateful.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void InitializeState<TValue>(in TValue value, ref SessionState state)
        where TValue : struct
    {
        if (value is IStatefulLayer)
        {
            ((IStatefulLayer)value).InitializeState(ref state);
        }
    }

    #endregion
}
