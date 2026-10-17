// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Completion;

/// <summary>Covers <see cref="Filter.GetCompletions"/>: word range, grammar context, ordering and edge cases.</summary>
internal sealed class FilterCompletionTests
{
    #region Helpers

    /// <summary>Completes at the position of the last '|' (the caret marker), which is removed from the expression.</summary>
    private static FilterCompletions _Complete(string markedExpression, IStack? stack)
    {
        int caret = markedExpression.LastIndexOf('|');
        string expression = markedExpression.Remove(caret, 1);
        return Filter.GetCompletions(expression, caret, stack);
    }

    private static List<string> _Texts(FilterCompletions completions) => [.. completions.Items.Select(item => item.Text)];

    #endregion

    #region Word range

    [Test]
    public async Task Complete_AtTheEndOfAName_ReplacesTheTypedPrefix()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("udp.sr| && tcp", stack);

        await Assert.That(completions.Prefix).IsEqualTo("udp.sr");
        await Assert.That(completions.ReplaceStart).IsEqualTo(0);
        await Assert.That(completions.ReplaceLength).IsEqualTo(6);
        await Assert.That(_Texts(completions)).Contains("udp.srcport");
        await Assert.That(completions.Items.All(item => item.Text.StartsWith("udp.sr", StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    [Test]
    public async Task Complete_InTheMiddleOfAName_ReplacesTheWholeWord()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("tcp || udp.s|rcport == 53", stack);

        await Assert.That(completions.Prefix).IsEqualTo("udp.s");
        await Assert.That(completions.ReplaceStart).IsEqualTo(7);
        await Assert.That(completions.ReplaceLength).IsEqualTo(11);
        await Assert.That(_Texts(completions)).Contains("udp.srcport");
    }

    [Test]
    public async Task Complete_AfterATrailingDot_OffersTheProtocolFields()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("udp.|", stack);

        await Assert.That(completions.Prefix).IsEqualTo("udp.");
        await Assert.That(_Texts(completions)).Contains("udp.srcport");
        await Assert.That(_Texts(completions)).DoesNotContain("udp");
    }

    [Test]
    public async Task Complete_Items_AreAlphabeticalAndUnique()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("|", stack);
        List<string> texts = _Texts(completions);
        List<string> sorted = [.. texts];
        sorted.Sort(FilterNameCatalog.NameOrder);

        await Assert.That(texts.Count).IsGreaterThan(100);
        await Assert.That(texts).IsEquivalentTo(sorted);
        await Assert.That(texts.SequenceEqual(sorted)).IsTrue();
        await Assert.That(texts.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(texts.Count);
    }

    [Test]
    public async Task Complete_PrefixMatching_IgnoresCase()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("UD|", stack);

        await Assert.That(_Texts(completions)).Contains("udp");
        await Assert.That(_Texts(completions)).Contains("udp.srcport");
    }

    #endregion

    #region Kinds

    [Test]
    public async Task Complete_Names_CarryTheirKindAndValueType()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        List<FilterCompletionItem> items = [.. _Complete("|", stack).Items];

