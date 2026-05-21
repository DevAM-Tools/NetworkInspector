// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Compact 4-byte result type for protocol <see cref="Protocols.IProtocol.Parse"/> operations.
/// Encodes success (consumed byte count) and error state in a single <see cref="int"/>.
/// <para>
/// <b>Encoding scheme:</b>
/// <list type="bullet">
/// <item><c>EncodedValue &gt; 0</c> — Success: consumed bytes = <c>EncodedValue - 1</c></item>
/// <item><c>EncodedValue == 0</c> — Error: uninitialized (<c>default</c> or accidental <c>return -1</c>)</item>
/// <item><c>EncodedValue &lt; 0</c> — Error: details stored in thread-local <see cref="ParseError.LastError"/></item>
/// </list>
/// </para>
/// <para>
/// Construction is constrained to valid values only — the constructor is private:
/// <list type="bullet">
/// <item><c>return consumed;</c> — implicit from <see cref="int"/> (consumed ≥ 0 → encoded as consumed + 1)</item>
/// <item><c>return ParseError.XXX(…);</c> — implicit from <see cref="ParseError"/> (stores in TLS, encoded as −1)</item>
/// <item><c>return existingResult;</c> — 4-byte copy for error propagation (TLS already set)</item>
/// </list>
/// <c>return default;</c> is detected as an error ("uninitialized result").
/// </para>
/// <para>
/// <b>Thread-safety:</b> Error details are stored in thread-local storage (TLS). A
/// <see cref="ParseResult"/> value itself is safe to copy across threads, but
/// <see cref="ParseError.LastError"/> must be read on the <b>same thread</b> that
/// produced the error. Observing error details after an <c>await</c>, a
/// <c>Task.Run</c> boundary, or any other thread-context switch yields<c>null</c>
/// or stale data from the receiving thread's own error slot.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ParseResult
{
    // Encoding: >0 = success (consumed+1), 0 = uninitialized error, <0 = explicit error (TLS)
    private readonly int _EncodedValue;

    #region Constructors

    /// <summary>Private — only constructible via implicit operators.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ParseResult(int encodedValue)
    {
        _EncodedValue = encodedValue;
    }

    #endregion

    #region Properties

    /// <summary>Whether this result represents a successful parse (consumed ≥ 0).</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue > 0;
    }

    /// <summary>Whether this result represents a parse error.</summary>
    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue <= 0;
    }

    /// <summary>The consumed byte count. Throws if this is an error result.</summary>
    public int Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue > 0 ? _EncodedValue - 1 : ThrowHelpers.ThrowParseResultNoValue<int>();
    }

    /// <summary>The parse error. Throws if this is a success result.</summary>
    public ParseError Error
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue <= 0
            ? (_EncodedValue < 0 ? ParseError.LastError : UninitializedError)
            : ThrowHelpers.ThrowParseResultNoError<ParseError>();
    }

    #endregion

    #region Result Helpers

    /// <summary>Tries to get the consumed byte count.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(out int value)
    {
        // Branch-free decode: value is only meaningful when _EncodedValue > 0
        value = _EncodedValue - 1;
        return _EncodedValue > 0;
    }

    /// <summary>Tries to get the error details.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetError(out ParseError error)
    {
        if (_EncodedValue <= 0)
        {
            // Negative = explicit error (TLS), zero = uninitialized (static sentinel)
            error = _EncodedValue < 0 ? ParseError.LastError : UninitializedError;
            return true;
        }

        error = default;
        return false;
    }

    #endregion

    #region Conversions

    /// <summary>
    /// Implicit conversion from consumed byte count to success result.
    /// <para>Consumed must be ≥ 0. Negative values are programming errors
    /// and cause an <see cref="ArgumentOutOfRangeException"/>.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult(int consumed)
    {
        if (consumed < 0)
        {
            ThrowNegativeConsumed(consumed);
        }
        return new(consumed + 1);
    }

    /// <summary>
    /// Implicit conversion from <see cref="ParseError"/> to error result.
    /// Stores the error in thread-local storage for later retrieval via <see cref="TryGetError"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static implicit operator ParseResult(ParseError error)
    {
        ParseError.SetLastError(error);
        return new(-1);
    }

    /// <summary>Sentinel error for <c>default(ParseResult)</c> / uninitialized results.</summary>
    private static readonly ParseError UninitializedError =
        ParseError.InternalError("Uninitialized ParseResult (missing return statement or return default)");

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for negative consumed byte counts.</summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNegativeConsumed(int consumed)
        => throw new ArgumentOutOfRangeException(
            nameof(consumed),
            consumed,
            "ParseResult: consumed bytes must be >= 0. Use ParseError factory methods for errors.");

    /// <inheritdoc/>
    public override string ToString() =>
        _EncodedValue > 0 ? $"Ok({_EncodedValue - 1})" : $"Error({Error.Message})";

    #endregion
}

/// <summary>
/// Discriminated result type for parsing operations.
/// Avoids exception overhead on the hot path.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
[StructLayout(LayoutKind.Auto)]
public readonly struct ParseResult<T>
{
    #region Constructors

    private readonly T? _Value;
    private readonly ParseError _Error;
    private readonly bool _IsSuccess;

    /// <summary>Creates a parse result with the given value, error, and success state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ParseResult(T? value, ParseError error, bool isSuccess)
    {
        _Value = value;
        _Error = error;
        _IsSuccess = isSuccess;
    }

    #endregion

    #region Properties

    /// <summary>Whether this result represents a successful parse.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IsSuccess;
    }

    /// <summary>Whether this result represents a parse error.</summary>
    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_IsSuccess;
    }

    /// <summary>The success value. Throws if this is an error result.</summary>
    public T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _IsSuccess ? _Value! : ThrowHelpers.ThrowParseResultNoValue<T>();
    }

    /// <summary>The parse error. Throws if this is a success result.</summary>
    public ParseError Error
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_IsSuccess ? _Error : ThrowHelpers.ThrowParseResultNoError<ParseError>();
    }

    #endregion

    #region Result Helpers

    /// <summary>Tries to get the success value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _Value;
        return _IsSuccess;
    }

    /// <summary>Tries to get the error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetError(out ParseError error)
    {
        error = _Error;
        return !_IsSuccess;
    }

    /// <summary>Creates a successful result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Ok(T value) => new(value, default, true);

    /// <summary>Creates a failed result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Fail(ParseError error) => new(default, error, false);

    /// <inheritdoc/>
    public override string ToString() =>
        _IsSuccess ? $"Ok({_Value})" : $"Error({_Error.Message})";

    #endregion

    #region Conversions

    /// <summary>Implicit conversion from value to success result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult<T>(T value) => Ok(value);

    /// <summary>Implicit conversion from ParseError to failed result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult<T>(ParseError error) => Fail(error);

    #endregion
}
