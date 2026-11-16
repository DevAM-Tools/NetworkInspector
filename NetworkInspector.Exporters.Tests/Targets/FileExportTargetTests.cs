// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Targets;

/// <summary>
/// Tests for <see cref="FileExportTarget"/>: the lazy file, the one-open rule, the refusal of named entries, and the failure of a
/// path whose folder is missing.
/// </summary>
internal sealed class FileExportTargetTests
{
    [Test]
    public async Task NoFileExistsUntilTheMainEntryIsOpened()
    {
        using TestDir dir = new("file_target_lazy");
        string path = dir.FilePath("out.bin");
        FileExportTarget target = new(path);

        await Assert.That(File.Exists(path)).IsFalse();

        bool opened = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);
        using Stream entry = stream!; // Invariant: stream is set when TryOpenWrite returns true

        await Assert.That(opened).IsTrue();
        await Assert.That(failure).IsEqualTo(string.Empty);
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(entry.CanWrite).IsTrue();
        await Assert.That(entry.CanRead).IsFalse();
    }

    [Test]
    public async Task OpeningTruncatesAnExistingFile()
    {
        using TestDir dir = new("file_target_truncate");
        string path = dir.FilePath("out.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        FileExportTarget target = new(path);

        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        stream!.Dispose(); // Invariant: stream is set when TryOpenWrite returns true

        await Assert.That(new FileInfo(path).Length).IsEqualTo(0L);
    }

    [Test]
    public async Task TheEntryWritesTheBytesToTheFile()
    {
        using TestDir dir = new("file_target_bytes");
        string path = dir.FilePath("out.bin");
        FileExportTarget target = new(path);

        target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        stream!.Write([9, 8, 7]); // Invariant: stream is set when TryOpenWrite returns true
        stream.Dispose();

        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 9, 8, 7 });
        await Assert.That(target.FullPath).IsEqualTo(Path.GetFullPath(path));
    }

    [Test]
    public async Task ASecondOpenIsRefusedAndKeepsTheFirstEntryIntact()
    {
        using TestDir dir = new("file_target_second");
        FileExportTarget target = new(dir.FilePath("out.bin"));
        target.TryOpenWrite(ExportEntryName.Main, out Stream? first, out _);
        using Stream firstEntry = first!; // Invariant: stream is set when TryOpenWrite returns true

        bool second = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);

        await Assert.That(second).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("already opened");
    }

    [Test]
    public async Task ANamedEntryIsRefusedWithTheNameOfTheDirectoryTarget()
    {
        using TestDir dir = new("file_target_named");
        string path = dir.FilePath("out.bin");
        FileExportTarget target = new(path);
        ExportEntryName.TryCreate("packets.parquet", out ExportEntryName named, out _);

        bool opened = target.TryOpenWrite(named, out Stream? stream, out string failure);

        await Assert.That(opened).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("DirectoryExportTarget");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task APathInAMissingFolderIsRefusedWithTheReasonAndLeavesNoFile()
    {
        using TestDir dir = new("file_target_missing");
        string folder = Path.Combine(dir.DirectoryPath, "missing");
        string path = Path.Combine(folder, "out.bin");
        FileExportTarget target = new(path);

        bool opened = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);

        await Assert.That(opened).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure.Length).IsGreaterThan(0);
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(Directory.Exists(folder)).IsFalse();
    }

    [Test]
    public async Task AFailedOpenIsNotRetriedEvenWhenTheFolderAppearsLater()
    {
        using TestDir dir = new("file_target_no_retry");
        string folder = Path.Combine(dir.DirectoryPath, "later");
        string path = Path.Combine(folder, "out.bin");
        FileExportTarget target = new(path);
        bool first = target.TryOpenWrite(ExportEntryName.Main, out _, out _);
        Directory.CreateDirectory(folder);

        bool second = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("already opened");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task ARefusedNamedEntryDoesNotUseUpTheMainEntry()
    {
        using TestDir dir = new("file_target_named_then_main");
        string path = dir.FilePath("out.bin");
        FileExportTarget target = new(path);
        ExportEntryName.TryCreate("packets.parquet", out ExportEntryName named, out _);

        bool namedOpened = target.TryOpenWrite(named, out _, out _);
        bool mainOpened = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out _);
        stream?.Dispose();

        await Assert.That(namedOpened).IsFalse();
        await Assert.That(mainOpened).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task TheConstructorRejectsAnEmptyPathAnInvalidPathAndNull()
    {
        await Assert.That(static () => new FileExportTarget(string.Empty)).Throws<ArgumentException>();
        await Assert.That(static () => new FileExportTarget("bad\0name")).Throws<ArgumentException>();
        await Assert.That(static () => new FileExportTarget(null!)).Throws<ArgumentNullException>();
    }
}
