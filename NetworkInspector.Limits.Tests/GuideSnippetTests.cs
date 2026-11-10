// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The code of the package README and of the guide, run as tests so that the documented example cannot go stale.</summary>
/// <remarks>
/// Sync: <c>NetworkInspector.Limits/README.md</c> § Quickstart and <c>docs/guides/limits.md</c> § Declare a limit show the body of
/// <see cref="TheQuickstartWritesTheDocumentedOutput"/> line by line; only its first line differs (the documents write to
/// <see cref="Console.Out"/>, the test to a <see cref="StringWriter"/>). A change of the snippet is made in the test and both
/// documents together. The failure texts of the guide table are the ones <see cref="TheFailureTextsOfTheGuideAreTheTextsTheLibraryProduces"/>
/// checks; the unit fragment of the guide is <see cref="TheOwnUnitExamplesOfTheGuideProduceTheDocumentedTexts"/>.
/// </remarks>
internal sealed class GuideSnippetTests
{
    #region Quickstart

    [Test]
    public async Task TheQuickstartWritesTheDocumentedOutput()
    {
        StringWriter output = new();

        // 1. Declare the limit once: key, unit, default, hard range, and the seven texts that explain it.
        LimitDefinition maxEntrySize = new(
            "archive.maxEntrySize",
            LimitUnits.Bytes,
            defaultValue: 128L * 1024 * 1024,
            minimum: 1024,
            maximum: 1L << 40,
            new LimitTexts(
                Title: "Largest archive entry",
                Description: "The largest size of one entry that is read from an archive.",
                Reason: "A hostile archive could otherwise claim an entry that uses all memory.",
                Benefit: "A broken or hostile archive is refused with a clear message.",
                SmallerValues: "Entries above the value are refused: 1 MiB refuses a 2 MiB entry.",
                LargerValues: "More memory may be used per entry: 1 GiB lets a 900 MiB entry through.",
                OffEffect: "Every entry is accepted and a hostile archive can use all memory."));

        // 2. Show the default.
        output.WriteLine(maxEntrySize.Format(maxEntrySize.DefaultValue));

        // 3. Parse what a user wrote: a whole number with a unit, or off. Everything else is refused with a suggestion.
        if (!maxEntrySize.TryParse("256 MiB", out LimitValue chosen, out string? problem))
        {
            output.WriteLine(problem);
        }

        output.WriteLine(maxEntrySize.Format(chosen));
        if (!maxEntrySize.TryParse("256 MB", out _, out problem))
        {
            output.WriteLine(problem);
        }

        // 4. Hold the live value and read it in the hot loop: one volatile read, no allocation, no lookup.
        LimitWriter writer = new(maxEntrySize);
        Limit live = writer.Limit;
        if (!writer.TrySet(chosen, out problem))
        {
            output.WriteLine(problem);
        }

        long entrySize = 300L * 1024 * 1024;
        if (entrySize > live.Ceiling)
        {
            // Report the hit with the standard sentence; the application supplies how to change the limit.
            output.WriteLine(maxEntrySize.HitMessage("Entry is 300 MiB", live.Value, "Change it with app set archive.maxEntrySize <value>."));
        }

        // 5. Check the catalog once in a test: duplicate keys.
        IReadOnlyList<LimitCatalogProblem> problems = LimitCatalog.Check([maxEntrySize, maxEntrySize]);
        output.WriteLine(problems[0].Message);

        await Assert.That(output.ToString()).IsEqualTo(_Documented);
    }

    /// <summary>The output block of the README quickstart and of the guide.</summary>
    private static readonly string _Documented = string.Join(
        Environment.NewLine,
        "128 MiB",
        "256 MiB",
        "archive.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB",
        "Entry is 300 MiB. Limit: 256 MiB (setting archive.maxEntrySize). Change it with app set archive.maxEntrySize <value>.",
        "The key 'archive.maxEntrySize' appears twice; a key names one limit.",
        string.Empty);

    #endregion

    #region Failure texts

    /// <summary>Parses <paramref name="text"/> and returns the refusal.</summary>
    /// <param name="definition">The limit to parse for.</param>
    /// <param name="text">The text a user wrote; refused by the test cases that call this.</param>
    /// <returns>The refusal, or <c>accepted</c> when the text was valid (the test then fails on the comparison).</returns>
    private static string _Problem(LimitDefinition definition, string text)
    {
        if (definition.TryParse(text, out _, out string? problem))
        {
            return "accepted";
        }

        return problem;
    }

