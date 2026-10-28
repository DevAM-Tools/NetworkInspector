// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Display text that is either present now or built on first read and then cached in place. One reference wide
/// (8 bytes), so <see cref="FieldValue"/> stays 32 bytes.
/// <para><b>Why this exists:</b> protocol parsers create far more display text than anyone reads. A lazy text costs one
/// small box at parse time and the string only when a field tree, an exporter, or a UI reads it.</para>
/// <para>Wraps a single <c>object?</c>: <c>null</c> = absent, <see cref="string"/> = direct value (or the cached result),
/// <see cref="Func{String}"/> = stateless deferred, <c>ILazyTextFormat</c> = stateful deferred (<see cref="FormatLazy{TState}"/>).</para>
/// <para><b>Thread safety:</b> concurrent first reads are safe. Every member reads the wrapped value exactly once per
/// call and evaluates only the deferred object it observed; the result replaces that object through
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>. The first publisher wins and every concurrent reader returns
/// that one string, so a loser discards its own result. A factory may therefore run more than once and must tolerate
/// concurrent calls. Do not mutate captured factory state unsafely.</para>
/// <para><b>Storage:</b> the cache lives in the storage location that was read. This is a readonly struct, so evaluating a
/// copy caches into the copy and leaves the original deferred. Read through a <see langword="ref"/> (array element, field,
/// or the box that <see cref="FieldValueData"/> keeps) when the cached result must be shared.</para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct LazyString : IEquatable<LazyString>, IComparable<LazyString>
{
    #region Fields

    /// <summary>Empty string singleton (non-null, zero-length).</summary>
    public static readonly LazyString Empty = new(string.Empty);

    // The one wrapped reference:
    //   null            -> absent
    //   string          -> direct value, or the cached result of an evaluated deferred text
    //   Func<string>    -> stateless deferred (Lazy)
    //   ILazyTextFormat -> stateful deferred (FormatLazy)
    // A single field keeps the struct the size of a pointer and lets the cache replace the deferred object atomically
    // without a second slot or a wrapper object.
    private readonly object? _Value;

    #endregion

    #region Lifecycle

    /// <summary>Creates a lazy text from an already built string.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LazyString(string value)
    {
        _Value = value;
    }

    // Raw wrapping, used by the factory methods.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LazyString(object? value)
    {
        _Value = value;
    }

    /// <summary>
    /// Creates a lazily evaluated text. One result is cached per storage location; the delegate (and its closure, if it
    /// captures) is the per-call allocation. Prefer <see cref="FormatLazy{TState}"/> with a static lambda on hot paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LazyString Lazy(Func<string> factory) => new(factory);

    /// <summary>
    /// Creates a lazily evaluated text over captured <paramref name="state"/>. Use a <c>static</c> lambda and a value tuple:
    /// <code>
    /// LazyString.FormatLazy((srcPort, dstPort),
    ///     static s =&gt; string.Create(CultureInfo.InvariantCulture, $"Src: {s.srcPort}, Dst: {s.dstPort}"));
    /// </code>
    /// The state is boxed once (one allocation now); the static lambda captures nothing, so no closure object exists, and the
    /// string is allocated only when the text is first read.
    /// </summary>
    /// <remarks>
    /// <b>Accepted cost (decision record):</b> this is one small object per call, also for packets whose info text is never
    /// read; the packet-info pattern of the protocol parsers (IPv4, UDP, TCP and others) pays it once per packet on ingest.
    /// It is accepted, not overlooked: the alternative is a struct payload that the field value storage holds inline, which
    /// needs a change to the shared value layout (a fixed state size and a cached formatter delegate per call site) and
    /// belongs to the next rework of this class, together with the other lazy-text paths. The object is gen-0 garbage that
    /// dies with the packet unless the info text is read, so the cost is allocation volume, not retention. A protocol that
    /// would pay it twice per packet should build the text from a single state (one call), as the TCP layer does.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LazyString FormatLazy<TState>(TState state, Func<TState, string> formatter)
        => new(new LazyTextFormat<TState>(state, formatter));

    /// <summary>Rebuilds a lazy text from <see cref="RawValue"/>; for union-style storage such as <see cref="FieldValueData"/>.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LazyString FromRawValue(object? value) => new(value);

    #endregion

    #region Public API

    /// <summary>The wrapped reference, for union-style storage that cannot hold the struct itself.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public object? RawValue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value;
    }

    /// <summary>
    /// The text; <see cref="string.Empty"/> when absent. Builds and caches on first read; concurrent first readers of the
    /// same storage location all return the one cached string reference.
    /// </summary>
    public string AsString
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            // Read the field once: a second read could observe a string that another thread cached in between, which must
            // never be mistaken for "unknown type". The cached path stays one read plus one type check.
            object? observed = _ReadValue();
            return observed switch
            {
                string s => s,
                Func<string> => _EvaluateAndCache(observed),
                ILazyTextFormat => _EvaluateAndCache(observed),
                _ => string.Empty,
            };
        }
    }

    /// <summary>Tries to get the text without throwing; forwards to <see cref="TryGetString(out string, out LazyStringError)"/>.</summary>
    /// <param name="result">The cached string afterwards; <see cref="string.Empty"/> if absent or if this call's factory faulted and won.</param>
    /// <returns><see langword="true"/> if this call obtained the string without a factory fault.</returns>
    public bool TryGetString(out string result) => TryGetString(out result, out _);

    /// <summary>
    /// Tries to get the text without throwing. A direct string or an absent value succeeds at once; a deferred text is
    /// evaluated safely: on success the result is cached, on a factory exception an empty string is cached so the fault is
    /// not retried forever and <paramref name="error"/> carries the message.
    /// </summary>
    /// <remarks>
    /// <paramref name="result"/> is always the string cached afterwards; when another thread cached first, that is the other
    /// thread's string. The return value and <paramref name="error"/> describe only this call's own factory run.
    /// </remarks>
    /// <param name="result">The cached string; <see cref="string.Empty"/> if absent or if this call's factory faulted and won.</param>
    /// <param name="error">Failure detail when this call's own factory run threw; otherwise default.</param>
    /// <returns><see langword="true"/> if this call obtained the string without a factory fault; <see langword="false"/> if its own factory threw.</returns>
    public bool TryGetString(out string result, out LazyStringError error)
    {
        // Single read of the field; the helper evaluates only this observed object (see AsString).
        object? observed = _ReadValue();
        switch (observed)
        {
            case string s:
                result = s;
                error = default;
                return true;
            case Func<string>:
            case ILazyTextFormat:
                return _TryEvaluateAndCache(observed, out result, out error);
            default:
                result = string.Empty;
                error = default;
                return true;
        }
    }

    /// <summary>Whether this instance has no value (default/absent).</summary>
    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value is null;
    }

    /// <summary>Whether the text is empty (absent or zero-length). Evaluates a deferred text.</summary>
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Value switch
        {
            null => true,
            string s => s.Length == 0,
            _ => AsString.Length == 0,
        };
    }

    /// <summary>Length of the text (0 if absent). Evaluates a deferred text.</summary>
    public int Length => AsString.Length;

    /// <summary>Whether this wraps a deferred text that has not been evaluated yet.</summary>
    public bool IsLazy => _Value is Func<string> or ILazyTextFormat;

    /// <summary>Whether the value is a direct string, absent, or the cached result of an evaluated text.</summary>
    public bool IsEvaluated => _Value is not (Func<string> or ILazyTextFormat);

    /// <summary>The text as a character span. Evaluates a deferred text.</summary>
    public ReadOnlySpan<char> AsSpan => AsString.AsSpan();

    /// <summary>
    /// Concatenates this text with <paramref name="other"/>. Both sides are evaluated immediately (the result is a direct
    /// string); an absent or empty side returns the other side unchanged.
    /// </summary>
    public LazyString Append(LazyString other)
    {
        if (_Value is null)
        {
            return other;
        }

        if (other._Value is null)
        {
            return this;
        }

        if (_Value is string s && s.Length == 0)
        {
            return other;
        }

        if (other._Value is string otherString && otherString.Length == 0)
        {
            return this;
        }

        return new LazyString(string.Concat(AsString, other.AsString));
    }

    /// <summary>Prepends <paramref name="other"/> (evaluated eagerly, see <see cref="Append"/>).</summary>
    public LazyString Prepend(LazyString other) => other.Append(this);

    /// <summary>
    /// Writes the text into <paramref name="destination"/>. Returns the number of characters written, or -1 when the
    /// destination is too small.
    /// </summary>
    public int TryWriteTo(Span<char> destination)
    {
        string text = AsString;
        if (text.Length > destination.Length)
        {
            return -1;
        }

        text.AsSpan().CopyTo(destination);
        return text.Length;
    }

    #endregion

    #region Equality and comparison

    // Ordinal on the text; an absent value equals only another absent value and sorts before every present value.

    /// <inheritdoc/>
    public bool Equals(LazyString other)
    {
        if (_Value is null && other._Value is null)
        {
            return true;
        }

        if (_Value is null || other._Value is null)
        {
            return false;
        }

        return string.Equals(AsString, other.AsString, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LazyString other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _Value is not null
        ? AsString.GetHashCode(StringComparison.Ordinal)
        : 0;

    /// <inheritdoc/>
    public int CompareTo(LazyString other)
    {
        if (_Value is null && other._Value is null)
        {
            return 0;
        }

        if (_Value is null)
        {
            return -1;
        }

        if (other._Value is null)
        {
            return 1;
        }

        return string.CompareOrdinal(AsString, other.AsString);
    }

    /// <inheritdoc/>
    public override string ToString() => AsString;

    /// <summary>Implicit conversion from <see cref="string"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LazyString(string value) => new(value);

    /// <summary>Implicit conversion to <see cref="string"/> (evaluates a deferred text).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator string(LazyString value) => value.AsString;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LazyString left, LazyString right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LazyString left, LazyString right) => !left.Equals(right);

    /// <summary>Less-than operator.</summary>
    public static bool operator <(LazyString left, LazyString right) => left.CompareTo(right) < 0;

    /// <summary>Less-than-or-equal operator.</summary>
    public static bool operator <=(LazyString left, LazyString right) => left.CompareTo(right) <= 0;

    /// <summary>Greater-than operator.</summary>
    public static bool operator >(LazyString left, LazyString right) => left.CompareTo(right) > 0;

    /// <summary>Greater-than-or-equal operator.</summary>
    public static bool operator >=(LazyString left, LazyString right) => left.CompareTo(right) >= 0;

    #endregion

    #region Private helpers

    /// <summary>
    /// Reads the wrapped value once with acquire semantics. Every member that can evaluate calls this exactly once per call
    /// and hands the observed object to the helpers; the helpers never read the field again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object? _ReadValue() => Volatile.Read(ref Unsafe.AsRef(in _Value));

    /// <summary>
    /// Runs the deferred object that the caller observed. Callers guarantee a <see cref="Func{String}"/> or an
    /// <c>ILazyTextFormat</c>; anything else is a broken invariant.
    /// </summary>
    private static string _Run(object deferred) => deferred switch
    {
        Func<string> factory => factory(),
        ILazyTextFormat format => format.Evaluate(),
        _ => throw new UnreachableException("LazyString evaluation requires a deferred object."),
    };

    /// <summary>
    /// Publishes <paramref name="evaluated"/> in place of <paramref name="deferred"/> and returns the string that is cached
    /// afterwards. Winner takes all: if this compare-exchange wins, the own result is cached and returned; otherwise the
    /// string another thread cached first is returned and the own result is discarded. If the location holds neither the
    /// deferred object nor a string (the storage was reassigned), the own result is returned and nothing is persisted.
    /// </summary>
    /// <remarks>
    /// <c>Unsafe.AsRef</c> on the readonly field is sound here: the write is one reference-sized atomic store, and the
    /// compare-exchange compares against the object this call observed, so it can only replace that exact deferred object
    /// and never overwrites a string that was cached in the meantime.
    /// </remarks>
    private string _Publish(object deferred, string evaluated)
    {
        object? prior = Interlocked.CompareExchange(ref Unsafe.AsRef(in _Value), evaluated, deferred);
        if (ReferenceEquals(prior, deferred))
        {
            return evaluated;
        }

        // Lost the race: hand out the string that is cached, not the own result.
        string? cached = prior as string;
        return cached
            ?? evaluated;
    }

    /// <summary>
    /// Evaluates the deferred object the caller observed and caches the result (see <see cref="_Publish"/>). The factory may
    /// run on several threads at once; all of them return the same cached string. Kept out of line so the cached read
    /// path of <see cref="AsString"/> stays small enough to inline.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string _EvaluateAndCache(object deferred) => _Publish(deferred, _Run(deferred));

    /// <summary>
    /// Evaluates the observed deferred object for <see cref="TryGetString(out string, out LazyStringError)"/>. A fault is
    /// turned into a cached empty string so it is not retried forever; <paramref name="result"/> is always the string cached
    /// afterwards, while the return value and <paramref name="error"/> describe only this call's own factory run.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool _TryEvaluateAndCache(object deferred, out string result, out LazyStringError error)
    {
        // Run the factory first; a fault is recorded for this call and turned into an empty string to cache.
        string evaluated;
        bool succeeded;
        LazyStringError ownError;
        try
        {
            evaluated = _Run(deferred);
            succeeded = true;
            ownError = default;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            evaluated = string.Empty;
            succeeded = false;
            ownError = new LazyStringError(LazyStringErrorCode.FactoryFaulted, ex.Message);
        }

        // Publish after the try block so the compare-exchange itself is never covered by the catch.
        result = _Publish(deferred, evaluated);
        error = ownError;
        return succeeded;
    }

    #endregion
}

/// <summary>Error codes of <see cref="LazyString.TryGetString(out string, out LazyStringError)"/>.</summary>
public enum LazyStringErrorCode
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>The deferred factory or format object threw.</summary>
    FactoryFaulted = 1,
}

/// <summary>Failure detail of <see cref="LazyString.TryGetString(out string, out LazyStringError)"/>.</summary>
/// <param name="Code">The error code.</param>
/// <param name="Message">Exception message from the factory, or empty when <paramref name="Code"/> is <see cref="LazyStringErrorCode.None"/>.</param>
public readonly record struct LazyStringError(LazyStringErrorCode Code, string Message);
