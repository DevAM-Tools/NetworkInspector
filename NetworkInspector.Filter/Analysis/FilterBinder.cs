// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// Binding steps shared by the field-tree code generator (<see cref="EvalTreeCodegen"/>) and the
/// tree-free observer compiler (<see cref="FilterObserverCompiler"/>).
/// <para>
/// Both back ends must turn the same AST node into the same accessor, regular expression and flank
/// tracker; otherwise a <see cref="Filter"/> and a <see cref="FilterObserver"/> of one expression
/// disagree. Keeping the steps in one place makes that drift impossible.
/// </para>
/// <para><b>Thread-safety:</b> stateless; every method is safe to call from any thread.</para>
/// </summary>
internal static class FilterBinder
{
    #region Regular expressions

    // Threat: a user-written pattern with catastrophic backtracking stalls the per-packet path (denial of service).
    // Mitigation: the linear-time engine is preferred (see TryBuildRegex); this timeout only bounds the remaining
    // backtracking-only patterns, and a timeout is reported as a runtime filter error instead of hanging.
    // Constant default of one second; a caller changes it per compilation through FilterCompileOptions.RegexTimeout, which
    // is range-checked once per compilation by TryValidateRegexTimeout. Residual risk: a backtracking-only pattern can
    // still burn up to this time per packet.
    //
    // Range: greater than zero and at most MaxRegexTimeout (the largest value the .NET Regex type accepts, a fact of that
    // type), or Regex.InfiniteMatchTimeout. Threat: a zero, negative or huge value is rejected by the Regex constructor
    // with an exception that would otherwise be misreported as a bad pattern. Configurable by the host code per
    // compilation, secure by default (one second); the infinite value is an explicit opt-out of the bound that was
    // accepted before this check existed and stays accepted so no caller changes behavior.

    /// <summary>Default bound on <c>matches</c> evaluation for patterns that need the backtracking engine.</summary>
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Largest finite match timeout the <see cref="Regex"/> type accepts: <c>int.MaxValue - 1</c> milliseconds.</summary>
    public static readonly TimeSpan MaxRegexTimeout = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    /// <summary>
    /// Checks a <c>matches</c> timeout against the range <see cref="Regex"/> accepts, so a bad option is reported as such
    /// instead of surfacing later as an exception from the regular-expression constructor.
    /// </summary>
    /// <param name="timeout">The timeout to check.</param>
    /// <param name="failure">Receives an error naming the allowed range when the timeout is out of range.</param>
    /// <returns><see langword="true"/> for <see cref="Regex.InfiniteMatchTimeout"/> or a value in (0, <see cref="MaxRegexTimeout"/>].</returns>
    public static bool TryValidateRegexTimeout(TimeSpan timeout, [NotNullWhen(false)] out FilterError? failure)
    {
        if (timeout == Regex.InfiniteMatchTimeout || (timeout > TimeSpan.Zero && timeout <= MaxRegexTimeout))
        {
            failure = null;
            return true;
        }

        // Position and length are -1: the value comes from FilterCompileOptions, not from the expression text.
        string actual = timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
        string maximum = ((long)MaxRegexTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        failure = FilterError.InvalidValue(
            "FilterCompileOptions.RegexTimeout " + actual + " ms is out of range: it must be greater than 0 ms and at most "
            + maximum + " ms, or Regex.InfiniteMatchTimeout (-1 ms) for no bound",
            -1,
            -1);
        return false;
    }

    /// <summary>
    /// Builds the regular expression for a <c>matches</c> pattern.
    /// <para>
    /// <see cref="RegexOptions.NonBacktracking"/> is tried first: it runs in time linear in the input,
    /// so an adversarial pattern cannot stall the per-packet path and the timeout never fires. Patterns
    /// that engine does not support (lookaround, backreferences, atomic groups, conditionals) fall back to
    /// the backtracking interpreter bounded by <paramref name="timeout"/>. For every pattern both engines
    /// accept, <c>IsMatch</c> gives the same verdict, so the choice is not observable apart from cost.
    /// </para>
    /// </summary>
    /// <param name="pattern">The pattern as written in the expression.</param>
    /// <param name="timeout">Match timeout for the backtracking fallback.</param>
    /// <param name="regex">Receives the compiled pattern.</param>
    /// <returns><see langword="false"/> when the pattern is not a valid .NET regular expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> fails <see cref="TryValidateRegexTimeout"/>; callers check it first.
    /// </exception>
    public static bool TryBuildRegex(string pattern, TimeSpan timeout, [NotNullWhen(true)] out Regex? regex)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // An out-of-range timeout is a caller bug: the compile entry points reject it as an option error before parsing.
        // Failing here, before any engine runs, keeps the Regex constructor's ArgumentOutOfRangeException from being
        // swallowed by the invalid-pattern handlers below, which would blame the user's pattern for a bad option.
        if (!TryValidateRegexTimeout(timeout, out FilterError? timeoutFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeoutFailure.Message);
        }

        // Attempt 1: the linear-time engine. Construction parses the full pattern first, so bad syntax surfaces here as
        // an ArgumentException (RegexParseException) before any engine restriction is checked; unlike matching, parsing
        // cannot time out. No timeout is passed: this engine's matching time is linear in the input.
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            return true;
        }
        catch (NotSupportedException)
        {
            // Valid .NET syntax the linear engine cannot run — keep the backtracking engine below.
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }

