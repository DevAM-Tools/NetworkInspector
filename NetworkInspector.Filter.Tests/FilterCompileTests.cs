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
        await Assert.That(result.Error.Suggestion).IsNull();
    }

    [Test]
    [Arguments("udp.srcprot == 53", "udp.srcport", 0, 11)]
    [Arguments("udp && udp.dstpotr == 53", "udp.dstport", 7, 11)]
    [Arguments("UDP", "udp", 0, 3)]
    [Arguments("tpc", "tcp", 0, 3)]
    [Arguments("len(udp.paylaod) > 1", "udp.payload", 4, 11)]
    [Arguments("udp.paylod[0:2] == 00:01", "udp.payload", 0, 10)]
    [Arguments("flank(ip.tll, changed, within: 1s)", "ip.ttl", 6, 6)]
    public async Task Compile_MisspelledName_SuggestsTheClosestName(string expression, string suggestion, int position, int length)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> tree = Filter.Compile(expression, stack);
        FilterResult<FilterObserver> observer = Filter.Compile(expression, stack, FilterObserverOptions.Default);

        await Assert.That(tree.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
        await Assert.That(tree.Error.Suggestion).IsEqualTo(suggestion);
        await Assert.That(tree.Error.Message).Contains(Invariant($"Did you mean '{suggestion}'?"));
        await Assert.That(tree.Error.Position).IsEqualTo(position);
        await Assert.That(tree.Error.Length).IsEqualTo(length);
        await Assert.That(observer.Error.Suggestion).IsEqualTo(suggestion);
    }

    [Test]
    [Arguments("udp.payload[10:12 == 00", FilterErrorKind.SyntaxError, "Expected ] but found '=='", 18)]
    [Arguments("udp.srcport ==", FilterErrorKind.SyntaxError, "Unexpected end of expression — a literal value is required here", 14)]
    [Arguments("flank(ip.ttl, changed)", FilterErrorKind.SyntaxError, "'flank' requires a 'within:' window", 0)]
    [Arguments(
        "udp.srcprot == 53",
        FilterErrorKind.UnknownField,
        "Unknown field or protocol 'udp.srcprot'. Did you mean 'udp.srcport'?",
        0)]
    public async Task Compile_GuideErrorExamples_ReportTheDocumentedMessages(
        string expression,
        FilterErrorKind kind,
        string message,
        int position)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterError error = Filter.Compile(expression, stack).Error;

        await Assert.That(error.Kind).IsEqualTo(kind);
        await Assert.That(error.Message).IsEqualTo(message);
        await Assert.That(error.Position).IsEqualTo(position);
    }

    [Test]
    public async Task Derive_ThroughTheInterface_ReturnsAnIndependentFilter()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        using Stack other = FilterTestHelper.BuildStack();
        using Stack ethernetOnly = FilterTestHelper.BuildEthernetOnlyStack();
        IFilter source = Filter.Compile("udp.srcport == 53", stack).Value;
        IFilter always = Filter.AlwaysMatch;

        bool derived = source.TryDerive(other, out IFilter? rebound, out FilterError? failure);
        bool alwaysDerived = always.TryDerive(other, out IFilter? alwaysRebound, out _);
        bool missing = source.TryDerive(ethernetOnly, out IFilter? none, out FilterError? missingFailure);

        await Assert.That(derived).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(rebound).IsNotSameReferenceAs(source);
        await Assert.That(rebound!.Stack).IsSameReferenceAs(other);
        await Assert.That(rebound.Expression).IsEqualTo(source.Expression);
        await Assert.That(alwaysDerived).IsTrue();
        await Assert.That(alwaysRebound).IsSameReferenceAs(Filter.AlwaysMatch);
        await Assert.That(missing).IsFalse();
        await Assert.That(none).IsNull();
        await Assert.That(missingFailure!.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task AlwaysMatch_IsSafeFromTwoThreads()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        bool[] results = new bool[2];
        Thread[] threads = new Thread[2];
        using Barrier start = new(threads.Length);
        for (int i = 0; i < threads.Length; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                bool ok = true;
                for (int n = 0; n < 10_000; n++)
                {
                    ok &= Filter.AlwaysMatch.TryIsMatch(packet, out bool matched, out _) && matched;
                    Filter.AlwaysMatch.ResetState();
                    ok &= Filter.AlwaysMatch.EvaluatedCount == 0;
                }

                results[slot] = ok;
            })
            {
                Name = Invariant($"AlwaysMatch-{slot}"),
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        await Assert.That(results[0] && results[1]).IsTrue();
        await Assert.That(Filter.AlwaysMatch.IsPoisoned).IsFalse();
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
            OnFieldNameSpan = (expression, start, length, kind) =>
                names.Add(expression.Slice(start, length).ToString()),
        };

        bool parsed = Filter.TryParse("udp.srcport == ", options, out _);

        await Assert.That(parsed).IsFalse();
        await Assert.That(names).Contains("udp.srcport");
        await Assert.That(options.RegexTimeout).IsNull();
    }

    #endregion
}
