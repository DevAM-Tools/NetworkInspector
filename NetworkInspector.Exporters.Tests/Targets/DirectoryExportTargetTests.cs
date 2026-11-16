// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Targets;

/// <summary>
/// Tests for <see cref="DirectoryExportTarget"/>: lazy folders, nested entries, the one-open rule per name, refusals with the
/// operating system's reason, and concurrent opens.
/// </summary>
internal sealed class DirectoryExportTargetTests
{
    /// <summary>Creates a valid entry name.</summary>
    /// <param name="text">The entry text; must match the grammar.</param>
    /// <returns>The entry name.</returns>
    private static ExportEntryName _Name(string text)
    {
        ExportEntryName.TryCreate(text, out ExportEntryName name, out _);
        return name;
    }

    [Test]
    public async Task NothingIsCreatedUntilTheFirstOpen()
    {
        using TestDir dir = new("dir_target_lazy");
        string root = Path.Combine(dir.DirectoryPath, "dataset");

        DirectoryExportTarget target = new(root);

        await Assert.That(Directory.Exists(root)).IsFalse();
        await Assert.That(target.RootPath).IsEqualTo(Path.GetFullPath(root));
    }

    [Test]
    public async Task ANestedEntryCreatesTheFoldersAndTheFileBelowTheRoot()
    {
        using TestDir dir = new("dir_target_nested");
        string root = Path.Combine(dir.DirectoryPath, "dataset");
        DirectoryExportTarget target = new(root);

        bool opened = target.TryOpenWrite(_Name("fields/field_1.parquet"), out Stream? stream, out string failure);
        stream!.Write([1]); // Invariant: stream is set when TryOpenWrite returns true
        stream.Dispose();

        await Assert.That(opened).IsTrue();
        await Assert.That(failure).IsEqualTo(string.Empty);
        await Assert.That(File.Exists(Path.Combine(root, "fields", "field_1.parquet"))).IsTrue();
        await Assert.That(new FileInfo(Path.Combine(root, "fields", "field_1.parquet")).Length).IsEqualTo(1L);
    }

    [Test]
    public async Task TheSameNameASecondTimeIsRefused()
    {
        using TestDir dir = new("dir_target_twice");
        DirectoryExportTarget target = new(dir.DirectoryPath);
        target.TryOpenWrite(_Name("a.parquet"), out Stream? first, out _);
        using Stream firstEntry = first!; // Invariant: stream is set when TryOpenWrite returns true

        bool second = target.TryOpenWrite(_Name("a.parquet"), out Stream? stream, out string failure);

        await Assert.That(second).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("already opened");
    }

