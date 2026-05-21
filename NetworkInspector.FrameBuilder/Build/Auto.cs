// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Wraps a header field that may be either explicitly supplied by the caller
/// or computed automatically by the layer (length, checksum, identifier, …).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Compute"/> = let the layer compute the value.
/// <see cref="Explicit"/> = use the supplied value verbatim and skip the
/// per-field auto-compute branch.
/// </para>
/// <para>The default value (<c>default(Auto&lt;T&gt;)</c>) is <see cref="Compute"/>.</para>
/// </remarks>
/// <typeparam name="T">Underlying unmanaged value type.</typeparam>
public readonly struct Auto<T> where T : unmanaged
{
    private readonly T _Value;
    private readonly bool _IsExplicit;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Auto(T value, bool isExplicit)
    {
        _Value = value;
        _IsExplicit = isExplicit;
    }

    /// <summary>Sentinel value asking the layer to compute the field automatically.</summary>
    public static Auto<T> Compute => default;

    /// <summary>Wraps a caller-supplied explicit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Auto<T> Explicit(T value) => new(value, true);

    /// <summary>
    /// Returns <c>true</c> together with the explicit value when one was
    /// supplied; <c>false</c> when the layer should compute the value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetExplicit(out T value)
    {
        value = _Value;
        return _IsExplicit;
    }

    /// <summary>Implicitly accept a bare value as an explicit override.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Auto<T>(T value) => Explicit(value);
}
