// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Analysis;

/// <summary>Covers the presence-only abstraction used for index pruning.</summary>
internal sealed class DependencyAnalyzerTests
{
    #region Helpers

    private static DependencyNode _Analyze(string expression, IStack stack)
    {
        FilterLexer lexer = new(expression);
        List<Token> tokens = lexer.Tokenize().Value;
        FilterProgram program = new FilterParser(tokens, expression, null).Parse().Value;
        return DependencyAnalyzer.Analyze(program, new SymbolResolver(stack));
    }

    #endregion

    #region Shapes

    [Test]
    public async Task Analyze_Presence_ProducesLeaf()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("udp", stack);

        await Assert.That(node).IsTypeOf<DependencyLeaf>();
        await Assert.That(((DependencyLeaf)node).Symbol.Name).IsEqualTo("udp");
    }

    [Test]
    [Arguments("udp.srcport == 53")]
    [Arguments("udp.srcport in {1, 2}")]
    [Arguments("udp.srcport in 1..2")]
    [Arguments("dns.qry.name contains \"a\"")]
    public async Task Analyze_ValuePredicates_ProduceLeaves(string expression)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze(expression, stack);

        await Assert.That(node).IsTypeOf<DependencyLeaf>();
    }

    [Test]
    public async Task Analyze_Conjunction_ProducesAll()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("udp && tcp", stack);

        await Assert.That(node).IsTypeOf<DependencyAll>();
        await Assert.That(((DependencyAll)node).Children.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Analyze_Disjunction_ProducesAny()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("udp || tcp", stack);

        await Assert.That(node).IsTypeOf<DependencyAny>();
        await Assert.That(((DependencyAny)node).Children.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Analyze_Scope_RequiresAnchorAndBody()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("$udp { udp.srcport == 53 }", stack);

        await Assert.That(node).IsTypeOf<DependencyAll>();
    }

    #endregion

    #region Unknowns

    [Test]
    [Arguments("!udp")]
    [Arguments("true")]
    [Arguments("false")]
    public async Task Analyze_NegationAndConstants_ProduceUnknown(string expression)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze(expression, stack);

        await Assert.That(node).IsSameReferenceAs(DependencyUnknown.Instance);
    }

    [Test]
    public async Task Analyze_StatefulProgram_ProducesUnknown()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("flank(ip.ttl, changed, within: 1s)", stack);

        await Assert.That(node).IsSameReferenceAs(DependencyUnknown.Instance);
    }

    [Test]
    public async Task Analyze_UnresolvableName_ProducesUnknown()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        DependencyNode node = _Analyze("nosuchprotocol", stack);

        await Assert.That(node).IsSameReferenceAs(DependencyUnknown.Instance);
    }

    #endregion
}
