// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Parser;

/// <summary>Covers grammar coverage, removed-feature rejection and field-span callbacks.</summary>
internal sealed class FilterParserTests
{
    #region Helpers

    private static FilterProgram _Parse(string source, FilterFieldNameSpanCallback? callback = null)
    {
        FilterLexer lexer = new(source);
        FilterResult<List<Token>> tokens = lexer.Tokenize();
        if (!tokens.TryGetValue(out List<Token>? tokenList))
        {
            throw new InvalidOperationException($"Lexing '{source}' failed: {tokens.Error}");
        }

        FilterParser parser = new(tokenList, source, callback);
        FilterResult<FilterProgram> program = parser.Parse();
        if (!program.TryGetValue(out FilterProgram? parsed))
        {
            throw new InvalidOperationException($"Parsing '{source}' failed: {program.Error}");
        }
        return parsed;
    }

    private static FilterError _ParseError(string source, FilterFieldNameSpanCallback? callback = null)
    {
        FilterLexer lexer = new(source);
        FilterResult<List<Token>> tokens = lexer.Tokenize();
        if (!tokens.TryGetValue(out List<Token>? tokenList))
        {
            return tokens.Error;
        }

        FilterParser parser = new(tokenList, source, callback);
        FilterResult<FilterProgram> program = parser.Parse();
        if (program.IsSuccess)
        {
            throw new InvalidOperationException($"Expected '{source}' to fail parsing.");
        }
        return program.Error;
    }

    #endregion

    #region Structure

