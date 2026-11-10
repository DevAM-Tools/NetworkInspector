// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The catalog check: duplicate keys and refused input.</summary>
internal sealed class LimitCatalogTests
{
    #region Duplicate keys

    [Test]
    public async Task TwoDefinitionsWithTheSameKeyAreOneProblemThatNamesTheKey()
    {
        LimitDefinition[] definitions = [LimitTestDefinitions.Bytes(key: "demo.maxSize"), LimitTestDefinitions.Count(key: "demo.maxSize")];

        IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check(definitions);

        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0].Key).IsEqualTo("demo.maxSize");
        await Assert.That(problems[0].Message).Contains("demo.maxSize").And.Contains("twice");
    }

    [Test]
    public async Task AListOfThreeWithOnePairHasExactlyOneProblem()
    {
        LimitDefinition[] definitions =
        [
            LimitTestDefinitions.Bytes(key: "demo.first"),
            LimitTestDefinitions.Bytes(key: "demo.second"),
            LimitTestDefinitions.Bytes(key: "demo.first"),
        ];

        IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check(definitions);

        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0].Key).IsEqualTo("demo.first");
    }

    [Test]
    public async Task AKeyThatAppearsThreeTimesIsOneProblemWithTheCount()
    {
        LimitDefinition[] definitions = [LimitTestDefinitions.Bytes(), LimitTestDefinitions.Bytes(), LimitTestDefinitions.Bytes()];

        IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check(definitions);

        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(problems[0].Message).Contains("3 times");
    }

    [Test]
    public async Task EmptySingleAndDifferentKeysHaveNoProblem()
    {
        LimitDefinition[] different = [LimitTestDefinitions.Bytes(key: "demo.first"), LimitTestDefinitions.Bytes(key: "demo.second")];

        await Assert.That(LimitCatalog.Check([]).Count).IsEqualTo(0);
        await Assert.That(LimitCatalog.Check([LimitTestDefinitions.Bytes()]).Count).IsEqualTo(0);
        await Assert.That(LimitCatalog.Check(different).Count).IsEqualTo(0);
    }

    [Test]
    public async Task KeysThatDifferOnlyInLetterCaseAreTwoKeys()
    {
        LimitDefinition[] definitions = [LimitTestDefinitions.Count(key: "demo.maxItems"), LimitTestDefinitions.Count(key: "demo.maxitems")];

        await Assert.That(LimitCatalog.Check(definitions).Count).IsEqualTo(0);
    }

    #endregion

    #region Refused input

    [Test]
    public async Task ANullElementIsRefusedWithItsIndex()
    {
        LimitDefinition?[] definitions = [LimitTestDefinitions.Bytes(), null];

        ArgumentException exception = Assert.Throws<ArgumentException>(() => LimitCatalog.Check(definitions!)); // null element is the input under test

        await Assert.That(exception.Message).Contains("index 1");
    }

    [Test]
    public async Task ANullListIsRefusedWithTheNullException()
    {
        await Assert.That(() => LimitCatalog.Check(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion
}
