// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators;

/// <summary>
/// Warns when a prebuilt <c>LazyString</c> (<c>LazyString.FormatLazy</c> / <c>LazyString.Lazy</c>) is passed as the last
/// argument of a MutField custom-text append: that text is built in the caller and cannot be skipped on
/// <c>FieldTreeMode.Skip</c>; pass the fragments instead. Emits no source.
/// </summary>
/// <remarks>
/// Thread safety: this type is stateless. Multiple instances may run concurrently in one
/// compiler invocation.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class LazyAppendDiagnostics : IIncrementalGenerator
{
    #region Diagnostics

    private const string _DiagCategory = "NetworkInspector.Generators";

    private static readonly DiagnosticDescriptor _DiagLazyAppend = new(
        id: "NIGEN015",
        title: "Prebuilt LazyString passed to MutField custom-text append",
        messageFormat: "Pass format arguments to AppendWithCustomText so skip-tree can omit display-text allocation. Do not wrap them in LazyString.FormatLazy first.",
        category: _DiagCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    #endregion

    #region Public API

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Diagnostic?> diagnostics = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCustomTextAppend(node),
                transform: static (ctx, _) => TryDiag(ctx.Node))
            .Where(static d => d is not null);

        context.RegisterSourceOutput(diagnostics, static (spc, d) => spc.ReportDiagnostic(d!));
    }

    #endregion

    #region Predicate and transform

    /// <summary>
    /// True for the four MutField custom-text append names with at least three arguments.
    /// Excludes <c>SetPacketInfo</c> by name.
    /// </summary>
    internal static bool IsCustomTextAppend(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax inv || inv.ArgumentList.Arguments.Count < 3)
        {
            return false;
        }

        string name = _SimpleName(inv.Expression);
        return name is "AppendWithCustomText"
            or "PrependWithCustomText"
            or "InsertAfterWithCustomText"
            or "AppendLazyWithCustomText";
    }

    /// <summary>
    /// Returns NIGEN015 when the last argument is an invocation of <c>LazyString.FormatLazy</c> or <c>LazyString.Lazy</c>
    /// (receiver <c>LazyString</c>, optionally qualified by a <c>Fields</c> namespace such as
    /// <c>NetworkInspector.Core.Fields</c>). <c>SetPacketInfo</c> and plain fragments are not reported.
    /// </summary>
    internal static Diagnostic? TryDiag(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax inv || inv.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        ArgumentSyntax lastArg = inv.ArgumentList.Arguments[inv.ArgumentList.Arguments.Count - 1];
        if (lastArg.Expression is not InvocationExpressionSyntax inner)
        {
            return null;
        }

        string methodName = _SimpleName(inner.Expression);
        if (methodName is not ("FormatLazy" or "Lazy"))
        {
            return null;
        }

        if (!_IsLazyStringReceiver(inner.Expression))
        {
            return null;
        }

        return Diagnostic.Create(_DiagLazyAppend, lastArg.GetLocation());
    }

    #endregion

    #region Name helpers

    private static string _SimpleName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    // Syntax-only on purpose: the analyzer runs without a semantic model so it stays cheap. The receiver is matched by name
    // (LazyString or Fields.LazyString); a user type that is also called LazyString would be reported too, which is the
    // accepted false-positive risk (rejected alternative: a semantic-model lookup on every custom-text append).
    private static bool _IsLazyStringReceiver(ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax ma)
        {
            return false;
        }

        return ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "LazyString",
            MemberAccessExpressionSyntax outer =>
                outer.Name.Identifier.ValueText == "LazyString"
                && _SimpleName(outer.Expression) == "Fields",
            _ => false,
        };
    }

    #endregion
}
