// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Shared;

/// <summary>
/// The builder shortcut <c>ToFile</c> and the shortcut <c>ToStream</c> give the same bytes for the same items in every
/// single-file format, and <c>ToFile</c> creates its file only with the first item.
/// </summary>
internal sealed class ShortcutsTests
{
    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Blf)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task ToFileAndToStreamGiveTheSameBytesAndTheFileAppearsWithTheFirstItem(SingleFileFormat format)
    {
        using TestDir dir = new("shortcuts_same");
        string path = dir.FilePath("out.bin");
        using MemoryStream stream = new();

        SingleFileTargetTests.Export(format, SingleFileTargetTests.Output.ToStream(stream), 2);
        bool existedBeforeTheFirstItem;
        using (SingleFileTargetTests.Run run = SingleFileTargetTests.Create(format, SingleFileTargetTests.Output.ToFile(path)))
        {
            existedBeforeTheFirstItem = File.Exists(path);
            run.Offer(0);
            run.Offer(1);
            run.Finish();
        }

        byte[] fileBytes = await File.ReadAllBytesAsync(path);
        await Assert.That(existedBeforeTheFirstItem).IsFalse();
        await Assert.That(SingleFileTargetTests.SameOutput(format, fileBytes, stream.ToArray())).IsTrue();
    }

    [Test]
    [Arguments(SingleFileFormat.Csv)]
    [Arguments(SingleFileFormat.Json)]
    [Arguments(SingleFileFormat.Text)]
    [Arguments(SingleFileFormat.PbfStandard)]
    [Arguments(SingleFileFormat.PbfColumnar)]
    [Arguments(SingleFileFormat.Pcapng)]
    [Arguments(SingleFileFormat.Blf)]
    [Arguments(SingleFileFormat.Asc)]
    public async Task TheLastOutputCallWinsAndTheReplacedOutputIsNeverOpened(SingleFileFormat format)
    {
        using TestDir dir = new("shortcuts_replaced");
        string replacedFile = dir.FilePath("replaced.bin");
        string winningFile = dir.FilePath("wins.bin");
        MemoryExportTarget winningTarget = new();
        MemoryExportTarget replacedTarget = new();

        // ToFile then ToTarget: the target wins and the file is never created. ToTarget then ToFile: the reverse.
        SingleFileTargetTests.Export(format, SingleFileTargetTests.Output.FileThenTarget(replacedFile, winningTarget), 2);
        SingleFileTargetTests.Export(format, SingleFileTargetTests.Output.TargetThenFile(replacedTarget, winningFile), 2);

        await Assert.That(File.Exists(replacedFile)).IsFalse();
        await Assert.That(winningTarget.EntryNames.Count).IsEqualTo(1);
        await Assert.That(winningTarget.Bytes(string.Empty).Length).IsGreaterThan(0);
        await Assert.That(replacedTarget.EntryNames.Count).IsEqualTo(0);
        await Assert.That(File.Exists(winningFile)).IsTrue();
    }
}
