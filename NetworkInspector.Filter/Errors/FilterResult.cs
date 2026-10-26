// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Errors;

/// <summary>
/// Holds either a success value or a <see cref="FilterError"/>.
/// Used by every filter API that can fail so callers never pay exception cost for expected
/// failures such as syntax errors or unknown field names.
/// <para>
/// A struct, so a success carries its value without an allocation on the compile and evaluation paths. Immutable and
/// therefore thread-safe. <c>default(FilterResult&lt;T&gt;)</c> is not a meaningful result: it reports
/// <see cref="IsSuccess"/> as <see langword="false"/> but holds no error, so <see cref="Error"/> returns
/// <see langword="null"/> despite its non-nullable type. Create results only through <see cref="FilterResult.Ok{T}"/>,
/// <see cref="FilterResult.Fail{T}"/>, or the implicit conversions.
/// </para>
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly struct FilterResult<T> : IEquatable<FilterResult<T>>
{
    #region Fields

    private readonly T? _Value;
    private readonly FilterError? _Error;

    #endregion

    #region Construction

    /// <summary>Creates a success result.</summary>
    internal FilterResult(T value)
    {
        _Value = value;
        _Error = null;
        IsSuccess = true;
    }

    /// <summary>Creates a failure result.</summary>
    internal FilterResult(FilterError error)
    {
        _Value = default;
        _Error = error;
        IsSuccess = false;
    }

    #endregion

    #region Properties

    /// <summary>Whether this result carries a value.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The success value.
    /// Intended for non-hot-path call sites that already checked <see cref="IsSuccess"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">When this result carries an error.</exception>
    public T Value => IsSuccess
        ? _Value!
        : throw new InvalidOperationException($"Cannot read Value from a failed FilterResult: {_Error}");

    /// <summary>
    /// The error.
    /// Intended for non-hot-path call sites that already checked <see cref="IsSuccess"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">When this result carries a value.</exception>
    public FilterError Error => IsSuccess
        ? throw new InvalidOperationException("Cannot read Error from a successful FilterResult.")
        : _Error!;

    #endregion

    #region Accessors

    /// <summary>Extracts the success value when present.</summary>
    /// <param name="value">Receives the value on success.</param>
    /// <returns><see langword="true"/> when this result carries a value.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _Value;
        return IsSuccess;
    }

    /// <summary>Extracts the error when present.</summary>
    /// <param name="error">Receives the error on failure.</param>
    /// <returns><see langword="true"/> when this result carries an error.</returns>
    public bool TryGetError([MaybeNullWhen(false)] out FilterError error)
    {
        error = _Error;
        return !IsSuccess;
    }

    #endregion

    #region Conversions

    /// <summary>Wraps a value into a success result.</summary>
    public static implicit operator FilterResult<T>(T value) => new(value);

    /// <summary>Wraps an error into a failure result.</summary>
    public static implicit operator FilterResult<T>(FilterError error) => new(error);

    #endregion

    #region Equality

    /// <inheritdoc />
    public bool Equals(FilterResult<T> other) =>
        // Errors compare by reference: FilterError defines no value equality, and one failure is one error instance.
        IsSuccess == other.IsSuccess
        && ReferenceEquals(_Error, other._Error)
        && EqualityComparer<T?>.Default.Equals(_Value, other._Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is FilterResult<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsSuccess, _Value, _Error);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FilterResult<T> left, FilterResult<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FilterResult<T> left, FilterResult<T> right) => !left.Equals(right);

    #endregion
}
