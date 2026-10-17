// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// Binding steps shared by the field-tree code generator (<see cref="ExpressionTreeCodegen"/>) and the
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

    /// <summary>Default bound on <c>matches</c> evaluation for patterns that need the backtracking engine.</summary>
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromSeconds(1);

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
    public static bool TryBuildRegex(string pattern, TimeSpan timeout, [NotNullWhen(true)] out Regex? regex)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Construction parses the full pattern and throws on bad syntax; unlike matching it cannot time out.
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
