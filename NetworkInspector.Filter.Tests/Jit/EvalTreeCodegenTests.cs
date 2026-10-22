// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Jit;

/// <summary>
/// Covers the node-tree back end: left-to-right short-circuit (observable through lazy materialization and regex
/// timeouts), scope domain restoration on every exit, flank gates, the parser limits, and compile errors.
/// </summary>
internal sealed class EvalTreeCodegenTests
{
    #region Helpers

    private static (Stack Stack, LazyValueProtocol Protocol, ProtocolId Id) _BuildLazy()
    {
#pragma warning disable CA2000 // Id: CA2000. Why: ownership of the settings manager transfers to the stack. Scope: this statement. User approved.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        LazyValueProtocol protocol = new() { Value = 7 };
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        return (builder.Build(), protocol, id);
    }

    private static Packet _ParseLazy(Stack stack, ProtocolId firstProtocol, int packetId)
    {
        Frame frame = Frame.Create(
            new FrameId(packetId),
            Timestamp.FromNanos(packetId),
            new byte[4],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(FieldTreeMode.Build, firstProtocol: firstProtocol);
        if (!Packet.TryParse(new PacketId(packetId), stack, frame, in options, out Packet? packet, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet!;
    }

    private static Filter _CompileWithTimeout(string expression, Stack stack)
    {
        FilterCompileOptions options = new() { RegexTimeout = TimeSpan.FromTicks(1) };
        FilterResult<Filter> compiled = Filter.Compile(expression, stack, options);
        if (!compiled.TryGetValue(out Filter? filter))
        {
            throw new InvalidOperationException(compiled.Error.Message);
        }

        return filter;
    }

    #endregion

    #region Short-circuit

    [Test]
    [Arguments("false && lazy.value == 7", false, false)]
    [Arguments("true || lazy.value == 7", false, true)]
    [Arguments("!(true) && lazy.value == 7", false, false)]
    [Arguments("true && lazy.value == 7", true, true)]
    [Arguments("lazy.value == 7 || false", true, true)]
    public async Task RightSideRunsOnlyWhenTheLeftSideDoesNotDecide(string expression, bool materializes, bool expected)
    {
        (Stack stack, LazyValueProtocol protocol, ProtocolId id) = _BuildLazy();
        using (stack)
        {
            Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
            Packet packet = _ParseLazy(stack, id, 0);

            bool matched = FilterTestHelper.MatchOrThrow(filter, packet);

            await Assert.That(matched).IsEqualTo(expected);
            await Assert.That(protocol.PopulateCount).IsEqualTo(materializes ? 1 : 0);
            await Assert.That(packet.HasUnpopulatedLazyFields).IsEqualTo(!materializes);
        }
    }

    [Test]
    public async Task SkippedRegexOnTheRightOfAFalseConjunctionCannotPoison()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = _CompileWithTimeout("false && dns.qry.name matches \"(?=^(a+)+$)\"", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame(label: new string('a', 60)));

        bool produced = filter.TryIsMatch(packet, out bool matched, out FilterError? failure);

        await Assert.That(produced).IsTrue();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure).IsNull();
        await Assert.That(filter.IsPoisoned).IsFalse();
    }

    #endregion

    #region Scopes

    [Test]
    public async Task OuterTermAfterAScopeSeesTheWholePacketAgain()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));
        Filter afterScope = FilterTestHelper.CompileOrThrow("$udp { udp.srcport == 53 } && eth.type == 2048", stack);
        Filter nested = FilterTestHelper.CompileOrThrow("$udp { $udp.srcport { udp.srcport == 53 } }", stack);
        Filter outsideBody = FilterTestHelper.CompileOrThrow("$udp { eth.type == 2048 }", stack);

        await Assert.That(FilterTestHelper.MatchOrThrow(afterScope, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(nested, packet)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(outsideBody, packet)).IsFalse();
    }

    [Test]
    public async Task RegexTimeoutInsideAScopePoisonsAndResetRestoresTheWholePacketDomain()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = _CompileWithTimeout("$dns { dns.qry.name matches \"(?=^(a+)+$)\" } || eth.type == 2048", stack);
        Packet dns = FilterTestHelper.Parse(stack, FilterTestHelper.BuildDnsQueryFrame(label: new string('a', 60)), 0);
        Packet plain = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(99, 1024), 1);

        bool firstProduced = filter.TryIsMatch(dns, out _, out FilterError? firstFailure);
        filter.ResetState();
        bool secondProduced = filter.TryIsMatch(plain, out bool secondMatched, out FilterError? secondFailure);

        await Assert.That(firstProduced).IsFalse();
        await Assert.That(firstFailure!.Kind).IsEqualTo(FilterErrorKind.RuntimeError);
        await Assert.That(secondProduced).IsTrue();
        await Assert.That(secondFailure).IsNull();
        await Assert.That(secondMatched).IsTrue();
    }

    #endregion

    #region Flank gate

    [Test]
    public async Task FalseGateLeavesTheTrackerUntouched()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(
            "flank(udp.srcport, changed, within: 10packets, when: udp.dstport == 53)",
            stack);
        Packet first = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(1, 53), 0, 0);
        Packet hidden = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(2, 54), 1, 1);
        Packet third = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(3, 53), 2, 2);

        bool firstMatched = FilterTestHelper.MatchOrThrow(filter, first);
        bool hiddenMatched = FilterTestHelper.MatchOrThrow(filter, hidden);
        bool thirdMatched = FilterTestHelper.MatchOrThrow(filter, third);

        await Assert.That(firstMatched).IsFalse();
        await Assert.That(hiddenMatched).IsFalse();
        await Assert.That(thirdMatched).IsTrue();
    }

    #endregion

    #region Limits

    [Test]
    public async Task DeepestAllowedNestingCompilesAndEvaluates()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        string expression = new string('(', FilterParser.MaxNestingDepth) + "udp.srcport == 53"
            + new string(')', FilterParser.MaxNestingDepth);
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        bool matched = FilterTestHelper.MatchOrThrow(filter, packet);
        bool tooDeep = Filter.Compile("(" + expression + ")", stack).IsSuccess;

        await Assert.That(matched).IsTrue();
        await Assert.That(tooDeep).IsFalse();
    }

    [Test]
    public async Task LongestAllowedChainOfComparisonsCompilesAndEvaluates()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        IEnumerable<string> terms = Enumerable.Range(0, FilterParser.MaxTermCount - 1)
            .Select(static i => Invariant($"udp.srcport == {i + 10000}"))
            .Append("udp.srcport == 53");
        Filter filter = FilterTestHelper.CompileOrThrow(string.Join(" || ", terms), stack);
        Packet hit = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024), 0);
        Packet miss = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(54, 1024), 1);

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, hit)).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(filter, miss)).IsFalse();
    }

    #endregion

    #region Errors

    [Test]
    public async Task UnknownFieldKeepsKindPositionAndSuggestion()
    {
        using Stack stack = FilterTestHelper.BuildStack();

        FilterResult<Filter> result = Filter.Compile("udp && udp.srcprot == 1", stack);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
        await Assert.That(result.Error.Position).IsEqualTo(7);
        await Assert.That(result.Error.Length).IsEqualTo("udp.srcprot".Length);
        await Assert.That(result.Error.Suggestion).IsEqualTo("udp.srcport");
    }

    [Test]
    public async Task MatchesWithoutACompiledPatternIsACompilerError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        StringPredicateNode node = new(new FieldOperandNode("dns.qry.name", 0, 12), StringOp.Matches, "a", null, 0, 22);
        FilterProgram program = new("<synthetic>", node, FilterFeature.Classic);

        FilterResult<CompiledFilterProgram> result = new EvalTreeCodegen().Compile(program, new SymbolResolver(stack), null);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.CompilerError);
        await Assert.That(result.Error.Message).Contains("without a compiled pattern");
    }

    #endregion
}
