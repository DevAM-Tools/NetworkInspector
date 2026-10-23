// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The target rules every export runner applies: overwrite decisions before <c>Create</c> and cleanup after a failed export.</summary>
internal sealed class ExportTargetsTests
{
    #region Helpers

    private static (Stack Stack, List<Packet> Packets, List<Frame> Frames) _Traffic(int count, ulong seed)
    {
        Stack stack = SampleStack.Build(withSample: false);
        List<Frame> frames = SmallTraceWriter.Generate(count, seed, stack.FrameInterfaceRegistry);
        return (stack, [.. frames.Select(frame => SampleStack.Parse(stack, frame))], frames);
    }

    private static ExporterRegistration<IPacketListener> _Packet(string format)
    {
        RecordingPluginBuilder recording = new();
        new ExportersPlugin().Configure(recording);
        return recording.PacketExporters.Single(r => r.Format == format);
    }

    private static ExporterRegistration<IFrameListener> _Frame(string format)
    {
        RecordingPluginBuilder recording = new();
        new ExportersPlugin().Configure(recording);
        return recording.FrameExporters.Single(r => r.Format == format);
    }

    #endregion

    #region Overwrite

    [Test]
    public async Task AnExistingFileOrFolderIsRefusedWithoutOverwriteAndStaysUntouched()
    {
        using TempFolder folder = new("targets-refuse");
        string file = folder.FilePath("out.csv");
        string dataset = folder.FilePath("dataset");
        File.WriteAllText(file, "keep");
        Directory.CreateDirectory(dataset);
        File.WriteAllText(Path.Combine(dataset, "keep.txt"), "keep");

        bool fileOk = ExportTargets.TryPrepare(file, isDirectory: false, overwrite: false, out string? filePath, out _, out PluginError? fileError);
        bool folderOk = ExportTargets.TryPrepare(dataset, isDirectory: true, overwrite: false, out string? folderPath, out _, out PluginError? folderError);

        await Assert.That(fileOk).IsFalse();
        await Assert.That(filePath).IsNull();
        await Assert.That(Expect.NotNull(fileError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(Expect.NotNull(fileError).Message).IsEqualTo($"Output '{file}' exists.");
        await Assert.That(folderOk).IsFalse();
        await Assert.That(folderPath).IsNull();
        await Assert.That(Expect.NotNull(folderError).Message).IsEqualTo($"Output '{dataset}' exists.");
        await Assert.That(File.ReadAllText(file)).IsEqualTo("keep");
        await Assert.That(File.Exists(Path.Combine(dataset, "keep.txt"))).IsTrue();
    }

    [Test]
    public async Task OverwriteMovesTheOldTargetAsideAndSettlingRemovesItOnceTheNewOutputExists()
    {
        using TempFolder folder = new("targets-overwrite");
        (Stack stack, List<Packet> packets, _) = _Traffic(5, 21);
        string file = folder.FilePath("out.csv");
        string dataset = folder.FilePath("dataset");
        File.WriteAllText(file, "old");
        Directory.CreateDirectory(dataset);
        File.WriteAllText(Path.Combine(dataset, ExportTargets.DatasetMarkerFileName), string.Empty);
        File.WriteAllText(Path.Combine(dataset, "old.txt"), "old");

        bool fileOk = ExportTargets.TryPrepare(file, isDirectory: false, overwrite: true, out string? filePath,
            out TargetReplacement? fileReplacement, out PluginError? fileError);
        bool folderOk = ExportTargets.TryPrepare(dataset, isDirectory: true, overwrite: true, out string? folderPath,
            out TargetReplacement? folderReplacement, out PluginError? folderError);

        // The paths are free before any exporter exists, and the old outputs are kept next to them, not deleted.
        await Assert.That(fileOk).IsTrue();
        await Assert.That(folderOk).IsTrue();
        await Assert.That(fileError).IsNull();
        await Assert.That(folderError).IsNull();
        await Assert.That(File.Exists(file)).IsFalse();
        await Assert.That(Directory.Exists(dataset)).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(folder.Path, ".*.ni-replaced-*").Length).IsEqualTo(2);

        // The exporters then create the new outputs at the prepared paths.
        IPacketListener csv = Expect.NotNull(RegistrationRunner.Create(_Packet("CSV"), Expect.NotNull(filePath), stack).Listener);
        IPacketListener parquet = Expect.NotNull(RegistrationRunner.Create(_Packet("Parquet"), Expect.NotNull(folderPath), stack).Listener);
        foreach (IPacketListener listener in new[] { csv, parquet })
        {
            foreach (Packet packet in packets)
            {
                listener.OnPacket(packet);
            }

            listener.OnFinish();
            ((IDisposable)listener).Dispose();
        }

        string? fileProblem = Expect.NotNull(fileReplacement).Settle();
        string? folderProblem = Expect.NotNull(folderReplacement).Settle();

        await Assert.That(fileProblem).IsNull();
        await Assert.That(folderProblem).IsNull();
        await Assert.That(File.ReadAllText(file)).DoesNotContain("old");
        await Assert.That(Directory.EnumerateFileSystemEntries(dataset).Any()).IsTrue();
        await Assert.That(File.Exists(Path.Combine(dataset, "old.txt"))).IsFalse();
        await Assert.That(Directory.GetFileSystemEntries(folder.Path, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task SettlingWithoutANewOutputGivesTheOldFileAndTheOldFolderBack()
    {
        using TempFolder folder = new("targets-restore");
        string file = folder.FilePath("precious.csv");
        string dataset = folder.FilePath("dataset");
        File.WriteAllText(file, "my previous good export");
        Directory.CreateDirectory(dataset);
        File.WriteAllText(Path.Combine(dataset, ExportTargets.DatasetMarkerFileName), string.Empty);
        File.WriteAllText(Path.Combine(dataset, "part.bin"), "old");
        bool fileOk = ExportTargets.TryPrepare(file, isDirectory: false, overwrite: true, out _, out TargetReplacement? fileReplacement, out _);
        bool folderOk = ExportTargets.TryPrepare(dataset, isDirectory: true, overwrite: true, out _, out TargetReplacement? folderReplacement, out _);

        await Assert.That(fileOk && folderOk).IsTrue();

        // The run was refused or cancelled before an exporter wrote anything.
        string? fileProblem = Expect.NotNull(fileReplacement).Settle();
        string? folderProblem = Expect.NotNull(folderReplacement).Settle();
        string? again = Expect.NotNull(fileReplacement).Settle();

        await Assert.That(fileProblem).IsNull();
        await Assert.That(folderProblem).IsNull();
        await Assert.That(again).IsNull();
        await Assert.That(File.ReadAllText(file)).IsEqualTo("my previous good export");
        await Assert.That(File.ReadAllText(Path.Combine(dataset, "part.bin"))).IsEqualTo("old");
        await Assert.That(Directory.GetFileSystemEntries(folder.Path, ".*.ni-replaced-*")).IsEmpty();
    }

    [Test]
    public async Task ANonEmptyFolderWithoutTheMarkerIsNotReplaced()
    {
        using TempFolder folder = new("targets-unmarked");
        string dataset = folder.FilePath("data");
        Directory.CreateDirectory(dataset);
        File.WriteAllText(Path.Combine(dataset, "notes.txt"), "mine");

        bool prepared = ExportTargets.TryPrepare(dataset, isDirectory: true, overwrite: true, out string? fullPath, out TargetReplacement? replacement,
            out PluginError? error);
        bool checkedOnly = ExportTargets.TryCheck(dataset, isDirectory: true, overwrite: true, out _, out PluginError? checkError);

        await Assert.That(prepared).IsFalse();
        await Assert.That(fullPath).IsNull();
        await Assert.That(replacement).IsNull();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(checkedOnly).IsFalse();
        await Assert.That(Expect.NotNull(checkError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(File.ReadAllText(Path.Combine(dataset, "notes.txt"))).IsEqualTo("mine");
    }

    [Test]
    public async Task AnEmptyFolderAndAFolderWithTheMarkerAreReplaced()
    {
        using TempFolder folder = new("targets-marked");
        string empty = folder.FilePath("empty");
        string marked = folder.FilePath("marked");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(marked);
        string? markedProblem = ExportTargets.MarkDataset(marked);

        bool emptyOk = ExportTargets.TryCheck(empty, isDirectory: true, overwrite: true, out _, out _);
        bool markedOk = ExportTargets.TryCheck(marked, isDirectory: true, overwrite: true, out _, out _);
        string? firstMark = ExportTargets.MarkDataset(folder.FilePath("new-dataset"));
        string? secondMark = ExportTargets.MarkDataset(folder.FilePath("new-dataset"));

        await Assert.That(markedProblem).IsNull();
        await Assert.That(emptyOk).IsTrue();
        await Assert.That(markedOk).IsTrue();
        await Assert.That(firstMark).IsNull();
        await Assert.That(secondMark).IsNull();
        await Assert.That(File.Exists(Path.Combine(folder.FilePath("new-dataset"), ExportTargets.DatasetMarkerFileName))).IsTrue();
    }

    [Test]
    public async Task AnOutputThatCannotBeMovedAsideIsAnErrorAndStaysInPlace()
    {
        // Only Windows refuses to move an open file; elsewhere the move succeeds and there is nothing to refuse.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TempFolder folder = new("targets-locked-move");
        string locked = folder.FilePath("locked.csv");
        using FileStream open = new(locked, FileMode.Create, FileAccess.Write, FileShare.None);

        bool prepared = ExportTargets.TryPrepare(locked, isDirectory: false, overwrite: true, out string? fullPath, out TargetReplacement? replacement,
            out PluginError? error);

        await Assert.That(prepared).IsFalse();
        await Assert.That(fullPath).IsNull();
        await Assert.That(replacement).IsNull();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.FileUnreadable);
        await Assert.That(File.Exists(locked)).IsTrue();
    }

    [Test]
    public async Task MarkDatasetRejectsABlankPath()
    {
        await Assert.That(() => ExportTargets.MarkDataset(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AThingOfTheWrongKindIsRefusedEvenWithOverwrite()
    {
        using TempFolder folder = new("targets-kind");
        string folderAtFile = folder.FilePath("looks-like-a-file.csv");
        string fileAtFolder = folder.FilePath("looks-like-a-folder");
        Directory.CreateDirectory(folderAtFile);
        File.WriteAllText(fileAtFolder, "keep");

        bool asFile = ExportTargets.TryPrepare(folderAtFile, isDirectory: false, overwrite: true, out _, out _, out PluginError? fileError);
        bool asFolder = ExportTargets.TryPrepare(fileAtFolder, isDirectory: true, overwrite: true, out _, out _, out PluginError? folderError);

        await Assert.That(asFile).IsFalse();
        await Assert.That(asFolder).IsFalse();
        await Assert.That(Expect.NotNull(fileError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(Expect.NotNull(folderError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(Directory.Exists(folderAtFile)).IsTrue();
        await Assert.That(File.ReadAllText(fileAtFolder)).IsEqualTo("keep");
    }

    [Test]
    public async Task AFreeTargetIsReturnedAsAFullPathAndNothingIsCreated()
    {
        using TempFolder folder = new("targets-free");
        string relative = "ni-relative-" + Guid.NewGuid().ToString("N") + ".csv";

        bool relativeOk = ExportTargets.TryPrepare(relative, isDirectory: false, overwrite: false, out string? relativePath, out _, out _);
        bool absoluteOk = ExportTargets.TryPrepare(folder.FilePath("new.json"), isDirectory: false, overwrite: false, out string? absolutePath, out _, out _);

        // A relative path is resolved against the current directory; preparing creates no file.
        await Assert.That(relativeOk).IsTrue();
        await Assert.That(relativePath).IsEqualTo(Path.Combine(Directory.GetCurrentDirectory(), relative));
        await Assert.That(File.Exists(relativePath)).IsFalse();
        await Assert.That(absoluteOk).IsTrue();
        await Assert.That(absolutePath).IsEqualTo(folder.FilePath("new.json"));
        await Assert.That(File.Exists(absolutePath)).IsFalse();
    }

    [Test]
    public async Task PathProblemsAreErrorsNotExceptions()
    {
        using TempFolder folder = new("targets-paths");
        string missingParent = Path.Combine(folder.Path, "missing", "out.csv");
        string root = Expect.NotNull(Path.GetPathRoot(folder.Path));

        bool missingOk = ExportTargets.TryPrepare(missingParent, isDirectory: false, overwrite: true, out _, out _, out PluginError? missing);
        bool blankOk = ExportTargets.TryPrepare("  ", isDirectory: false, overwrite: false, out _, out _, out PluginError? blank);
        bool nullOk = ExportTargets.TryPrepare(string.Empty, isDirectory: false, overwrite: false, out _, out _, out PluginError? empty);
        bool rootOk = ExportTargets.TryPrepare(root, isDirectory: true, overwrite: true, out _, out _, out PluginError? rootError);

        await Assert.That(missingOk).IsFalse();
        await Assert.That(Expect.NotNull(missing).Code).IsEqualTo(PluginErrorCode.FileNotFound);
        await Assert.That(Expect.NotNull(missing).Message).IsEqualTo($"Folder '{Path.Combine(folder.Path, "missing")}' does not exist.");
        await Assert.That(blankOk).IsFalse();
        await Assert.That(Expect.NotNull(blank).Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(nullOk).IsFalse();
        await Assert.That(Expect.NotNull(empty).Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(rootOk).IsFalse();
        await Assert.That(Expect.NotNull(rootError).Code).IsEqualTo(PluginErrorCode.InvalidOption);
    }

    [Test]
    public async Task ADirectoryTargetWithATrailingSeparatorIsNormalized()
    {
        using TempFolder folder = new("targets-trailing");
        string target = folder.FilePath("dataset");

        bool ok = ExportTargets.TryPrepare(target + Path.DirectorySeparatorChar, isDirectory: true, overwrite: false,
            out string? path, out _, out PluginError? error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(path).IsEqualTo(target);
    }

    [Test]
    public async Task DeletePartialNeverRemovesAFolderThatContainsTheCurrentDirectory()
    {
        string current = Directory.GetCurrentDirectory();

        string? problem = ExportTargets.DeletePartial(current, isDirectory: true);

        await Assert.That(problem).IsNotNull();
        await Assert.That(Directory.Exists(current)).IsTrue();
    }

    [Test]
    public async Task TryCheckAppliesTheRulesWithoutChangingAnything()
    {
        using TempFolder folder = new("targets-check");
        string file = folder.FilePath("out.csv");
        File.WriteAllText(file, "keep");
        string folderAtFile = folder.FilePath("folder.csv");
        Directory.CreateDirectory(folderAtFile);

        bool refused = ExportTargets.TryCheck(file, isDirectory: false, overwrite: false, out _, out PluginError? existsError);
        bool accepted = ExportTargets.TryCheck(file, isDirectory: false, overwrite: true, out string? acceptedPath, out PluginError? acceptedError);
        bool wrongKind = ExportTargets.TryCheck(folderAtFile, isDirectory: false, overwrite: true, out _, out PluginError? kindError);
        bool missingParent = ExportTargets.TryCheck(folder.FilePath("missing/out.csv"), isDirectory: false, overwrite: true, out _, out PluginError? parentError);

        await Assert.That(refused).IsFalse();
        await Assert.That(Expect.NotNull(existsError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(accepted).IsTrue();
        await Assert.That(acceptedError).IsNull();
        await Assert.That(acceptedPath).IsEqualTo(file);
        await Assert.That(wrongKind).IsFalse();
        await Assert.That(Expect.NotNull(kindError).Code).IsEqualTo(PluginErrorCode.TargetExists);
        await Assert.That(missingParent).IsFalse();
        await Assert.That(Expect.NotNull(parentError).Code).IsEqualTo(PluginErrorCode.FileNotFound);

        // Nothing was deleted by any of the checks.
        await Assert.That(File.ReadAllText(file)).IsEqualTo("keep");
        await Assert.That(Directory.Exists(folderAtFile)).IsTrue();
    }

    [Test]
    public async Task OverwriteNeverRemovesAFolderThatContainsTheCurrentDirectory()
    {
        string current = Directory.GetCurrentDirectory();

        bool ok = ExportTargets.TryPrepare(current, isDirectory: true, overwrite: true, out string? path, out _, out PluginError? error);

        await Assert.That(ok).IsFalse();
        await Assert.That(path).IsNull();
        await Assert.That(Expect.NotNull(error).Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(Directory.Exists(current)).IsTrue();
    }

    #endregion

    #region Partial output

    [Test]
    public async Task DeletePartialRemovesWhatAnUnfinishedExportLeftBehind()
    {
        using TempFolder folder = new("targets-partial");
        (Stack stack, List<Packet> packets, List<Frame> frames) = _Traffic(5, 22);
        string json = folder.FilePath("partial.json");
        string dataset = folder.FilePath("partial-dataset");
        string blf = folder.FilePath("partial.blf");

        IPacketListener jsonListener = Expect.NotNull(RegistrationRunner.Create(_Packet("JSON"), json, stack).Listener);
        IPacketListener parquetListener = Expect.NotNull(RegistrationRunner.Create(_Packet("Parquet"), dataset, stack).Listener);
        IFrameListener blfListener = Expect.NotNull(RegistrationRunner.Create(_Frame("BLF"), blf, stack).Listener);
        foreach (Packet packet in packets)
        {
            jsonListener.OnPacket(packet);
            parquetListener.OnPacket(packet);
        }

        foreach (Frame frame in frames)
        {
            blfListener.OnFrame(frame);
        }

        // The runner disposes the listeners without OnFinish (a cancelled export) and then cleans up.
        ((IDisposable)jsonListener).Dispose();
        ((IDisposable)parquetListener).Dispose();
        ((IDisposable)blfListener).Dispose();
        string? jsonProblem = ExportTargets.DeletePartial(json, isDirectory: false);
        string? parquetProblem = ExportTargets.DeletePartial(dataset, isDirectory: true);
        string? blfProblem = ExportTargets.DeletePartial(blf, isDirectory: false);

        await Assert.That(jsonProblem).IsNull();
        await Assert.That(parquetProblem).IsNull();
        await Assert.That(blfProblem).IsNull();
        await Assert.That(File.Exists(json)).IsFalse();
        await Assert.That(Directory.Exists(dataset)).IsFalse();
        await Assert.That(File.Exists(blf)).IsFalse();
    }

    [Test]
    public async Task AFinishedExportKeepsItsOutputWhenTheRunnerSkipsTheCleanup()
    {
        using TempFolder folder = new("targets-finished");
        (Stack stack, List<Packet> packets, _) = _Traffic(5, 23);
        string json = folder.FilePath("finished.json");

        IPacketListener listener = Expect.NotNull(RegistrationRunner.Create(_Packet("JSON"), json, stack).Listener);
        foreach (Packet packet in packets)
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
        ((IDisposable)listener).Dispose();

        await Assert.That(File.Exists(json)).IsTrue();
        await Assert.That(new FileInfo(json).Length).IsGreaterThan(0L);
    }

    [Test]
    public async Task DeletePartialOfNothingIsANoOpAndALockedFileIsReportedNotThrown()
    {
        using TempFolder folder = new("targets-locked");

        // A plugin listener may have deleted its output already: nothing to do, no problem.
        await Assert.That(ExportTargets.DeletePartial(folder.FilePath("gone.csv"), isDirectory: false)).IsNull();
        await Assert.That(ExportTargets.DeletePartial(folder.FilePath("gone"), isDirectory: true)).IsNull();

        // Only Windows refuses to delete an open file; elsewhere the deletion succeeds and there is nothing to report.
        if (OperatingSystem.IsWindows())
        {
            string locked = folder.FilePath("locked.csv");
            using FileStream open = new(locked, FileMode.Create, FileAccess.Write, FileShare.None);
            string? problem = ExportTargets.DeletePartial(locked, isDirectory: false);

            await Assert.That(problem).IsNotNull();
            await Assert.That(problem).IsNotEmpty();
        }
    }

    [Test]
    public async Task DeletePartialRejectsAnEmptyPath()
    {
        await Assert.That(() => ExportTargets.DeletePartial(" ", isDirectory: false)).Throws<ArgumentException>();
    }

    #endregion
}
