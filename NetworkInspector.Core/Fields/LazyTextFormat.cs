// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// A deferred text that knows how to build its string and remembers it. Implemented by <see cref="LazyTextFormat{TState}"/> and
/// <see cref="LazyTextFunc"/>; <see cref="LazyString"/> tests for this interface to tell a deferred text from a direct string.
/// </summary>
/// <remarks>
/// <b>Why the object remembers the result:</b> a <see cref="LazyString"/> is a one-reference struct that is copied freely (into
/// a staging buffer, a cache, an observer). Every copy points to the same deferred object but caches only into its own
/// location, so without a memo each copy that is read would run the formatter again. With the memo the formatter result is
/// built once per deferred object, however many copies exist and whichever copy is read first.
/// </remarks>
internal interface ILazyTextFormat
{
    /// <summary>
    /// Builds the string on the first call and returns the same string instance on every later call, from any thread.
    /// Concurrent first calls may run the formatter more than once; exactly one result is kept and every caller returns it.
    /// </summary>
    /// <returns>The built string.</returns>
    string Evaluate();
}

/// <summary>
/// The box that <see cref="LazyString.FormatLazy{TState}"/> allocates: a captured state value, a formatter delegate, and the
/// memo of the built string.
/// </summary>
/// <remarks>
/// <para>
/// The formatter is static by convention, so this box is the only allocation until the text is read; with a capturing
/// lambda the closure object would come on top. The state is a value tuple in practice, boxed once here.
/// </para>
/// <para>
/// <b>Thread safety:</b> the state and the formatter are immutable after construction; the memo is published once with a
/// compare-exchange and read with an acquire load, so a reader that sees the string sees a fully built one. The formatter must
/// tolerate concurrent calls. If it throws, nothing is remembered and the next call tries again.
/// </para>
/// </remarks>
/// <typeparam name="TState">The captured state (usually a ValueTuple).</typeparam>
/// <param name="state">The state handed to <paramref name="formatter"/> on evaluation; boxed with this object.</param>
/// <param name="formatter">
/// Builds the text from <paramref name="state"/>; static by convention. Not validated here: <see cref="LazyString.FormatLazy{TState}"/> rejects null.
/// </param>
internal sealed class LazyTextFormat<TState>(TState state, Func<TState, string> formatter) : ILazyTextFormat
{
    /// <summary>The captured state; immutable after construction.</summary>
    private readonly TState _State = state;

    /// <summary>The formatter that builds the text from <see cref="_State"/>; immutable after construction.</summary>
    private readonly Func<TState, string> _Formatter = formatter;

    /// <summary>The built string; null until the first successful evaluation. The first publisher wins (see <see cref="Evaluate"/>).</summary>
    /// <remarks>
    /// Perf: 8 bytes more per deferred text, allocated at parse time for every text; it saves a second formatter run for every
    /// copy of the LazyString that is read (staging buffers, observers, the cache). Removing it brings back one build per copy.
    /// </remarks>
    private string? _Result;

    /// <inheritdoc/>
    /// <remarks>An exception from the formatter propagates to the caller and nothing is remembered.</remarks>
    public string Evaluate()
    {
        // Acquire load: a non-null memo is a complete string published by a compare-exchange.
        string? remembered = Volatile.Read(ref _Result);
        if (remembered is not null)
        {
            return remembered;
        }

        // Winner takes all: a loser of the race discards its own string and returns the winner's, so every copy of the
        // LazyString that points to this box hands out one string instance.
        string built = _Formatter(_State)
            ?? string.Empty; // A formatter must not return null; a null result is stored as an empty text.
        return Interlocked.CompareExchange(ref _Result, built, null)
            ?? built;
    }
}

/// <summary>
/// The box that <see cref="LazyString.Lazy"/> allocates around a factory delegate, so a stateless deferred text is also built
/// once per object (a bare delegate cannot remember a result).
/// </summary>
/// <remarks>
/// Same memo and thread-safety contract as <see cref="LazyTextFormat{TState}"/>. Production code builds deferred texts with
/// <see cref="LazyString.FormatLazy{TState}"/> and a static lambda; this box exists for callers that already hold a delegate.
/// </remarks>
/// <param name="factory">Builds the text. Not validated here: <see cref="LazyString.Lazy"/> rejects null.</param>
internal sealed class LazyTextFunc(Func<string> factory) : ILazyTextFormat
{
    /// <summary>The delegate that builds the text; immutable after construction.</summary>
    private readonly Func<string> _Factory = factory;

    /// <summary>The built string; null until the first successful evaluation. The first publisher wins (see <see cref="Evaluate"/>).</summary>
    /// <remarks>
    /// Perf: 8 bytes more per deferred text, allocated at parse time for every text; it saves a second formatter run for every
    /// copy of the LazyString that is read (staging buffers, observers, the cache). Removing it brings back one build per copy.
    /// </remarks>
    private string? _Result;

    /// <inheritdoc/>
    /// <remarks>An exception from the factory propagates to the caller and nothing is remembered.</remarks>
    public string Evaluate()
    {
        string? remembered = Volatile.Read(ref _Result);
        if (remembered is not null)
        {
            return remembered;
        }

        string built = _Factory()
            ?? string.Empty; // A factory must not return null; a null result is stored as an empty text.
        return Interlocked.CompareExchange(ref _Result, built, null)
            ?? built;
    }
}
