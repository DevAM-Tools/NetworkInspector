// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Fields;

/// <summary>
/// A stateful deferred text: knows how to build its string. Implemented only by <see cref="LazyTextFormat{TState}"/>;
/// <see cref="LazyString"/> tests for this interface to tell a stateful deferred text from a stateless <c>Func&lt;string&gt;</c>.
/// </summary>
internal interface ILazyTextFormat
{
    /// <summary>Builds the string. May run more than once under concurrent first reads; must not have side effects.</summary>
    string Evaluate();
}

/// <summary>
/// The box that <see cref="LazyString.FormatLazy{TState}"/> allocates: a captured state value and a formatter delegate.
/// </summary>
/// <remarks>
/// The formatter is static by convention, so this box is the only allocation until the text is read; with a capturing
/// lambda the closure object would come on top. The state is a value tuple in practice, boxed once here. Thread safety:
/// immutable after construction, so concurrent <see cref="Evaluate"/> calls are safe as long as the formatter is.
/// </remarks>
/// <typeparam name="TState">The captured state (usually a ValueTuple).</typeparam>
internal sealed class LazyTextFormat<TState>(TState state, Func<TState, string> formatter) : ILazyTextFormat
{
    private readonly TState _State = state;

    private readonly Func<TState, string> _Formatter = formatter;

    /// <inheritdoc/>
    public string Evaluate() => _Formatter(_State);
}