    [Test]
    public async Task TheFailureTextsOfTheGuideAreTheTextsTheLibraryProduces()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "archive.maxEntrySize", minimum: 1024, maximum: 1L << 40);

        string decimalUnit = _Problem(definition, "256 MB");
        string fraction = _Problem(definition, "1.5 GiB");
        string belowRange = _Problem(definition, "512 B");
        string wrongWord = _Problem(definition, "5 s");

        await Assert.That(decimalUnit).IsEqualTo("archive.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB");
        await Assert.That(fraction).IsEqualTo("archive.maxEntrySize: whole number expected; use 1536 MiB");
        await Assert.That(belowRange).IsEqualTo("archive.maxEntrySize: allowed 1 KiB – 1 TiB, or off");
        await Assert.That(wrongWord).IsEqualTo("archive.maxEntrySize: unknown unit 's'; this value takes bytes: B, KiB, MiB, GiB, TiB");
    }

    [Test]
    public async Task TheLastTwoFailureTextsOfTheGuideAreTheTextsTheLibraryProduces()
    {
        LimitDefinition keepFor = LimitTestDefinitions.Duration(key: "x.keepFor", maximum: 100L * 86_400_000L);
        TableUnit bitRate = new("bit rates", "100 Mbit/s", UnitWordSet.Decimal("bit/s"), wordRequired: true, smallestMinimum: 1);
        LimitDefinition maxRate = new("x.maxSpeed", bitRate, 1_000_000, 1_000, 100_000_000_000, LimitTestDefinitions.Texts(), allowOff: false);

        string month = _Problem(keepFor, "2month");
        string off = _Problem(maxRate, "off");

        await Assert.That(month).IsEqualTo("x.keepFor: unknown unit 'month'; this value takes durations: ms, s, min, h, d, w");
        await Assert.That(off).IsEqualTo("x.maxSpeed: off is not accepted for this limit; write a whole number such as 100 Mbit/s");
    }

    #endregion

    #region Own unit

    /// <summary>
    /// The own-unit fragments of the guide (<c>docs/guides/limits.md</c> § Compose a unit), run as a test; <c>texts</c> of the guide
    /// is <see cref="LimitTestDefinitions.Texts"/> here.
    /// </summary>
    [Test]
    public async Task TheOwnUnitExamplesOfTheGuideProduceTheDocumentedTexts()
    {
        StringWriter output = new();
        LimitTexts texts = LimitTestDefinitions.Texts();

        // A unit of your own domain: a word set (the base word and larger words), wrapped in a table unit.
        TableUnit frames = new("frames", "10 kframes", UnitWordSet.Of("frames").With("kframes", 1_000).With("Mframes", 1_000_000));
        LimitDefinition maxFrames = new("capture.maxFrames", frames, defaultValue: 50_000_000, minimum: 1_000, maximum: 10_000_000_000,
            texts, allowOff: false);

        output.WriteLine(maxFrames.Format(maxFrames.DefaultValue));
        output.WriteLine(maxFrames.FormatRange());
        if (!maxFrames.TryParse("off", out _, out string? problem))
        {
            output.WriteLine(problem);
        }

        if (!maxFrames.TryParse("5 MiB", out _, out problem))
        {
            output.WriteLine(problem);
        }

        // The prefix ladders save the words: Decimal("bit/s") is bit/s, kbit/s, Mbit/s, Gbit/s, Tbit/s.
        TableUnit bitRate = new("bit rates", "100 Mbit/s", UnitWordSet.Decimal("bit/s"), wordRequired: true, smallestMinimum: 1);
        LimitDefinition maxSpeed = new("net.maxSpeed", bitRate, 1_000_000_000, 1_000_000, 100_000_000_000, texts);
        output.WriteLine(maxSpeed.Format(maxSpeed.DefaultValue));
        if (!maxSpeed.TryParse("1.5 Gbit/s", out _, out problem))
        {
            output.WriteLine(problem);
        }

        await Assert.That(output.ToString()).IsEqualTo(string.Join(
            Environment.NewLine,
            "50 Mframes",
            "1 kframes – 10000 Mframes",
            "capture.maxFrames: off is not accepted for this limit; write a whole number such as 10 kframes",
            "capture.maxFrames: unknown unit 'MiB'; this value takes frames: frames, kframes, Mframes",
            "1 Gbit/s",
            "net.maxSpeed: whole number expected; use 1500 Mbit/s",
            string.Empty));
    }

    #endregion
}
