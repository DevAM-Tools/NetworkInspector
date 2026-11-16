// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ExportEntryName"/>: the accepted and refused entry names of the grammar (the trust boundary between an
/// exporter and the host's file system), the <see cref="ExportEntryName.Main"/> value, and equality.
/// </summary>
internal sealed class ExportEntryNameTests
{
    #region Grammar

    /// <summary>Entry names that match the grammar are accepted unchanged.</summary>
    /// <param name="text">The accepted entry text.</param>
    /// <returns>The task of the test.</returns>
    [Test]
    [Arguments("packets.parquet")]
    [Arguments("fields/field_12.parquet")]
    [Arguments("a-b_c.d")]
    [Arguments("a")]
    [Arguments("a.b.c")]
    public async Task TryCreateAcceptsAValidName(string text)
    {
        bool created = ExportEntryName.TryCreate(text, out ExportEntryName name, out string? problem);

        await Assert.That(created).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(name.Text).IsEqualTo(text);
        await Assert.That(name.IsMain).IsFalse();
    }

    /// <summary>Names the host's file system cannot store or that escape the root are refused with the rule they break.</summary>
    /// <param name="text">The refused entry text.</param>
    /// <param name="ruleFragment">A fragment of the problem text that names the broken rule.</param>
    /// <returns>The task of the test.</returns>
    [Test]
    [Arguments("", "must not be empty")]
    [Arguments("/a", "empty segment")]
    [Arguments("a/", "empty segment")]
    [Arguments("a//b", "empty segment")]
    [Arguments("..", "starts or ends")]
    [Arguments("a/../b", "starts or ends")]
    [Arguments(".", "starts or ends")]
    [Arguments(".hidden", "starts or ends")]
    [Arguments("name.", "starts or ends")]
    [Arguments("a\\b", "contains")]
    [Arguments("c:x", "contains")]
    [Arguments("Packets.parquet", "contains")]
    [Arguments("pa ckets", "contains")]
    [Arguments("nul", "device")]
    [Arguments("nul.parquet", "device")]
    [Arguments("COM1", "contains")]
    [Arguments("com1.txt", "device")]
    [Arguments("lpt9.x", "device")]
    [Arguments("aux", "device")]
    [Arguments("con.parquet", "device")]
    [Arguments("prn", "device")]
    [Arguments("com0", "device")]
    [Arguments("a/nul", "device")]
    [Arguments("ä", "contains")]
    public async Task TryCreateRefusesAnInvalidNameWithTheBrokenRule(string text, string ruleFragment)
    {
        bool created = ExportEntryName.TryCreate(text, out ExportEntryName name, out string? problem);

        await Assert.That(created).IsFalse();
        await Assert.That(name == ExportEntryName.Main).IsTrue();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!.Contains(ruleFragment, StringComparison.Ordinal)).IsTrue(); // Invariant: problem is set on false
    }

    /// <summary>Device names are only the exact stems; names that merely start like one are accepted.</summary>
    /// <param name="text">The accepted entry text.</param>
    /// <returns>The task of the test.</returns>
    [Test]
    [Arguments("nulx.parquet")]
    [Arguments("com10.parquet")]
    [Arguments("coma.parquet")]
    [Arguments("lpt.parquet")]
    public async Task TryCreateAcceptsANameThatOnlyLooksLikeADevice(string text)
    {
        bool created = ExportEntryName.TryCreate(text, out _, out _);

        await Assert.That(created).IsTrue();
    }

    /// <summary>A segment of exactly 255 characters is the longest accepted segment.</summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task TryCreateAcceptsASegmentOfTheMaximumLength()
    {
        string text = new('a', ExportEntryName.MaxSegmentLength);

        bool created = ExportEntryName.TryCreate(text, out ExportEntryName name, out _);

        await Assert.That(created).IsTrue();
        await Assert.That(name.Text).IsEqualTo(text);
    }

    /// <summary>A segment one character over the maximum is refused with the limit in the message.</summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task TryCreateRefusesASegmentOneOverTheMaximumLength()
    {
        string text = "fields/" + new string('a', ExportEntryName.MaxSegmentLength + 1);

        bool created = ExportEntryName.TryCreate(text, out _, out string? problem);

        await Assert.That(created).IsFalse();
        await Assert.That(problem!.Contains("at most 255", StringComparison.Ordinal)).IsTrue(); // Invariant: problem is set on false
    }

    /// <summary>A null text is a bug of the caller and throws instead of returning a refusal.</summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task TryCreateThrowsForANullText()
    {
        await Assert.That(static () => ExportEntryName.TryCreate(null!, out _, out _)).Throws<ArgumentNullException>();
    }

    #endregion

    #region Main and equality

    /// <summary>The main entry has no text, is the default value, and is never produced by <see cref="ExportEntryName.TryCreate"/>.</summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task MainIsTheEmptyDefaultEntry()
    {
        ExportEntryName main = ExportEntryName.Main;

        await Assert.That(main.IsMain).IsTrue();
        await Assert.That(main.Text).IsEqualTo(string.Empty);
        await Assert.That(default(ExportEntryName) == main).IsTrue();
        await Assert.That(main.ToString()).IsEqualTo("(main)");
        await Assert.That(ExportEntryName.TryCreate(string.Empty, out ExportEntryName created, out _)).IsFalse();
        await Assert.That(created.IsMain).IsTrue();
    }

    /// <summary>Two names built from the same text are equal and share a hash; different text or the main entry differ.</summary>
    /// <returns>The task of the test.</returns>
    [Test]
    public async Task NamesOfTheSameTextAreEqual()
    {
        ExportEntryName.TryCreate("packets.parquet", out ExportEntryName first, out _);
        ExportEntryName.TryCreate("packets.parquet", out ExportEntryName second, out _);
        ExportEntryName.TryCreate("catalog.parquet", out ExportEntryName other, out _);

        await Assert.That(first == second).IsTrue();
        await Assert.That(first.Equals((object)second)).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
        await Assert.That(first != other).IsTrue();
        await Assert.That(first == ExportEntryName.Main).IsFalse();
        await Assert.That(first.Equals("packets.parquet")).IsFalse();
        await Assert.That(first.ToString()).IsEqualTo("packets.parquet");
        await Assert.That(ExportEntryName.Main.GetHashCode()).IsEqualTo(0);
    }

    #endregion
}