    [Test]
    public async Task TheMainEntryIsRefusedWithTheNameOfTheFileTarget()
    {
        using TestDir dir = new("dir_target_main");
        string root = Path.Combine(dir.DirectoryPath, "dataset");
        DirectoryExportTarget target = new(root);

        bool opened = target.TryOpenWrite(ExportEntryName.Main, out Stream? stream, out string failure);

        await Assert.That(opened).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).Contains("FileExportTarget");
        await Assert.That(Directory.Exists(root)).IsFalse();
    }

    [Test]
    public async Task AFolderOnTheEntryPathIsRefusedWithTheReasonAndALaterOpenOfAnotherNameWorks()
    {
        using TestDir dir = new("dir_target_occupied");
        Directory.CreateDirectory(Path.Combine(dir.DirectoryPath, "a.parquet"));
        DirectoryExportTarget target = new(dir.DirectoryPath);

        bool occupied = target.TryOpenWrite(_Name("a.parquet"), out Stream? refused, out string failure);
        bool other = target.TryOpenWrite(_Name("b.parquet"), out Stream? stream, out _);
        stream?.Dispose();

        await Assert.That(occupied).IsFalse();
        await Assert.That(refused).IsNull();
        await Assert.That(failure.Length).IsGreaterThan(0);
        await Assert.That(other).IsTrue();
        await Assert.That(File.Exists(Path.Combine(dir.DirectoryPath, "b.parquet"))).IsTrue();
    }

    [Test]
    public async Task AFailedOpenReleasesTheNameSoTheSameNameOpensOnceTheObstacleIsGone()
    {
        using TestDir dir = new("dir_target_retry");
        string obstacle = Path.Combine(dir.DirectoryPath, "a.parquet");
        Directory.CreateDirectory(obstacle);
        DirectoryExportTarget target = new(dir.DirectoryPath);

        bool occupied = target.TryOpenWrite(_Name("a.parquet"), out Stream? refused, out _);
        Directory.Delete(obstacle);
        bool retried = target.TryOpenWrite(_Name("a.parquet"), out Stream? stream, out string failure);
        stream?.Dispose();

        await Assert.That(occupied).IsFalse();
        await Assert.That(refused).IsNull();
        await Assert.That(retried).IsTrue();
        await Assert.That(failure).IsEqualTo(string.Empty);
        await Assert.That(File.Exists(obstacle)).IsTrue();
    }

    [Test]
    public async Task ARootWithATrailingSeparatorAcceptsEntriesAndReportsTheRootWithoutIt()
    {
        using TestDir dir = new("dir_target_trailing");
        string root = Path.Combine(dir.DirectoryPath, "dataset") + Path.DirectorySeparatorChar;
        DirectoryExportTarget target = new(root);

        bool opened = target.TryOpenWrite(_Name("fields/field_1.parquet"), out Stream? stream, out string failure);
        stream?.Dispose();

        await Assert.That(opened).IsTrue();
        await Assert.That(failure).IsEqualTo(string.Empty);
        await Assert.That(target.RootPath).IsEqualTo(Path.TrimEndingDirectorySeparator(root));
        await Assert.That(File.Exists(Path.Combine(root, "fields", "field_1.parquet"))).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task OpeningNEntriesCreatesNFilesAndZeroOpensLeaveNoFolder(int count)
    {
        using TestDir dir = new("dir_target_count");
        string root = Path.Combine(dir.DirectoryPath, "dataset");
        DirectoryExportTarget target = new(root);

        for (int index = 0; index < count; index++)
        {
            target.TryOpenWrite(_Name(Invariant($"entry_{index}.parquet")), out Stream? stream, out _);
            stream!.Dispose(); // Invariant: stream is set when TryOpenWrite returns true
        }

        int files = Directory.Exists(root)
            ? Directory.GetFiles(root).Length
            : 0;
        await Assert.That(files).IsEqualTo(count);
        await Assert.That(Directory.Exists(root)).IsEqualTo(count > 0);
    }

    [Test]
    public async Task TheConstructorRejectsAnEmptyAndAnInvalidRoot()
    {
        await Assert.That(static () => new DirectoryExportTarget(string.Empty)).Throws<ArgumentException>();
        await Assert.That(static () => new DirectoryExportTarget("bad\0root")).Throws<ArgumentException>();
        await Assert.That(static () => new DirectoryExportTarget(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task EightThreadsOpeningOneNameYieldOneSuccess()
    {
        using TestDir dir = new("dir_target_race");
        DirectoryExportTarget target = new(dir.DirectoryPath);
        ExportEntryName name = _Name("same.parquet");
        using Barrier barrier = new(8);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            barrier.SignalAndWait();
            bool opened = target.TryOpenWrite(name, out Stream? stream, out _);
            stream?.Dispose();
            return opened;
        })));

        await Assert.That(results.Count(static opened => opened)).IsEqualTo(1);
    }

    [Test]
    public async Task EightThreadsOpeningEightNamesAllSucceed()
    {
        using TestDir dir = new("dir_target_race_many");
        DirectoryExportTarget target = new(dir.DirectoryPath);
        using Barrier barrier = new(8);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            bool opened = target.TryOpenWrite(_Name(Invariant($"fields/field_{index}.parquet")), out Stream? stream, out _);
            stream?.Dispose();
            return opened;
        })));

        await Assert.That(results.All(static opened => opened)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.Combine(dir.DirectoryPath, "fields")).Length).IsEqualTo(8);
    }
}
