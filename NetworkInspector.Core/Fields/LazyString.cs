// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// Display text that is either present now or built on first read and then cached in place. One reference wide
/// (8 bytes), so <see cref="FieldValue"/> stays 32 bytes.
/// <para><b>Why this exists:</b> protocol parsers create far more display text than anyone reads. A lazy text costs one
/// small box at parse time and the string only when a field tree, an exporter, or a UI reads it.</para>
/// <para>Wraps a single <c>object?</c>: <c>null</c> = absent, <see cref="string"/> = direct value (or the cached result),
/// <c>ILazyTextFormat</c> = deferred text (<see cref="Lazy"/> wraps its delegate, <see cref="FormatLazy{TState}"/> its state).</para>
/// <para><b>Thread safety:</b> concurrent first reads are safe. Each member evaluates a deferred value at most once; the
/// result is cached, so a second read inside one member returns the same string. The result replaces the deferred object
/// through <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>. The first publisher wins and every concurrent reader
/// returns that one string, so a loser discards its own result. A factory may therefore run more than once and must tolerate
/// concurrent calls. Do not mutate captured factory state unsafely.</para>
/// <para><b>Storage:</b> the evaluated string is cached in the storage location that was read, replacing the deferred
/// object (which then releases its captured state). This is a readonly struct, so evaluating a copy caches into the copy and
/// leaves the original holding the deferred object. Read through a <see langword="ref"/> (array element, field, or the box that
/// <see cref="FieldValueData"/> keeps) to drop the deferred object where the text lives.</para>
/// <para><b>Built once:</b> the deferred object itself remembers the string it built (see <c>ILazyTextFormat</c>), so copies
/// share one build: the first copy that is read runs the formatter, every other copy (and the original) gets the same string
/// instance without running it again. A copy therefore costs a compare-exchange on its own location at most, never a second
/// build, and there is no need to pass a text by reference to avoid building it twice. Consequence: the first read freezes the
/// text for every copy, so a text that a dissector completes later in the parse must not be read before the parse ended.</para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct LazyString : IEquatable<LazyString>, IComparable<LazyString>
{
    #region Storage and construction

    /// <summary>Empty string singleton (non-null, zero-length).</summary>
    public static readonly LazyString Empty = new(string.Empty);

    /// <summary>
    /// The one wrapped reference: <see langword="null"/> is an absent text; a <see cref="string"/> is a direct value or the
    /// cached result of an evaluated deferred text; an <c>ILazyTextFormat</c> is a deferred text (<c>LazyTextFunc</c> from
    /// <see cref="Lazy"/>, <c>LazyTextFormat&lt;TState&gt;</c> from <see cref="FormatLazy{TState}"/>) that remembers its string.
    /// </summary>
    /// <remarks>
    /// A single field keeps the struct the size of a pointer and lets the cache replace the deferred object atomically
    /// without a second slot or a wrapper object.
    /// </remarks>
    private readonly object? _Value;

    /// <summary>Creates a lazy text from an already built string.</summary>
    /// <param name="value">The text; stored as-is (no copy). <see langword="null"/> gives an absent text (see <see cref="IsNull"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LazyString(string value)
    {
        _Value = value;
    }

    /// <summary>Wraps a raw reference without checking it; used by the factory methods, which pass only valid wrapped values.</summary>
    /// <param name="value">The reference to wrap (see <c>_Value</c>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LazyString(object? value)
    {
        _Value = value;
    }

    /// <summary>
    /// Creates a lazily evaluated text. The string is built once per call of this method, however many copies of the result
    /// are read (the wrapper remembers it) and is also cached per storage location. The delegate (and its closure, if it
    /// captures) and the small wrapper are the per-call allocations. Prefer <see cref="FormatLazy{TState}"/> with a static
    /// lambda on hot paths.
    /// </summary>
    /// <param name="factory">
    /// Builds the text. May run more than once under concurrent first reads and must tolerate that. Must not return
    /// <see langword="null"/>; a <see langword="null"/> result is stored as an empty text.
    /// </param>
    /// <returns>A deferred text; evaluated on the first read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LazyString Lazy(Func<string> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(new LazyTextFunc(factory));
    }

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
    /// <b>Accepted cost:</b> this is one small object per call (state, formatter, and the 8-byte memo that
    /// lets every copy share one build), also for packets whose info text is never
    /// read; the packet-info pattern of the protocol parsers (IPv4, UDP, TCP and others) pays it once per packet on ingest.
    /// Rejected: a struct payload that the field value storage holds inline, because it needs a fixed state size and a cached
    /// formatter delegate per call site, which changes the shared value layout. The object is gen-0 garbage that
    /// dies with the packet unless the info text is read, so the cost is allocation volume, not retention. A protocol that
    /// would pay it twice per packet should build the text from a single state (one call), as the TCP layer does.
    /// </remarks>
    /// <param name="state">The captured state; boxed once.</param>
    /// <param name="formatter">
    /// Builds the text from <paramref name="state"/>. Must not return <see langword="null"/>; a <see langword="null"/> result
    /// is stored as an empty text.
    /// </param>
    /// <typeparam name="TState">The captured state type (usually a value tuple).</typeparam>
    /// <returns>A deferred text; evaluated on the first read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="formatter"/> is <see langword="null"/>.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LazyString FormatLazy<TState>(TState state, Func<TState, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        return new(new LazyTextFormat<TState>(state, formatter));
    }

    /// <summary>Rebuilds a lazy text from <see cref="RawValue"/>; for union-style storage that cannot hold the struct itself.</summary>
    /// <param name="value">A value read from <see cref="RawValue"/>: <see langword="null"/>, a string, or a deferred text object.</param>
    /// <returns>A lazy text that wraps <paramref name="value"/>; absent for <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a value <see cref="RawValue"/> can return.</exception>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static LazyString FromRawValue(object? value)
    {
        // Only what RawValue hands out round-trips; anything else would read as an empty text silently.
        if (value is not (null or string or ILazyTextFormat))
        {
            throw new ArgumentException("A raw lazy text must be null, a string, or a value read from RawValue.", nameof(value));
        }

        return new(value);
    }

    #endregion

    #region Reading and combining the text

    /// <summary>The wrapped reference, for union-style storage that cannot hold the struct itself.</summary>
    /// <remarks>Returns <see langword="null"/> (absent), a string, or a deferred text object; hand it back to <see cref="FromRawValue"/>.</remarks>
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
    public bool IsLazy => _Value is ILazyTextFormat;

    /// <summary>Whether the value is a direct string, absent, or the cached result of an evaluated text.</summary>
    public bool IsEvaluated => _Value is not ILazyTextFormat;

    /// <summary>The text as a character span. Evaluates a deferred text.</summary>
    public ReadOnlySpan<char> AsSpan => AsString.AsSpan();

    /// <summary>
    /// Concatenates this text with <paramref name="other"/>. Both sides are evaluated immediately (the result is a direct
    /// string); an absent or empty side returns the other side unchanged.
    /// </summary>
    /// <param name="other">The text to put after this one.</param>
    /// <returns>
    /// The concatenation as a direct string; <paramref name="other"/> itself (still deferred) when this side is absent or empty;
    /// this text itself when <paramref name="other"/> is absent or empty.
    /// </returns>
    /// <remarks>Cost: one string allocation of exactly the result length when both sides are non-empty; none otherwise.</remarks>
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
    /// <param name="other">The text to put before this one.</param>
    /// <returns>The concatenation, with the same rules as <see cref="Append"/>.</returns>
    public LazyString Prepend(LazyString other) => other.Append(this);

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
    /// <param name="value">The text; <see langword="null"/> gives an absent text.</param>
    /// <returns>A lazy text that holds <paramref name="value"/> directly.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LazyString(string value) => new(value);

    /// <summary>Implicit conversion to <see cref="string"/> (evaluates a deferred text).</summary>
    /// <param name="value">The lazy text to read.</param>
    /// <returns>The text; the empty string when <paramref name="value"/> is absent.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator string(LazyString value) => value.AsString;

    /// <summary>Whether two texts are equal (ordinal; an absent text equals only another absent text).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when the texts are equal; evaluates deferred texts.</returns>
    public static bool operator ==(LazyString left, LazyString right) => left.Equals(right);

    /// <summary>Whether two texts differ (see <see cref="Equals(LazyString)"/>).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when the texts are not equal; evaluates deferred texts.</returns>
    public static bool operator !=(LazyString left, LazyString right) => !left.Equals(right);

    /// <summary>Whether <paramref name="left"/> sorts before <paramref name="right"/> (see <see cref="CompareTo(LazyString)"/>).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is less than <paramref name="right"/>.</returns>
    public static bool operator <(LazyString left, LazyString right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> sorts before or equal to <paramref name="right"/> (see <see cref="CompareTo(LazyString)"/>).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is less than or equal to <paramref name="right"/>.</returns>
    public static bool operator <=(LazyString left, LazyString right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> sorts after <paramref name="right"/> (see <see cref="CompareTo(LazyString)"/>).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is greater than <paramref name="right"/>.</returns>
    public static bool operator >(LazyString left, LazyString right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> sorts after or equal to <paramref name="right"/> (see <see cref="CompareTo(LazyString)"/>).</summary>
    /// <param name="left">The first text.</param>
    /// <param name="right">The second text.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</returns>
    public static bool operator >=(LazyString left, LazyString right) => left.CompareTo(right) >= 0;

    #endregion

    #region Evaluation and caching

    /// <summary>
    /// Reads the wrapped value once with acquire semantics. <see cref="AsString"/> and <see cref="TryGetString(out string, out LazyStringError)"/>
    /// call this exactly once per call and hand the observed object to the helpers; the helpers never read the field again.
    /// </summary>
    /// <returns>The wrapped reference as it was when read: null, a string, or a deferred text object.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object? _ReadValue() => Volatile.Read(ref Unsafe.AsRef(in _Value));

    /// <summary>
    /// Runs the deferred object that the caller observed. Callers guarantee an <c>ILazyTextFormat</c>; anything else is a
    /// broken invariant.
    /// </summary>
    /// <param name="deferred">The deferred text object that the caller observed.</param>
    /// <returns>The string the object builds (or remembered).</returns>
    /// <exception cref="UnreachableException"><paramref name="deferred"/> is not an <c>ILazyTextFormat</c>.</exception>
    private static string _Run(object deferred) => deferred is ILazyTextFormat format
        ? format.Evaluate()
        : throw new UnreachableException("LazyString evaluation requires a deferred object.");

    /// <summary>
    /// Publishes <paramref name="evaluated"/> in place of <paramref name="deferred"/> and returns the string that is cached
    /// afterwards. Winner takes all: if this compare-exchange wins, the own result is cached and returned; otherwise the
    /// string another thread cached first is returned and the own result is discarded. If the location holds neither the
    /// deferred object nor a string (the storage was reassigned), the own result is returned and nothing is persisted.
    /// </summary>
    /// <param name="deferred">The deferred object that the caller observed; the only object the compare-exchange may replace.</param>
    /// <param name="evaluated">The string this call built from <paramref name="deferred"/>.</param>
    /// <returns>The string that is cached afterwards: <paramref name="evaluated"/> on a win, the other thread's string on a loss.</returns>
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
    /// <param name="deferred">The deferred object that the caller observed.</param>
    /// <returns>The one cached string.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string _EvaluateAndCache(object deferred) => _Publish(deferred, _Run(deferred));

    /// <summary>
    /// Evaluates the observed deferred object for <see cref="TryGetString(out string, out LazyStringError)"/>. A fault is
    /// turned into a cached empty string so it is not retried forever; <paramref name="result"/> is always the string cached
    /// afterwards, while the return value and <paramref name="error"/> describe only this call's own factory run.
    /// </summary>
    /// <param name="deferred">The deferred object that the caller observed.</param>
    /// <param name="result">The string cached afterwards.</param>
    /// <param name="error">The fault of this call's own factory run, or default.</param>
    /// <returns><see langword="true"/> when this call's own factory run did not throw.</returns>
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