        // Attempt 2: backtracking interpreter for syntax the linear engine rejects, bounded by the caller's timeout.
        // The timeout was range-checked at entry, so an ArgumentException here can only come from the pattern.
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, timeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }

    #endregion

    #region Accessors

    /// <summary>
    /// Resolves an operand name to a value source and builds the accessor that derives the predicate input:
    /// the raw value, a byte slice, or the length.
    /// </summary>
    /// <remarks>
    /// Bind errors point at the name itself, not at the whole <c>len(…)</c> or <c>name[a:b]</c> operand, so an editor can
    /// underline exactly the misspelled name and replace it with <see cref="FilterError.Suggestion"/>.
    /// </remarks>
    public static FilterResult<ValueAccessor> BuildAccessor(SymbolResolver resolver, OperandNode operand)
    {
        int namePosition = operand is LengthOperandNode length
            ? length.NamePosition
            : operand.Position;
        FilterResult<FilterSymbol> resolved = resolver.ResolveValue(operand.Name, namePosition, operand.Name.Length);
        if (!resolved.TryGetValue(out FilterSymbol? symbol))
        {
            return FilterResult.Fail<ValueAccessor>(resolved.Error);
        }

        return operand switch
        {
            SliceOperandNode slice => ValueAccessor.Slice(symbol.Fields, slice.Start, slice.End),
            LengthOperandNode => ValueAccessor.Length(symbol.Fields),
            _ => ValueAccessor.Direct(symbol.Fields),
        };
    }

    #endregion

    #region Flank

    /// <summary>
    /// Resolves the tracked field of a <c>flank(…)</c> expression, checks the <c>by:</c> integer rule and
    /// creates the tracker. The <c>when:</c> gate is left to the caller because each back end compiles it
    /// into its own form.
    /// </summary>
    /// <param name="resolver">Resolver of the current compilation.</param>
    /// <param name="node">The parsed flank expression.</param>
    /// <param name="symbol">Receives the bound field so the caller can register the watched ids.</param>
    public static FilterResult<FlankRuntime> BuildFlank(SymbolResolver resolver, FlankNode node, out FilterSymbol? symbol)
    {
        // Errors about the tracked field point at its name (see BuildAccessor).
        FilterResult<FilterSymbol> resolved = resolver.ResolveValue(node.FieldName, node.FieldPosition, node.FieldName.Length);
        if (!resolved.TryGetValue(out symbol))
        {
            return FilterResult.Fail<FlankRuntime>(resolved.Error);
        }

        if (node.By is not null)
        {
            FilterError? typeError = resolver.CheckIntegerFields(symbol, node.FieldName, node.FieldPosition, node.FieldName.Length);
            if (typeError is not null)
            {
                symbol = null;
                return FilterResult.Fail<FlankRuntime>(typeError);
            }
        }

        return new FlankRuntime(
            ValueAccessor.Direct(symbol.Fields),
            node.From,
            node.To,
            node.By,
            node.IsAnyChange,
            node.Window);
    }

    #endregion
}
