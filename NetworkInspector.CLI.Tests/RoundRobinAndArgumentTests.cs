// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="RoundRobinSourceIterator"/> and <see cref="CliArgumentParsing"/>.
/// </summary>
internal sealed class RoundRobinAndArgumentTests
{
    [Test]
    public async Task RoundRobin_SingleSourceExhausted_HasActiveFalse()
    {
        using EmptyFrameSource source = new();
        RoundRobinSourceIterator iterator = new([source]);

        await Assert.That(iterator.HasActive).IsTrue();
        iterator.MarkCurrentExhaustedAndAdvance();
        await Assert.That(iterator.HasActive).IsFalse();
    }

    [Test]
    public async Task RoundRobin_TwoSources_AdvancesRoundRobin()
    {
        using EmptyFrameSource a = new("a");
        using EmptyFrameSource b = new("b");
        RoundRobinSourceIterator iterator = new([a, b]);

        await Assert.That(iterator.Current.UiName).IsEqualTo("a");
        iterator.Advance();
        await Assert.That(iterator.Current.UiName).IsEqualTo("b");
        iterator.Advance();
        await Assert.That(iterator.Current.UiName).IsEqualTo("a");
    }

    [Test]
    public async Task RoundRobin_ExhaustOne_ContinuesOnOther()
    {
        using EmptyFrameSource a = new("a");
        using EmptyFrameSource b = new("b");
        RoundRobinSourceIterator iterator = new([a, b]);

        iterator.MarkCurrentExhaustedAndAdvance();
        await Assert.That(iterator.HasActive).IsTrue();
        await Assert.That(iterator.Current.UiName).IsEqualTo("b");
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeLong_RejectsNegative()
    {
        await Assert.That(() => CliArgumentParsing.ParseNonNegativeLong("-1"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeInt_Valid_ReturnsValue()
    {
        int value = CliArgumentParsing.ParseNonNegativeInt("42", "--max-frames");
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeInt_Negative_Throws()
    {
        await Assert.That(() => CliArgumentParsing.ParseNonNegativeInt("-1", "--progress"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeInt_AboveMaxCount_Throws()
    {
        string over = (ArrayIndexIdRange.MaxCount + 1).ToString(CultureInfo.InvariantCulture);
        await Assert.That(() => CliArgumentParsing.ParseNonNegativeInt(over, "--max-frames"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeInt_CustomMaxInclusive_ThrowsWhenExceeded()
    {
        await Assert.That(() => CliArgumentParsing.ParseNonNegativeInt("5", "--x", maxInclusive: 3))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_ParseNonNegativeInt_Garbage_Throws()
    {
        await Assert.That(() => CliArgumentParsing.ParseNonNegativeInt("abc", "--split-count"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_MiBToCacheBudgetBytes_RejectsOverflow()
    {
        await Assert.That(() => CliArgumentParsing.MiBToCacheBudgetBytes(999999999999L, "--blf-cache-size"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CliArgumentParsing_MiBToSplitSizeBytes_Zero_ReturnsZero()
    {
        long bytes = CliArgumentParsing.MiBToSplitSizeBytes(0, "--split-size");

        await Assert.That(bytes).IsEqualTo(0L);
    }

    [Test]
    public async Task CliArgumentParsing_MiBToSplitSizeBytes_One_Returns1MiB()
    {
        long bytes = CliArgumentParsing.MiBToSplitSizeBytes(1, "--split-size");

        await Assert.That(bytes).IsEqualTo(1024L * 1024L);
    }

    [Test]
    public async Task CliArgumentParsing_IsHelpFlag_RecognizesVariants()
    {
        await Assert.That(CliArgumentParsing.IsHelpFlag("--help")).IsTrue();
        await Assert.That(CliArgumentParsing.IsHelpFlag("-h")).IsTrue();
        await Assert.That(CliArgumentParsing.IsHelpFlag("export")).IsFalse();
    }

    [Test]
    public async Task CliArgumentParsing_RunWithArgumentGuard_MapsArgumentExceptionToExit1()
    {
        int code = CliArgumentParsing.RunWithArgumentGuard(
            () => throw new ArgumentException("bad"));

        await Assert.That(code).IsEqualTo(1);
    }

    [Test]
    public async Task CliArgumentParsing_GetNextArg_MissingValue_Throws()
    {
        string[] args = ["--output"];
        int index = 0;

        await Assert.That(() => CliArgumentParsing.GetNextArg(args, ref index, "--output"))
            .Throws<ArgumentException>();
    }

    private sealed class EmptyFrameSource(string name = "empty") : IFrameSource
    {
        public string UiName => name;
        public string? Description => null;
        public int? EstimatedFrameCount => 0;
        public bool IsRunning => false;
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) { }
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;
        public void Dispose() { }
    }
}
