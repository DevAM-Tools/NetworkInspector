// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators;

/// <summary>
/// Warns when <c>ZA.Lazy</c> / <c>ZA.LazyInterpolated</c> is passed as the last argument of a
/// MutField custom-text append. Those factories run in the caller and cannot be skipped on
/// <c>FieldTreeMode.Skip</c>. Emits no source.
/// </summary>
/// <remarks>
/// Thread safety: this type is stateless. Multiple instances may run concurrently in one
/// compiler invocation.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ZaLazyAppendDiagnostics : IIncrementalGenerator
{
    #region Diagnostics

    private const string _DiagCategory = "NetworkInspector.Generators";

    private static readonly DiagnosticDescriptor _DiagZaLazyAppend = new(
        id: "NIGEN015",
        title: "ZA.Lazy passed to MutField custom-text append",
        messageFormat: "Pass format arguments to AppendWithCustomText so skip-tree can omit display-text allocation. Do not wrap them in ZA.Lazy first.",
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
    /// Returns NIGEN015 when the last argument is <c>ZA.Lazy</c> or <c>ZA.LazyInterpolated</c>.
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
        if (methodName is not ("Lazy" or "LazyInterpolated"))
        {
            return null;
        }

        if (!_IsZaReceiver(inner.Expression))
        {
            return null;
        }

        return Diagnostic.Create(_DiagZaLazyAppend, lastArg.GetLocation());
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

    private static bool _IsZaReceiver(ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax ma)
        {
            return false;
        }

        return ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "ZA",
            MemberAccessExpressionSyntax outer =>
                outer.Name.Identifier.ValueText == "ZA"
                && outer.Expression is IdentifierNameSyntax ns
                && ns.Identifier.ValueText == "ZeroAlloc",
            _ => false,
        };
    }

    #endregion
}
