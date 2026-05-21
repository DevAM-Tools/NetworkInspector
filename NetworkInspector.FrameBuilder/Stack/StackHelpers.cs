// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Non-generic helper methods for interface-cast checks inside generic
/// <see cref="Stack{THead,TTail}"/> and <see cref="StatelessStack{THead,TTail}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Head is IXxx</c> pattern inside a generic method triggers CA1508
/// ("always true or always false condition") because the JIT folds the test to a
/// constant for each concrete <c>THead</c> specialisation.  Routing the check
/// through a <see cref="MethodImplOptions.NoInlining"/> helper that operates on
/// <see langword="object"/> prevents the analyser from seeing a dead-code path
/// while preserving correct runtime behaviour.
/// </para>
/// </remarks>
internal static class StackHelpers
{
    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="result"/> when
    /// <paramref name="boxed"/> implements <typeparamref name="TInterface"/>;
    /// otherwise returns <see langword="false"/> and sets <paramref name="result"/>
    /// to <see langword="null"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TryCastObj<TInterface>([NotNullWhen(true)] object? boxed, [NotNullWhen(true)] out TInterface? result)
        where TInterface : class
    {
        result = boxed as TInterface;
        return result is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> and sets <paramref name="result"/> when
    /// <paramref name="value"/> implements <typeparamref name="TInterface"/>.
    /// Boxes <paramref name="value"/> and delegates to <see cref="TryCastObj{TInterface}"/>
    /// to avoid the CA1508 false positive on generic-struct dispatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryCast<TValue, TInterface>(in TValue value, [NotNullWhen(true)] out TInterface? result)
        where TValue : struct
        where TInterface : class
        => TryCastObj<TInterface>(value, out result);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> implements
    /// <typeparamref name="TInterface"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Is<TValue, TInterface>(in TValue value)
        where TValue : struct
        where TInterface : class
        => TryCastObj<TInterface>(value, out _);
}