        await Assert.That(items).Contains(new FilterCompletionItem("udp", FilterCompletionKind.Protocol, FieldType.None));
        await Assert.That(items).Contains(new FilterCompletionItem("udp.srcport", FilterCompletionKind.Field, FieldType.U64));
        await Assert.That(items).Contains(new FilterCompletionItem("eth.addr", FilterCompletionKind.FieldAlias, FieldType.MacAddress));
        await Assert.That(items).Contains(new FilterCompletionItem("flank", FilterCompletionKind.Keyword, FieldType.None));
    }

    #endregion

    #region Grammar context

    [Test]
    [Arguments("|")]
    [Arguments("not |")]
    [Arguments("!|")]
    [Arguments("(|")]
    [Arguments("tcp && |")]
    [Arguments("tcp or |")]
    [Arguments("$udp { |")]
    [Arguments("flank(ip.ttl, within: 1s, when: |")]
    public async Task Complete_WhereATermStarts_OffersNamesAndExpressionKeywords(string marked)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        List<string> texts = _Texts(_Complete(marked, stack));

        await Assert.That(texts).Contains("udp");
        await Assert.That(texts).Contains("udp.srcport");
        await Assert.That(texts).Contains("flank");
        await Assert.That(texts).Contains("len");
        await Assert.That(texts).Contains("not");
        await Assert.That(texts).DoesNotContain("==");
        await Assert.That(texts).DoesNotContain("and");
    }

    [Test]
    public async Task Complete_AfterDollar_OffersNamesOnly()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        List<string> texts = _Texts(_Complete("$ud|", stack));

        await Assert.That(texts).Contains("udp");
        await Assert.That(texts).DoesNotContain("flank");
    }

    [Test]
    [Arguments("flank(|")]
    [Arguments("flank( ud|")]
    [Arguments("len(|")]
    [Arguments("len (ud|")]
    public async Task Complete_InsideFlankOrLen_OffersValueSourcesOnly(string marked)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete(marked, stack);

        await Assert.That(_Texts(completions)).Contains("udp.srcport");
        await Assert.That(completions.Items.Any(item => item.Kind == FilterCompletionKind.Protocol)).IsFalse();
        await Assert.That(completions.Items.Any(item => item.Kind == FilterCompletionKind.Keyword)).IsFalse();
    }

    [Test]
    [Arguments("flank(ip.ttl, |", 6)]
    [Arguments("flank(ip.ttl, changed, |", 6)]
    [Arguments("flank(ip.ttl, wi|", 1)]
    [Arguments("flank(ip.ttl, within: 1s, when: udp, |", 6)]
    public async Task Complete_AfterACommaInFlank_OffersFlankArguments(string marked, int expectedCount)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete(marked, stack);

        await Assert.That(completions.Items.Count).IsEqualTo(expectedCount);
        await Assert.That(completions.Items.All(item => item.Kind == FilterCompletionKind.FlankArgument)).IsTrue();
    }

    [Test]
    [Arguments("udp.srcport |")]
    [Arguments("len(udp.payload) |")]
    [Arguments("udp.payload[0:2] |")]
    [Arguments("flank(ip.ttl, within: 1s, when: udp.srcport |")]
    public async Task Complete_AfterAnOperand_OffersOperatorsAndConnectives(string marked)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        List<string> texts = _Texts(_Complete(marked, stack));

        await Assert.That(texts).Contains("==");
        await Assert.That(texts).Contains("in");
        await Assert.That(texts).Contains("contains");
        await Assert.That(texts).Contains("matches");
        await Assert.That(texts).Contains("&&");
        await Assert.That(texts).DoesNotContain("udp");
    }

    [Test]
    [Arguments("udp.srcport =|", "==")]
    [Arguments("udp.srcport >|", ">,>=")]
    [Arguments("udp.srcport !|", "!=")]
    [Arguments("udp &|", "&&")]
    [Arguments("udp.srcport co|", "contains")]
    [Arguments("udp.srcport == 53 a|", "and")]
    [Arguments("udp.srcport == 53 |", "&&,and,or,||")]
    [Arguments("(udp) |", "&&,and,or,||")]
    [Arguments("$udp { udp } |", "&&,and,or,||")]
    [Arguments("flank(ip.ttl, within: 1s) |", "&&,and,or,||")]
    public async Task Complete_OperatorsAndConnectives_FollowThePrefix(string marked, string expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        List<string> texts = _Texts(_Complete(marked, stack));

        await Assert.That(string.Join(',', texts)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("udp.srcport == |")]
    [Arguments("udp.srcport == 5|")]
    [Arguments("udp.srcport in {53, |")]
    [Arguments("udp.srcport in {53 |")]
    [Arguments("udp.srcport in 1 |")]
    [Arguments("udp.payload[|")]
    [Arguments("udp.payload[0:|")]
    [Arguments("flank(ip.ttl, within: |")]
    [Arguments("flank(ip.ttl, from: 1 |")]
    [Arguments("flank(ip.ttl |")]
    [Arguments("http.host contains \"ud|")]
    [Arguments("http.host matches |")]
    [Arguments("$udp |")]
    [Arguments("$udp[0] |")]
    [Arguments("eth.src == 00:1|")]
    [Arguments("ip.src == 10.0.0.|")]
    [Arguments("flank|")]
    public async Task Complete_WhereAValueOrPunctuationFollows_OffersNothingMisleading(string marked)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete(marked, stack);

        await Assert.That(completions.Items.Any(item => item.Kind is FilterCompletionKind.Protocol or FilterCompletionKind.Field))
            .IsFalse();
        await Assert.That(completions.Items.Any(item => item.Kind == FilterCompletionKind.Operator)).IsFalse();
    }

    [Test]
    public async Task Complete_AfterTheFlankKeyword_CompletesTheKeywordItself()
    {
        FilterCompletions completions = _Complete("fla|", stack: null);

        await Assert.That(_Texts(completions)).IsEquivalentTo(["flank"]);
    }

    #endregion

    #region Edge cases

    [Test]
    public async Task Complete_WithoutAStack_OffersOnlyTheLanguage()
    {
        FilterCompletions completions = _Complete("|", stack: null);

        await Assert.That(_Texts(completions)).IsEquivalentTo(["false", "flank", "len", "not", "true"]);
    }

    [Test]
    public async Task Complete_EmptyExpression_IsAnExpressionStart()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = Filter.GetCompletions(string.Empty, 0, stack);

        await Assert.That(completions.ReplaceStart).IsEqualTo(0);
        await Assert.That(completions.ReplaceLength).IsEqualTo(0);
        await Assert.That(completions.Prefix).IsEqualTo(string.Empty);
        await Assert.That(_Texts(completions)).Contains("tcp");
    }

    [Test]
    public async Task Complete_UnknownPrefix_ReturnsAnEmptyList()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("zzzz|", stack);

        await Assert.That(completions.Items.Count).IsEqualTo(0);
        await Assert.That(completions.Prefix).IsEqualTo("zzzz");
    }

    [Test]
    public async Task Complete_AfterALexerErrorEarlierInTheText_StillUsesTheLastValidContext()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions completions = _Complete("udp = 5 && ud|", stack);

        await Assert.That(completions.Prefix).IsEqualTo("ud");
    }

    [Test]
    public async Task GetCompletions_InvalidArguments_Throw()
    {
        await Assert.That(() => Filter.GetCompletions(null!, 0, null)).Throws<ArgumentNullException>();
        await Assert.That(() => Filter.GetCompletions("udp", -1, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Filter.GetCompletions("udp", 4, null)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetCompletions_CaretAtBothEnds_IsValid()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterCompletions atStart = Filter.GetCompletions("udp", 0, stack);
        FilterCompletions atEnd = Filter.GetCompletions("udp", 3, stack);

        await Assert.That(atStart.Prefix).IsEqualTo(string.Empty);
        await Assert.That(atStart.ReplaceLength).IsEqualTo(3);
        await Assert.That(atEnd.Prefix).IsEqualTo("udp");
    }

    [Test]
    public async Task GetCompletions_FromManyThreads_AgreesWithASingleThread()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        List<string> expected = _Texts(Filter.GetCompletions("udp.", 4, stack));
        bool[] same = new bool[4];
        Thread[] threads = new Thread[same.Length];
        using Barrier start = new(threads.Length);
        for (int i = 0; i < threads.Length; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                bool ok = true;
                for (int n = 0; n < 200; n++)
                {
                    ok &= _Texts(Filter.GetCompletions("udp.", 4, stack)).SequenceEqual(expected);
                }

                same[slot] = ok;
            })
            {
                Name = Invariant($"Completion-{slot}"),
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        await Assert.That(same.All(value => value)).IsTrue();
    }

    #endregion
}
