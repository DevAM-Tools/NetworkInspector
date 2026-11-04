// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// <see cref="FieldSelection"/>: field names from users are resolved against the stack once. Every request that cannot be
/// served (unknown, value-less, duplicate, empty) fails the whole selection with an error that names the field, so a typo never
/// becomes a silent full export or an empty column.
/// </summary>
internal sealed class FieldSelectionTests
{
    [Test]
    [Arguments("ip.scr", "ip.scr")]
    [Arguments("", "")]
    [Arguments("   ", "   ")]
    public async Task UnknownAndBlankNamesAreRefusedAndNamed(string name, string expectedName)
    {
        Stack stack = TestHarness.GetStack();

        bool created = FieldSelection.TryCreate(stack, [new FieldRequest(name)], Occurrences.All, out FieldSelection? selection, out FieldSelectionError error);

        await Assert.That(created).IsFalse();
        await Assert.That(selection).IsNull();
        await Assert.That(error.Code).IsEqualTo(FieldSelectionErrorCode.UnknownField);
        await Assert.That(error.FieldName).IsEqualTo(expectedName);
        await Assert.That(error.Message).Contains(expectedName);
    }

    [Test]
    public async Task AContainerWithoutAValueIsRefusedAsNoValue()
    {
        Stack stack = TestHarness.GetStack();

        bool created = FieldSelection.TryCreate(stack, [new FieldRequest("can.flags")], Occurrences.All, out _, out FieldSelectionError error);

        await Assert.That(created).IsFalse();
        await Assert.That(error.Code).IsEqualTo(FieldSelectionErrorCode.NoValue);
        await Assert.That(error.Message).Contains("'can.flags'");
    }

    [Test]
    public async Task TheSameFieldTwiceIsRefusedAsDuplicate()
    {
        Stack stack = TestHarness.GetStack();

        bool created = FieldSelection.TryCreate(
            stack, [new FieldRequest("ip.src"), new FieldRequest("udp.dstport"), new FieldRequest("ip.src")], Occurrences.All, out _, out FieldSelectionError error);

        await Assert.That(created).IsFalse();
        await Assert.That(error.Code).IsEqualTo(FieldSelectionErrorCode.Duplicate);
        await Assert.That(error.FieldName).IsEqualTo("ip.src");
    }

    [Test]
    public async Task NoRequestIsRefusedAsEmpty()
    {
        Stack stack = TestHarness.GetStack();

        bool created = FieldSelection.TryCreate(stack, ReadOnlySpan<FieldRequest>.Empty, Occurrences.All, out _, out FieldSelectionError error);

        await Assert.That(created).IsFalse();
        await Assert.That(error.Code).IsEqualTo(FieldSelectionErrorCode.Empty);
    }

    [Test]
    public async Task WithoutAnOccurrenceChoiceEveryFieldExportsAllOccurrencesAndAFieldChoiceWins()
    {
        Stack stack = TestHarness.GetStack();

        bool created = FieldSelection.TryCreate(
            stack, [new FieldRequest("ip.src"), new FieldRequest("udp.dstport", Occurrences.Last)], Occurrences.All, out FieldSelection? selection, out _);

        await Assert.That(created).IsTrue();
        await Assert.That(selection!.Fields.Count).IsEqualTo(2);
        await Assert.That(selection.Fields[0].Name).IsEqualTo("ip.src");
        await Assert.That(selection.Fields[0].Occurrences).IsEqualTo(Occurrences.All);
        await Assert.That(selection.Fields[1].Occurrences).IsEqualTo(Occurrences.Last);
        await Assert.That(selection.Fields[1].FieldType).IsNotEqualTo(FieldType.None);
    }

    [Test]
    public async Task TheDefaultOccurrenceChoiceAppliesToRequestsWithoutTheirOwn()
    {
        Stack stack = TestHarness.GetStack();

        _ = FieldSelection.TryCreate(stack, [new FieldRequest("ip.src")], Occurrences.First, out FieldSelection? selection, out _);

        await Assert.That(selection!.Fields[0].Occurrences).IsEqualTo(Occurrences.First);
    }

    [Test]
    public async Task TheWatchesAcceptTheSelectedFieldsAndTheDisplayWatchOnlyThem()
    {
        Stack stack = TestHarness.GetStack();
        FieldId ipSrc = stack.GetFieldId("ip.src")!.Value;
        FieldId ipDst = stack.GetFieldId("ip.dst")!.Value;
        _ = FieldSelection.TryCreate(stack, [new FieldRequest("ip.src")], Occurrences.All, out FieldSelection? selection, out _);

        // The extraction watch is widened to the owning protocol (so deferred containers expand); the display watch is not:
        // texts are only built for the fields that are written.
        await Assert.That(selection!.Watch.Accepts(ipSrc)).IsTrue();
        await Assert.That(selection.Watch.Accepts(ipDst)).IsTrue();
        await Assert.That(selection.DisplayWatch.Accepts(ipSrc)).IsTrue();
        await Assert.That(selection.DisplayWatch.Accepts(ipDst)).IsFalse();
    }

    [Test]
    public async Task AnUndefinedOccurrenceChoiceIsRefusedWithAnException()
    {
        Stack stack = TestHarness.GetStack();

        await Assert.That(() => FieldSelection.TryCreate(stack, [new FieldRequest("ip.src")], (Occurrences)9, out _, out _))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("defaultOccurrences");
    }

    [Test]
    public async Task AnUndefinedOccurrenceChoiceOfARequestNamesTheRequestsParameter()
    {
        Stack stack = TestHarness.GetStack();

        await Assert.That(() => FieldSelection.TryCreate(stack, [new FieldRequest("ip.src", (Occurrences)9)], Occurrences.All, out _, out _))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("requests");
    }

    [Test]
    public async Task ContentValidationRequiresASelectionExactlyForTheSelectedFieldsShape()
    {
        Stack stack = TestHarness.GetStack();
        _ = FieldSelection.TryCreate(stack, [new FieldRequest("ip.src")], Occurrences.All, out FieldSelection? selection, out _);

        bool selectedWithout = new ExportContent { Shape = ExportShape.SelectedFields }.TryValidateShape(out string? message1);
        bool allWith = new ExportContent { Shape = ExportShape.AllFields, Fields = selection }.TryValidateShape(out string? message2);
        bool selectedWith = new ExportContent { Shape = ExportShape.SelectedFields, Fields = selection }.TryValidateShape(out string? message3);
        bool hierarchy = new ExportContent().TryValidateShape(out string? message4);
        bool undefined = new ExportContent { Shape = (ExportShape)9 }.TryValidateShape(out string? message5);

        await Assert.That(selectedWithout).IsFalse();
        await Assert.That(message1).Contains("needs a field selection");
        await Assert.That(allWith).IsFalse();
        await Assert.That(message2).Contains("only valid with the shape SelectedFields");
        await Assert.That(selectedWith).IsTrue();
        await Assert.That(message3).IsNull();
        await Assert.That(hierarchy).IsTrue();
        await Assert.That(message4).IsNull();
        await Assert.That(undefined).IsFalse();
        await Assert.That(message5).Contains("Unknown export shape");
    }
}
