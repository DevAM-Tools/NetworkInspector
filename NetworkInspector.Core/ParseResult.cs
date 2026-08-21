// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Compact 4-byte tagged union for protocol parse and dispatch outcomes.
/// Public variants are <b>Ok</b> (consumed byte count), <see cref="NotDispatched"/>, and <b>Error</b>.
/// The internal <see cref="int"/> discriminant is private.
/// Callers consume results with exactly two methods:
/// <see cref="TryPropagateError"/> (error path) and <see cref="TryGetConsumed"/> (Ok path).
/// Encoding −2 is an internal discriminant; callers use <see cref="TryGetConsumed"/> after
/// a false <see cref="TryPropagateError"/> to detect <see cref="NotDispatched"/>.
/// <para>
/// Construction is constrained to valid values only — the constructor is private:
/// <list type="bullet">
/// <item><c>return consumed;</c> — Ok: implicit from <see cref="int"/> (consumed in <c>[0, int.MaxValue - 1]</c>)</item>
/// <item><c>return ParseError.XXX(…);</c> — Error: implicit from <see cref="ParseError"/> (always TLS + error encoding)</item>
/// <item><c>return ParseResult.NotDispatched;</c> — named miss: table present, no protocol for the key</item>
/// <item><c>return existingResult;</c> — 4-byte copy for error propagation (TLS already set when Error)</item>
/// </list>
/// <c>return default;</c> is detected as an error ("uninitialized result").
/// <see cref="Protocols.IProtocol.Parse"/> must return Ok or Error only; <see cref="NotDispatched"/>
/// is reserved for <c>TryCallNextProtocol*</c> / <c>TryCallHeuristicProtocol</c>.
/// </para>
/// <para>
/// <b>Two-method contract:</b>
/// <list type="number">
/// <item>Call <see cref="TryPropagateError"/>. If <see langword="true"/>, <c>return</c> the
/// <c>out</c> result immediately (Error). TLS is not read; the 4-byte encoding is copied.</item>
/// <item>Call <see cref="TryGetConsumed"/>. If <see langword="true"/>, this is Ok (including Ok(0))
/// and <c>consumed</c> is the byte count. If <see langword="false"/>, this is
/// <see cref="NotDispatched"/> and <c>consumed</c> is 0.</item>
/// </list>
/// Archetypes:
/// <list type="bullet">
/// <item>K3 fire-and-propagate: <c>if (r.TryPropagateError(out ParseResult error)) return error;</c></item>
/// <item>K2 consumed-or-zero: K3, then <c>_ = r.TryGetConsumed(out int consumed);</c></item>
/// <item>K1 miss-fallback: K3, then <c>if (!r.TryGetConsumed(out _)) { /* try another key */ }</c></item>
/// </list>
/// After a false <see cref="TryPropagateError"/>, a false <see cref="TryGetConsumed"/> is exactly
/// <see cref="NotDispatched"/>. Ok(0) returns <see langword="true"/> from <see cref="TryGetConsumed"/>
/// with <c>consumed == 0</c> and must not be treated as a miss.
/// </para>
/// <para>
/// <b>Thread-safety and async:</b> Error details are stored in thread-local storage (TLS). A
/// <see cref="ParseResult"/> value itself is safe to copy across threads, but
/// <see cref="ParseError.LastError"/> must be read on the <b>same thread</b> that
/// produced the error — immediately after the call that returned the error, before any
/// further parse call on that thread overwrites the slot.
/// <b>Do not use non-generic <see cref="ParseResult"/> across <c>await</c>, <c>Task.Run</c>,
/// thread-pool continuations, or any other thread-context switch:</b> the receiving thread
/// will see an empty or unrelated error. Nested errors also overwrite the single TLS slot if
/// the outer error was not read first. Prefer <see cref="ParseResult{T}"/> when error details
/// must survive async boundaries, or capture via <see cref="TryGetError"/> synchronously on
/// the producing thread (packet-level consumers). Protocol call sites use
/// <see cref="TryPropagateError"/> and must not cross an async boundary with the result.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ParseResult
{
    // Encoding: >0 = Ok (consumed+1), 0 = uninitialized Error, -1 = TLS Error, -2 = NotDispatched
    private const int _NotDispatchedEncodedValue = -2;
    private readonly int _EncodedValue;

    #region Constructors

    /// <summary>Private — constructible via implicit operators and <see cref="NotDispatched"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ParseResult(int encodedValue)
    {
        _EncodedValue = encodedValue;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Named miss variant: a dispatch table was present and the key had no protocol.
    /// Not an error. Not a successful parse. Does not write TLS and does not allocate.
    /// </summary>
    public static readonly ParseResult NotDispatched = new(_NotDispatchedEncodedValue);

    /// <summary>
    /// Whether this result is the Ok variant (consumed ≥ 0), including Ok(0)
    /// when a protocol ran and consumed zero bytes.
    /// Internal diagnostic; public call sites use <see cref="TryGetConsumed"/>.
    /// </summary>
    internal bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue > 0;
    }

    /// <summary>
    /// Whether this result is <see cref="NotDispatched"/>. Not an error.
    /// <see cref="Protocols.IProtocol.Parse"/> must not return this.
    /// Internal diagnostic; public call sites use <see cref="TryGetConsumed"/> after
    /// a false <see cref="TryPropagateError"/>.
    /// </summary>
    internal bool IsNotDispatched
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue == _NotDispatchedEncodedValue;
    }

    /// <summary>
    /// Whether this result is the Error variant: uninitialized (<c>default</c>, encoding 0)
    /// or a TLS-backed parse error (encoding −1). False for <see cref="NotDispatched"/>.
    /// Internal diagnostic; public call sites use <see cref="TryPropagateError"/>.
    /// </summary>
    internal bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _EncodedValue <= 0 && _EncodedValue != _NotDispatchedEncodedValue;
    }

    #endregion

    #region Result Helpers

    /// <summary>
    /// Error-path method of the two-method contract. Returns <see langword="true"/> if and only
    /// if this is the Error variant. <paramref name="propagate"/> is always a 4-byte copy of
    /// this result and may be returned directly when the method returns <see langword="true"/>
    /// (TLS slot is not read or overwritten).
    /// </summary>
    /// <param name="propagate">Always <c>this</c>. Meaningful to return only when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for Error (including uninitialized); <see langword="false"/> for Ok and <see cref="NotDispatched"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPropagateError(out ParseResult propagate)
    {
        propagate = this;
        return _EncodedValue <= 0 && _EncodedValue != _NotDispatchedEncodedValue;
    }

    /// <summary>
    /// Ok-path method of the two-method contract. Returns <see langword="true"/> if and only
    /// if this is the Ok variant (including Ok(0)); <paramref name="consumed"/> is then the
    /// consumed byte count. When <see langword="false"/>, <paramref name="consumed"/> is
    /// guaranteed 0 — after a prior false <see cref="TryPropagateError"/> that means exactly
    /// <see cref="NotDispatched"/>.
    /// </summary>
    /// <param name="consumed">Consumed bytes on Ok; 0 on Error and <see cref="NotDispatched"/>.</param>
    /// <returns><see langword="true"/> for Ok; <see langword="false"/> for Error and <see cref="NotDispatched"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetConsumed(out int consumed)
    {
        if (_EncodedValue > 0)
        {
            consumed = _EncodedValue - 1;
            return true;
        }

        consumed = 0;
        return false;
    }

    /// <summary>
    /// Tries to get the error details. Returns <see langword="false"/> for Ok and for
    /// <see cref="NotDispatched"/>. Internal: packet-level consumers snapshot TLS.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetError(out ParseError error)
    {
        if (_EncodedValue == 0)
        {
            error = _UninitializedError;
            return true;
        }

        if (_EncodedValue == -1)
        {
            error = ParseError.LastError;
            return true;
        }

        error = default;
        return false;
    }

    #endregion

    #region Conversions

    /// <summary>
    /// Implicit conversion from consumed byte count to success result.
    /// <para>
    /// Consumed must be in <c>[0, int.MaxValue - 1]</c>: success is encoded as
    /// <c>consumed + 1</c>, so <see cref="int.MaxValue"/> has no representable encoding.
    /// Negative values and <see cref="int.MaxValue"/> throw
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult(int consumed)
    {
        if ((uint)consumed >= (uint)int.MaxValue)
        {
            _ThrowConsumedOutOfRange(consumed);
        }
        return new(consumed + 1);
    }

    /// <summary>
    /// Implicit conversion from <see cref="ParseError"/> to the Error variant.
    /// Stores the error in thread-local storage for later retrieval via <see cref="TryGetError"/>.
    /// Always Error — never <see cref="NotDispatched"/>.
    /// <para>
    /// <b>Warning:</b> Each conversion overwrites the thread-local slot. Propagate the prior
    /// error via <see cref="TryPropagateError"/> before returning a second error on the same
    /// thread. The slot is not preserved across <c>await</c> or thread-pool hops — read
    /// synchronously on the producing thread only.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static implicit operator ParseResult(ParseError error)
    {
        ParseError.SetLastError(error);
        return new(-1);
    }

    /// <summary>Sentinel error for <c>default(ParseResult)</c> / uninitialized results.</summary>
    private static readonly ParseError _UninitializedError =
        ParseError.InternalError("Uninitialized ParseResult (missing return statement or return default)");

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when consumed is negative or
    /// <see cref="int.MaxValue"/> (not representable as consumed + 1).
    /// </summary>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowConsumedOutOfRange(int consumed)
        => throw new ArgumentOutOfRangeException(
            nameof(consumed),
            consumed,
            "ParseResult: consumed bytes must be in [0, int.MaxValue - 1]. Use ParseError factory methods for errors.");

    /// <inheritdoc/>
    public override string ToString()
    {
        if (_EncodedValue > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Ok({_EncodedValue - 1})");
        }
        if (IsNotDispatched)
        {
            return "NotDispatched";
        }
        _ = TryGetError(out ParseError error);
        return string.Create(CultureInfo.InvariantCulture, $"Error({error.Message})");
    }

    /// <summary>Creates a successful typed result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Ok<T>(T value) => new(value, default, true);

    /// <summary>Creates a failed typed result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseResult<T> Fail<T>(ParseError error) => new(default, error, false);

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

    /// <summary>Creates a parse result with the given value, error, and success state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult(T? value, ParseError error, bool isSuccess)
    {
        _Value = value;
        _Error = error;
        IsSuccess = isSuccess;
    }

    #endregion

    #region Properties

    /// <summary>Whether this result represents a successful parse.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether this result represents a parse error.</summary>
    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !IsSuccess;
    }

    /// <summary>The success value. Throws if this is an error result.</summary>
    public T Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (IsSuccess)
            {
                return _Value!;
            }
            return ThrowHelpers.ThrowParseResultNoValue<T>();
        }
    }

    /// <summary>The parse error. Throws if this is a success result.</summary>
    public ParseError Error
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (!IsSuccess)
            {
                return _Error;
            }
            return ThrowHelpers.ThrowParseResultNoError<ParseError>();
        }
    }

    #endregion

    #region Result Helpers

    /// <summary>Tries to get the success value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _Value;
        return IsSuccess;
    }

    /// <summary>Tries to get the error.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetError(out ParseError error)
    {
        error = _Error;
        return !IsSuccess;
    }

    #endregion

    /// <inheritdoc/>
    public override string ToString()
    {
        if (IsSuccess)
        {
            return $"Ok({_Value})";
        }
        return $"Error({_Error.Message})";
    }

    #region Conversions

    /// <summary>Implicit conversion from value to success result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult<T>(T value) => ParseResult.Ok(value);

    /// <summary>Implicit conversion from ParseError to failed result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ParseResult<T>(ParseError error) => ParseResult.Fail<T>(error);

    #endregion
}
