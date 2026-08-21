// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers compilation entry points, the always-match filter and binding errors.</summary>
internal sealed class FilterCompileTests
{
    #region Always match

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t\n")]
    public async Task Compile_EmptyExpression_ReturnsAlwaysMatchWithoutStack(string expression)
    {
        FilterResult<Filter> result = Filter.Compile(expression);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsSameReferenceAs(Filter.AlwaysMatch);
        await Assert.That(result.Value.IsAlwaysMatch).IsTrue();
        await Assert.That(result.Value.Stack).IsNull();
        await Assert.That(result.Value.Expression).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AlwaysMatch_MatchesEveryPacket()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        bool produced = Filter.AlwaysMatch.TryIsMatch(packet, out bool matched, out FilterError? failure);

        await Assert.That(produced).IsTrue();
        await Assert.That(matched).IsTrue();
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task AlwaysMatch_IsNeitherStatefulNorPoisoned()
    {
        await Assert.That(Filter.AlwaysMatch.IsStateful).IsFalse();
        await Assert.That(Filter.AlwaysMatch.IsPoisoned).IsFalse();
        await Assert.That(Filter.AlwaysMatch.PoisonError).IsNull();
    }

    [Test]
    public async Task AlwaysMatch_ResetState_IsNoOpAndLeavesCachesEmpty()
    {
        Filter.AlwaysMatch.ResetState();

        await Assert.That(Filter.AlwaysMatch.IsPoisoned).IsFalse();
        await Assert.That(Filter.AlwaysMatch.EvaluatedCount).IsEqualTo(0);
        await Assert.That(Filter.AlwaysMatch.MatchedPackets.IsEmpty).IsTrue();
        await Assert.That(Filter.AlwaysMatch.EvaluatedPackets.IsEmpty).IsTrue();
    }

    [Test]
    public async Task AlwaysMatch_TryBuildCandidates_ReturnsFalse()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        PacketIndex index = new(stack);

        bool built = Filter.AlwaysMatch.TryBuildCandidates(index, out RoaringBitmap? candidates);

        await Assert.That(built).IsFalse();
        await Assert.That(candidates).IsNull();
    }

    [Test]
    public async Task AlwaysMatch_TryDerive_ReturnsSingleton()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        bool derived = Filter.AlwaysMatch.TryDerive(stack, out Filter? result, out FilterError? failure);

        await Assert.That(derived).IsTrue();
        await Assert.That(result).IsSameReferenceAs(Filter.AlwaysMatch);
        await Assert.That(failure).IsNull();
    }

    #endregion

    #region Errors

    [Test]
    public async Task Compile_NonEmptyWithoutStack_ReportsStackRequired()
    {
        FilterResult<Filter> result = Filter.Compile("udp");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.StackRequired);
    }

    [Test]
    public async Task Compile_NullExpression_Throws()
    {
        await Assert.That(() => Filter.Compile(null!, null)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Compile_UnknownField_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("udp.nonexistent == 1", stack);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
        await Assert.That(result.Error.Message).Contains("udp.nonexistent");
    }

    [Test]
    public async Task Compile_UnknownProtocol_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("nosuchproto", stack);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task Compile_LexerError_Propagates()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("udp = 1", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.LexerError);
    }

    [Test]
    public async Task Compile_UnknownScopeAnchor_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("$nope { udp }", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task Compile_RelativeNameInsideScope_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("$udp { srcport == 53 }", stack);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
        await Assert.That(result.Error.Message).Contains("srcport");
    }

    [Test]
    public async Task Compile_UnknownFlankField_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("flank(ip.nope, changed, within: 1s)", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task Compile_UnknownFieldInsideFlankGate_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("flank(ip.ttl, changed, within: 1s, when: nope)", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task Compile_ProtocolOperandInComparison_ReportsTypeMismatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("icmp == 1", stack);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.TypeMismatch);
    }

    [Test]
    public async Task Compile_By_OnStringField_ReportsTypeMismatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("flank(dns.qry.name, by: 1, within: 1s)", stack);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.TypeMismatch);
    }

    #endregion

    #region TryCompile

    [Test]
    public async Task TryCompile_Valid_ReturnsFilter()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        bool compiled = Filter.TryCompile("udp", stack, out Filter? filter, out FilterError? failure);

        await Assert.That(compiled).IsTrue();
        await Assert.That(filter).IsNotNull();
        await Assert.That(failure).IsNull();
        await Assert.That(filter!.Expression).IsEqualTo("udp");
        await Assert.That(filter.Stack).IsSameReferenceAs(stack);
    }

    [Test]
    public async Task TryCompile_Invalid_ReturnsError()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        bool compiled = Filter.TryCompile("nope", stack, out Filter? filter, out FilterError? failure);

        await Assert.That(compiled).IsFalse();
        await Assert.That(filter).IsNull();
        await Assert.That(failure).IsNotNull();
    }

    #endregion

    #region TryParse

    [Test]
    public async Task TryParse_Valid_ReturnsTrue()
    {
        bool parsed = Filter.TryParse("udp.srcport == 53", null, out FilterError? failure);

        await Assert.That(parsed).IsTrue();
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task TryParse_Empty_ReturnsTrue()
    {
        await Assert.That(Filter.TryParse("   ")).IsTrue();
    }

    [Test]
    public async Task TryParse_Invalid_ReturnsError()
    {
        bool parsed = Filter.TryParse("udp &&", null, out FilterError? failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.SyntaxError);
    }

    [Test]
    public async Task TryParse_LexerFailure_ReturnsError()
    {
        bool parsed = Filter.TryParse("udp = 1", null, out FilterError? failure);

        await Assert.That(parsed).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.LexerError);
    }

    [Test]
    public async Task TryParse_UnknownNames_AreNotValidated()
    {
        await Assert.That(Filter.TryParse("nosuchproto.nosuchfield == 1")).IsTrue();
    }

    [Test]
    public async Task TryParse_Null_Throws()
    {
        await Assert.That(() => Filter.TryParse(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryParse_ReportsSpansForIncompleteExpression()
    {
        List<string> names = [];
        FilterCompileOptions options = new()
        {
            CaretPosition = 4,
            OnFieldNameSpan = (expression, start, length, kind) =>
                names.Add(expression.Slice(start, length).ToString()),
        };

        bool parsed = Filter.TryParse("udp.srcport == ", options, out _);

        await Assert.That(parsed).IsFalse();
        await Assert.That(names).Contains("udp.srcport");
        await Assert.That(options.CaretPosition).IsEqualTo(4);
        await Assert.That(options.RegexTimeout).IsNull();
    }

    #endregion
}
