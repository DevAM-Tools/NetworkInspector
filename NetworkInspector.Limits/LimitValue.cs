// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The effective value of a limit: a non-negative number in the base unit of the <see cref="ILimitUnit"/> of its
/// <see cref="LimitDefinition"/> (bytes, milliseconds, a count, or the base unit of an application unit), or
/// <see cref="Off"/>. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// "Off" is a separate state, because "0 means no limit" would turn a typo into a disabled
/// protection. <see cref="Ceiling"/> is the form hot paths compare against: read it once per decision into a local
/// <see cref="long"/>, never call the struct per item. No member allocates except <see cref="ToString"/>.
/// <para>The default value is <c>Of(0)</c>, a limit that refuses everything; a field of this type must be assigned the
/// default of its definition (<see cref="LimitDefinition.DefaultValue"/>) before use.</para>
/// </remarks>
/// <seealso cref="LimitDefinition"/>
public readonly struct LimitValue : IEquatable<LimitValue>
{
    #region Encoding

    /// <summary>
    /// Invariant: -1 encodes off; every real value is &gt;= 0 because <see cref="Of"/> validates, so the encoding cannot collide with a
    /// value. The default struct (all bits zero) is therefore <c>Of(0)</c>, never <see cref="Off"/>: forgetting to initialize a
    /// field fails closed.
    /// </summary>
    private const long _OffBits = -1;

    /// <summary>The value in the base unit, or <see cref="_OffBits"/>.</summary>
    private readonly long _Bits;

    /// <summary>Wraps an already validated encoding.</summary>
    /// <param name="bits">A non-negative value or <see cref="_OffBits"/>.</param>
    private LimitValue(long bits) => _Bits = bits;

    #endregion

    #region Construction and state

    /// <summary>The limit is switched off: every input passes.</summary>
    public static LimitValue Off { get; } = new(_OffBits);

    /// <summary>Creates an active limit.</summary>
    /// <param name="value">The limit in the base unit of its definition; zero is a valid limit that refuses everything.</param>
    /// <returns>The active limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static LimitValue Of(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return new LimitValue(value);
    }

    /// <summary>Whether the limit is switched off.</summary>
    public bool IsOff => _Bits == _OffBits;

    /// <summary>The value in the base unit.</summary>
    /// <exception cref="InvalidOperationException">The limit is off.</exception>
    public long Value => IsOff
        ? throw new InvalidOperationException("The limit is off and has no value.")
        : _Bits;

    /// <summary>The value to compare against: <see cref="long.MaxValue"/> when the limit is off, so "input &gt; Ceiling" is never true.</summary>
    public long Ceiling => IsOff
        ? long.MaxValue
        : _Bits;

    /// <summary>The encoding that <see cref="Limit"/> stores in its one word: a non-negative value, or -1 for off.</summary>
    /// <remarks>Sync: <see cref="Limit"/> reads and writes this encoding and tests <c>bits &lt; 0</c> for off.</remarks>
    internal long Bits => _Bits;

    /// <summary>Rebuilds a value from an encoding that <see cref="Bits"/> produced.</summary>
    /// <param name="bits">A non-negative value or -1 for off.</param>
    /// <returns>The value.</returns>
    /// <remarks>Sync: <see cref="Limit"/> stores only encodings of valid values, so no check runs here.</remarks>
    internal static LimitValue FromBits(long bits) => new(bits);

    #endregion

    #region Equality and text

    /// <inheritdoc/>
    public bool Equals(LimitValue other) => _Bits == other._Bits;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LimitValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _Bits.GetHashCode();

    /// <summary>
    /// The text <c>off</c> or the base number in invariant form. It carries no unit: units belong to a definition
    /// (<see cref="LimitDefinition.Format"/>).
    /// </summary>
    public override string ToString() => IsOff
        ? "off"
        : _Bits.ToString(CultureInfo.InvariantCulture);

    /// <summary>Equality of two values: the same number, or both off.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when both values are the same number or both are off.</returns>
    public static bool operator ==(LimitValue left, LimitValue right) => left.Equals(right);

    /// <summary>Inequality of two values: different numbers, or one is off and the other is not.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when the values differ.</returns>
    public static bool operator !=(LimitValue left, LimitValue right) => !left.Equals(right);

    #endregion
}