    [Test]
    public async Task Parse_Presence_ProducesPresenceNode()
    {
        FilterProgram program = _Parse("udp");

        await Assert.That(program.Root).IsTypeOf<PresenceNode>();
        await Assert.That(program.Features).IsEqualTo(FilterFeature.Classic);
        await Assert.That(program.IsStateful).IsFalse();
        await Assert.That(program.Original).IsEqualTo("udp");
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    public async Task Parse_BooleanConstant_ProducesConstantNode(string source)
    {
        FilterProgram program = _Parse(source);

        await Assert.That(program.Root).IsTypeOf<BoolConstantNode>();
    }

    [Test]
    public async Task Parse_Negation_ProducesNotNode()
    {
        FilterProgram program = _Parse("not udp");

        await Assert.That(program.Root).IsTypeOf<NotNode>();
    }

    [Test]
    public async Task Parse_AndBindsTighterThanOr()
    {
        FilterProgram program = _Parse("udp || tcp && ip");

        LogicalNode root = (LogicalNode)program.Root;
        await Assert.That(root.Op).IsEqualTo(LogicalOp.Or);
        await Assert.That(root.Right).IsTypeOf<LogicalNode>();
    }

    [Test]
    public async Task Parse_Parentheses_OverridePrecedence()
    {
        FilterProgram program = _Parse("(udp || tcp) && ip");

        LogicalNode root = (LogicalNode)program.Root;
        await Assert.That(root.Op).IsEqualTo(LogicalOp.And);
        await Assert.That(root.Left).IsTypeOf<LogicalNode>();
    }

    [Test]
    public async Task Parse_Comparison_ProducesCompareNode()
    {
        FilterProgram program = _Parse("udp.srcport == 53");

        CompareNode compare = (CompareNode)program.Root;
        await Assert.That(compare.Op).IsEqualTo(CompareOp.Equal);
        await Assert.That(compare.Left.Name).IsEqualTo("udp.srcport");
        await Assert.That(compare.Right.TryGetAsU64(out ulong port)).IsTrue();
        await Assert.That(port).IsEqualTo(53UL);
    }

    [Test]
    public async Task Parse_Slice_ProducesSliceOperand()
    {
        FilterProgram program = _Parse("eth.src[0:3] == 00:11:22");

        CompareNode compare = (CompareNode)program.Root;
        SliceOperandNode slice = (SliceOperandNode)compare.Left;
        await Assert.That(slice.Start).IsEqualTo(0);
        await Assert.That(slice.End).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_Length_ProducesLengthOperand()
    {
        FilterProgram program = _Parse("len(udp.payload) > 4");

        CompareNode compare = (CompareNode)program.Root;
        await Assert.That(compare.Left).IsTypeOf<LengthOperandNode>();
        await Assert.That(compare.Left.Name).IsEqualTo("udp.payload");
    }

    [Test]
    public async Task Parse_InSet_ProducesSetNode()
    {
        FilterProgram program = _Parse("udp.port in {53, 67, 68}");

        InSetNode set = (InSetNode)program.Root;
        await Assert.That(set.Values.Length).IsEqualTo(3);
        await Assert.That(set.ValueArray.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_InRange_ProducesRangeNode()
    {
        FilterProgram program = _Parse("udp.port in 1024..65535");

        InRangeNode range = (InRangeNode)program.Root;
        await Assert.That(range.Low.TryGetAsU64(out ulong low)).IsTrue();
        await Assert.That(low).IsEqualTo(1024UL);
        await Assert.That(range.High.TryGetAsU64(out ulong high)).IsTrue();
        await Assert.That(high).IsEqualTo(65535UL);
    }

    [Test]
    [Arguments("contains", StringOp.Contains)]
    [Arguments("matches", StringOp.Matches)]
    public async Task Parse_StringPredicate_ProducesPredicateNode(string keyword, StringOp expected)
    {
        FilterProgram program = _Parse($"udp.checksum.status {keyword} \"good\"");

        StringPredicateNode predicate = (StringPredicateNode)program.Root;
        await Assert.That(predicate.Op).IsEqualTo(expected);
        await Assert.That(predicate.Pattern).IsEqualTo("good");
    }

    #endregion

    #region Scope

    [Test]
    public async Task Parse_Scope_ProducesExistentialScope()
    {
        FilterProgram program = _Parse("$udp { udp.srcport == 53 }");

        ScopeNode scope = (ScopeNode)program.Root;
        await Assert.That(scope.Name).IsEqualTo("udp");
        await Assert.That(scope.Occurrence).IsNull();
        await Assert.That(program.Features.HasFlag(FilterFeature.Scope)).IsTrue();
    }

    [Test]
    public async Task Parse_IndexedScope_CapturesOccurrence()
    {
        FilterProgram program = _Parse("$udp[1] { udp.srcport == 53 }");

        ScopeNode scope = (ScopeNode)program.Root;
        await Assert.That(scope.Occurrence).IsEqualTo(1);
    }

    #endregion

    #region Flank

    [Test]
    public async Task Parse_FlankChanged_SetsAnyChange()
    {
        FilterProgram program = _Parse("flank(ip.ttl, changed, within: 5s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.IsAnyChange).IsTrue();
        await Assert.That(flank.Window.IsPacketCount).IsFalse();
        await Assert.That(flank.Window.Nanoseconds).IsEqualTo(5_000_000_000L);
        await Assert.That(program.IsStateful).IsTrue();
    }

    [Test]
    public async Task Parse_FlankEndpoints_CapturesBothSides()
    {
        FilterProgram program = _Parse("flank(ip.ttl, from: 64, to: <32, within: 10packets)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.From!.Value.Op).IsEqualTo(CompareOp.Equal);
        await Assert.That(flank.To!.Value.Op).IsEqualTo(CompareOp.LessThan);
        await Assert.That(flank.IsAnyChange).IsFalse();
        await Assert.That(flank.Window.IsPacketCount).IsTrue();
        await Assert.That(flank.Window.PacketCount).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_FlankWithoutEndpoints_DefaultsToAnyChange()
    {
        FilterProgram program = _Parse("flank(ip.ttl, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.IsAnyChange).IsTrue();
    }

    [Test]
    public async Task Parse_FlankWithGate_CapturesWhen()
    {
        FilterProgram program = _Parse("flank(ip.ttl, changed, within: 1s, when: udp)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.When).IsNotNull();
    }

    [Test]
    public async Task Parse_FlankWithoutWindow_ReportsError()
    {
        FilterError error = _ParseError("flank(ip.ttl, changed)");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
        await Assert.That(error.Message).Contains("within");
    }

    [Test]
    public async Task Parse_FlankChangedWithEndpoint_ReportsError()
    {
        FilterError error = _ParseError("flank(ip.ttl, changed, to: 1, within: 1s)");

        await Assert.That(error.Message).Contains("changed");
    }

    [Test]
    public async Task Parse_FlankBy_BareLiteral_IsEqual()
    {
        FilterProgram program = _Parse("flank(ip.ttl, by: 2, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.By).IsNotNull();
        await Assert.That(flank.By!.Value.Op).IsEqualTo(CompareOp.Equal);
        _ = flank.By.Value.Value.TryGetAsU64(out ulong value);
        await Assert.That(value).IsEqualTo(2UL);
        await Assert.That(flank.IsAnyChange).IsFalse();
        await Assert.That(flank.IsArmedMode).IsFalse();
    }

    [Test]
    public async Task Parse_FlankBy_GreaterEqual()
    {
        FilterProgram program = _Parse("flank(ip.ttl, by: >= 2, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.By!.Value.Op).IsEqualTo(CompareOp.GreaterEqual);
    }

    [Test]
    public async Task Parse_FlankBy_NegativeExact()
    {
        FilterProgram program = _Parse("flank(ip.ttl, by: -2, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.By!.Value.Op).IsEqualTo(CompareOp.Equal);
        await Assert.That(flank.By.Value.Value.Type).IsEqualTo(FieldType.I64);
        _ = flank.By.Value.Value.TryGetAsI64(out long value);
        await Assert.That(value).IsEqualTo(-2L);
    }

    [Test]
    public async Task Parse_FlankBy_LessEqualNegative()
    {
        FilterProgram program = _Parse("flank(ip.ttl, by: <= -3, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.By!.Value.Op).IsEqualTo(CompareOp.LessEqual);
        _ = flank.By.Value.Value.TryGetAsI64(out long value);
        await Assert.That(value).IsEqualTo(-3L);
    }

    [Test]
    public async Task Parse_FlankBy_WithFrom_Valid()
    {
        FilterProgram program = _Parse("flank(ip.ttl, from: 1, by: >= 2, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.From).IsNotNull();
        await Assert.That(flank.By).IsNotNull();
        await Assert.That(flank.IsArmedMode).IsTrue();
    }

    [Test]
    public async Task Parse_FlankFromTo_IsArmed()
    {
        FilterProgram program = _Parse("flank(ip.ttl, from: 1, to: 2, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.IsArmedMode).IsTrue();
    }

    [Test]
    public async Task Parse_FlankFromOnly_IsNotArmed()
    {
        FilterProgram program = _Parse("flank(ip.ttl, from: 1, within: 1s)");

        FlankNode flank = (FlankNode)program.Root;
        await Assert.That(flank.IsArmedMode).IsFalse();
    }

    [Test]
    public async Task Parse_FlankBy_WithTo_WithoutFrom_Error()
    {
        FilterError error = _ParseError("flank(ip.ttl, to: 2, by: 1, within: 1s)");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
        await Assert.That(error.Message).Contains("from");
    }

    [Test]
    public async Task Parse_FlankBy_WithChanged_Error()
    {
        FilterError error = _ParseError("flank(ip.ttl, changed, by: 1, within: 1s)");

        await Assert.That(error.Message).Contains("changed");
    }

    [Test]
    public async Task Parse_FlankBy_FloatLiteral_Error()
    {
        FilterError error = _ParseError("flank(ip.ttl, by: 1.5, within: 1s)");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
        await Assert.That(error.Message).Contains("integer");
    }

    [Test]
    public async Task Parse_FlankUnknownArgument_ReportsError()
    {
        FilterError error = _ParseError("flank(ip.ttl, nonsense: 1, within: 1s)");

        await Assert.That(error.Message).Contains("nonsense");
    }

    [Test]
    public async Task Parse_FlankBadWindow_ReportsError()
    {
        FilterError error = _ParseError("flank(ip.ttl, changed, within: 5)");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
    }

    #endregion

    #region Removed features

    [Test]
    [Arguments("seq(udp, tcp)")]
    [Arguments("stream(udp)")]
    [Arguments("window(5)")]
    [Arguments("nav(udp)")]
    [Arguments("let x")]
    [Arguments("where udp")]
    [Arguments("step udp")]
    public async Task Parse_RemovedFeature_ReportsUnsupported(string source)
    {
        FilterError error = _ParseError(source);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.UnsupportedFeature);
    }

    #endregion

    #region Syntax errors

    [Test]
    [Arguments("udp &&")]
    [Arguments("(udp")]
    [Arguments("udp == ")]
    [Arguments("udp.port in {")]
    [Arguments("udp.port in 1..")]
    [Arguments("udp.port in 1 2")]
    [Arguments("udp contains 5")]
    [Arguments("$")]
    [Arguments("$udp")]
    [Arguments("$udp[x] { udp }")]
    [Arguments("$udp[0] udp")]
    [Arguments("flank")]
    [Arguments("flank(")]
    [Arguments("flank(ip.ttl, within 5s)")]
    [Arguments("flank(ip.ttl, changed, within: 5s")]
    [Arguments("eth.src[3:1] == 00:11")]
    [Arguments("len(5)")]
    [Arguments("len(udp.payload")]
    [Arguments("udp tcp")]
    [Arguments("udp.payload[0:2]")]
    [Arguments("udp matches \"(\"")]
    [Arguments("udp || ")]
    [Arguments("!")]
    [Arguments("(&&")]
    [Arguments("udp.port in {53")]
    [Arguments("udp.port in ..5")]
    [Arguments("53")]
    [Arguments("udp.payload[a:2] == 00")]
    [Arguments("udp.payload[\"a\":2] == 00")]
    [Arguments("udp.payload[0 2] == 00")]
    [Arguments("udp.payload[0:a] == 00")]
    [Arguments("udp.payload[0:2 == 00")]
    [Arguments("$udp[0 { udp }")]
    [Arguments("$udp { }")]
    [Arguments("$udp { udp ")]
    [Arguments("flank(ip.ttl, 5, within: 1s)")]
    [Arguments("flank(ip.ttl, from: , within: 1s)")]
    [Arguments("flank(ip.ttl, by: , within: 1s)")]
    [Arguments("flank(ip.ttl, to: , within: 1s)")]
    [Arguments("flank(ip.ttl, to: < , within: 1s)")]
    [Arguments("flank(ip.ttl, changed, within: 1s, when: )")]
    [Arguments("$udp[99999999999999999999999] { udp }")]
    [Arguments("$udp[4294967296] { udp }")]
    public async Task Parse_Malformed_ReportsError(string source)
    {
        FilterError error = _ParseError(source);

        await Assert.That(error.HasPosition).IsTrue();
    }

    #endregion

    #region Name spans

    [Test]
    public async Task Parse_ReportsFieldAndProtocolSpans()
    {
        List<(string Name, FilterFieldNameKind Kind)> spans = [];
        void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            spans.Add((expression.Slice(start, length).ToString(), kind));

        _ = _Parse("udp && udp.srcport == 53", Callback);

        await Assert.That(spans.Count).IsEqualTo(2);
        await Assert.That(spans[0]).IsEqualTo(("udp", FilterFieldNameKind.ProtocolName));
        await Assert.That(spans[1]).IsEqualTo(("udp.srcport", FilterFieldNameKind.FieldPath));
    }

    [Test]
    public async Task Parse_ReportsScopeAnchorSpan()
    {
        List<FilterFieldNameKind> kinds = [];
        void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            kinds.Add(kind);

        _ = _Parse("$udp { udp.srcport == 53 }", Callback);

        await Assert.That(kinds[0]).IsEqualTo(FilterFieldNameKind.ScopeAnchor);
    }

    [Test]
    public async Task Parse_ReportsLengthOperandNameOnly()
    {
        string? reported = null;
        void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            reported = expression.Slice(start, length).ToString();

        _ = _Parse("len(udp.payload) > 1", Callback);

        await Assert.That(reported).IsEqualTo("udp.payload");
    }

    [Test]
    public async Task Parse_IncompleteExpression_ReportsTrailingName()
    {
        List<(string Name, FilterFieldNameKind Kind)> spans = [];
        void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            spans.Add((expression.Slice(start, length).ToString(), kind));

        _ = _ParseError("len(tcp.po", Callback);

        await Assert.That(spans).Contains(("tcp.po", FilterFieldNameKind.Incomplete));
    }

    [Test]
    public async Task Parse_ThrowingCallback_ReportsCallbackFailed()
    {
        static void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            throw new InvalidOperationException("boom");

        FilterError error = _ParseError("udp.srcport == 53", Callback);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.CallbackFailed);
    }

    [Test]
    public async Task Parse_ThrowingCallbackOnMalformedExpression_PrefersTheCallbackFailure()
    {
        static void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            throw new InvalidOperationException("boom");

        FilterError error = _ParseError("udp.srcport == ", Callback);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.CallbackFailed);
    }

    [Test]
    public async Task Parse_FlankFieldName_IsReported()
    {
        List<FilterFieldNameKind> kinds = [];
        void Callback(ReadOnlySpan<char> expression, int start, int length, FilterFieldNameKind kind) =>
            kinds.Add(kind);

        _ = _Parse("flank(ip.ttl, changed, within: 1s)", Callback);

        await Assert.That(kinds).Contains(FilterFieldNameKind.FieldPath);
    }

    #endregion
}
