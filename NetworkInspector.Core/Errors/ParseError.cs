// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Non-exception error type for protocol parsing failures.
/// Returned via <see cref="ParseResult"/> on the hot path — never thrown.
/// <para>
/// This is a compact <c>readonly struct</c> (24 bytes on x64). The non-generic
/// <see cref="ParseResult"/> encodes success/error in a single <c>int</c> (4 bytes)
/// and stores the <see cref="ParseError"/> details in thread-local storage on the
/// rare error path (&lt; 0.1% of packets).
/// </para>
/// <para>
/// <b>Why not an exception, and why thread-local storage:</b> an exception would cost far more than a normal return on
/// every malformed packet, and a 24-byte struct returned by value from every parse call would make the success path
/// pay for the error case. The 4-byte <see cref="ParseResult"/> keeps the hot path small, and the details travel
/// out-of-band in the thread-local slot (<c>LastError</c>) that only the error path writes. The price is the
/// same-thread rule documented on <c>LastError</c>; a typed <see cref="ParseResult{T}"/> carries the error inline for
/// callers that cannot honor it. The factory methods that build a message with string formatting are marked
/// <c>NoInlining</c> to keep that work out of inlined hot code.
/// </para>
/// <para>
/// <b>Thread-safety:</b> an immutable <c>readonly struct</c>; instances may be shared freely. Only the thread-local
/// last-error slot is per thread.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ParseError
{
    #region Error details and construction

    /// <summary>The kind of parse error.</summary>
    public ParseErrorKind Kind
    {
        get;
    }

    /// <summary>The protocol that produced this error (e.g., "eth", "ip").</summary>
    public string? ProtocolName
    {
        get;
    }

    /// <summary>
    /// Human-readable error message. Pre-computed at construction time.
    /// This is acceptable because <see cref="ParseError"/> is only constructed on the
    /// error path, which is rare — the success path never touches this field.
    /// Empty when an error was created with a null or empty message; <see cref="ToString"/> substitutes the
    /// <see cref="ParseErrorKind"/> name in that case. A <c>default</c> instance (not created through a factory)
    /// bypasses the property initializer and has a <see langword="null"/> message, despite the non-nullable type.
    /// </summary>
    public string Message
    {
        get;
    } = string.Empty;

    /// <summary>Creates a parse error with the specified details. Null <paramref name="message"/> is stored as empty.</summary>
    /// <param name="kind">Category of the error.</param>
    /// <param name="protocolName">Protocol that produced the error, or null when no protocol applies.</param>
    /// <param name="message">Detail text, or null.</param>
    private ParseError(ParseErrorKind kind, string? protocolName, string? message)
    {
        Kind = kind;
        ProtocolName = protocolName;
        Message = message ?? string.Empty;
    }

    #endregion

    #region Last-error slot

    /// <summary>
    /// Thread-local storage for the most recent error details. Written only by <c>SetLastError</c> on the error
    /// path. It keeps the last error (and its message string) reachable until the next error on the same thread
    /// replaces it.
    /// </summary>
    [ThreadStatic]
    private static ParseError _LastError;

    /// <summary>
    /// Retrieves the most recent error stored by an implicit conversion to <see cref="ParseResult"/>.
    /// Meaningful only when <see cref="ParseResult.IsError"/> is <see langword="true"/>
    /// and the result was created via a <see cref="ParseError"/> factory method.
    /// <para>
    /// <b>Thread-safety:</b> Error details are stored in thread-local storage. This property
    /// must be read on the <b>same thread</b> that produced the <see cref="ParseResult"/>.
    /// <b>Thread-crossing hazards (any of the following yields an empty <see cref="ParseError"/>):</b>
    /// <list type="bullet">
    ///   <item><c>await</c> of an async method that internally produces a <see cref="ParseResult"/></item>
    ///   <item><c>Task.Run()</c> / <c>ThreadPool.QueueUserWorkItem()</c></item>
    ///   <item>Resumption of an async state machine on a different thread-pool thread</item>
    ///   <item>Any explicit context switch (<c>SynchronizationContext.Post()</c>)</item>
    /// </list>
    /// <b>Safe pattern:</b> Read <see cref="LastError"/> synchronously, on the same stack frame
    /// where the <see cref="ParseResult"/> was produced, before any <c>await</c>.
    /// </para>
    /// </summary>
    internal static ParseError LastError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _LastError;
    }

    /// <summary>
    /// Stores the error in thread-local storage. Called by the implicit
    /// <see cref="ParseResult"/> conversion operator on the error path.
    /// <para>
    /// <b>Overwrites</b> any prior unread error on this thread. Not safe across
    /// <c>await</c> / thread-pool boundaries — read <see cref="LastError"/> synchronously
    /// on the same thread that produced the <see cref="ParseResult"/>.
    /// </para>
    /// </summary>
    /// <param name="error">The error to keep as the last error of the calling thread.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetLastError(ParseError error) => _LastError = error;

    #endregion

    #region Error factories

    /// <summary>Creates an insufficient data error. The message is a constant, so the call formats nothing.</summary>
    /// <param name="protocolName">Protocol that produced the error.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.InsufficientData"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="protocolName"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError InsufficientData(string protocolName)
    {
        ArgumentNullException.ThrowIfNull(protocolName);
        return new(ParseErrorKind.InsufficientData, protocolName, "Insufficient data");
    }

    /// <summary>Creates an insufficient data error with expected/actual sizes.</summary>
    /// <param name="protocolName">Protocol that produced the error.</param>
    /// <param name="expected">Number of bytes the protocol needed.</param>
    /// <param name="actual">Number of bytes that were available.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.InsufficientData"/> whose message states both sizes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="protocolName"/> is <see langword="null"/>.</exception>
    // Not inlined — string interpolation on the rare error path should stay out of hot code.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ParseError InsufficientDataWithInfo(string protocolName, ulong expected, ulong actual)
    {
        ArgumentNullException.ThrowIfNull(protocolName);
        return new(
            ParseErrorKind.InsufficientData,
            protocolName,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Insufficient data: expected {expected} bytes, got {actual}"));
    }

    /// <summary>Creates an invalid data error.</summary>
    /// <param name="protocolName">Protocol that produced the error.</param>
    /// <param name="message">Detail text; <see langword="null"/> is stored as empty.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.InvalidData"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="protocolName"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError InvalidData(string protocolName, string? message)
    {
        ArgumentNullException.ThrowIfNull(protocolName);
        return new(ParseErrorKind.InvalidData, protocolName, message);
    }

    /// <summary>Creates a custom error.</summary>
    /// <param name="protocolName">Protocol that produced the error.</param>
    /// <param name="message">Detail text; <see langword="null"/> is stored as empty.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.Custom"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="protocolName"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError Custom(string protocolName, string? message)
    {
        ArgumentNullException.ThrowIfNull(protocolName);
        return new(ParseErrorKind.Custom, protocolName, message);
    }

    /// <summary>Creates an internal logic error (a condition that should not occur); it has no protocol name.</summary>
    /// <param name="message">Detail text; <see langword="null"/> is stored as empty.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.InternalError"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError InternalError(string? message) =>
        new(ParseErrorKind.InternalError, null, message);

    /// <summary>
    /// Creates a field append failed error: the field tree cannot take the field (it is full or the packet is finalized).
    /// It has no protocol name.
    /// </summary>
    /// <returns>An error of kind <see cref="ParseErrorKind.FieldAppendFailed"/> with a constant message.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError FieldAppendFailed() =>
        new(ParseErrorKind.FieldAppendFailed, null,
            "Field append failed: packet may be finalized or field tree full.");

    /// <summary>Creates a field type mismatch error.</summary>
    /// <param name="fieldName">Name of the field that was accessed with the wrong type.</param>
    /// <param name="expected">The type the caller asked for.</param>
    /// <param name="actual">The type the field holds.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.FieldTypeMismatch"/> whose message names the field and both types.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> is <see langword="null"/>.</exception>
    // Not inlined — string interpolation on the rare error path should stay out of hot code.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ParseError FieldTypeMismatch(string fieldName, FieldType expected, FieldType actual)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return new(
            ParseErrorKind.FieldTypeMismatch,
            null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Field type mismatch for '{fieldName}': expected {expected}, got {actual}"));
    }

    /// <summary>
    /// Creates an error when a dispatch table cannot be resolved (invalid id or not registered on the stack).
    /// This is not used for "table exists, key has no protocol".
    /// </summary>
    /// <param name="protocolName">Optional protocol context; <see langword="null"/> when no protocol applies.</param>
    /// <param name="message">Detail text; <see langword="null"/> is stored as empty.</param>
    /// <returns>An error of kind <see cref="ParseErrorKind.ProtocolTableMissing"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ParseError ProtocolTableMissing(string? protocolName, string? message) =>
        new(ParseErrorKind.ProtocolTableMissing, protocolName, message);

    #endregion

    #region Text form

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the message, or the kind name when the message is empty or null (a <c>default</c> instance), so the
    /// result is never empty.
    /// </remarks>
    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Message))
        {
            return Message;
        }

        return Kind.ToString();
    }

    #endregion
}
