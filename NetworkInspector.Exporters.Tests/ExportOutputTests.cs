// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Tests for <see cref="ExportOutput"/>: the lazy open of the main entry, the refusal of an open as an I/O error, the byte
/// count, and the ownership rule of the closing dispose.
/// </summary>
internal sealed class ExportOutputTests
{
    [Test]
    public async Task TheEntryOpensOnTheFirstWriteAndNotBefore()
    {
        MemoryExportTarget target = new();
        using ExportOutput output = ExportOutput.FromTarget(target);

        output.Flush();
        await Assert.That(target.EntryNames.Count).IsEqualTo(0);
        await Assert.That(output.TryGetExistingStream()).IsNull();

        output.Write([1, 2, 3]);

        await Assert.That(target.EntryNames.Count).IsEqualTo(1);
        await Assert.That(target.EntryNames[0]).IsEqualTo(string.Empty);
        await Assert.That(output.AcceptedBytes).IsEqualTo(3L);
        await Assert.That(output.TryGetExistingStream()).IsNotNull();
    }

    [Test]
    public async Task AFileOutputCreatesNoFileUntilTheFirstWrite()
    {
        using TestDir dir = new("output_file_lazy");
        string path = dir.FilePath("out.bin");

        using (ExportOutput output = ExportOutput.File(path))
        {
            await Assert.That(File.Exists(path)).IsFalse();
            output.Write([4, 5]);
        }

        await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(new byte[] { 4, 5 });
    }

    [Test]
    public async Task ARefusedOpenThrowsAnIoExceptionWithTheReasonFromWrite()
    {
        MemoryExportTarget target = new(refuse: static _ => "share offline");
        using ExportOutput output = ExportOutput.FromTarget(target);

        IOException? exception = Assert.Throws<IOException>(() => output.Write([1]));

        await Assert.That(exception!.Message).Contains("share offline");
        await Assert.That(output.AcceptedBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task ARefusedOpenReturnsFalseWithTheReasonFromTheStreamRequest()
    {
        MemoryExportTarget target = new(refuse: static _ => "share offline");
        using ExportOutput output = ExportOutput.FromTarget(target);

        bool opened = output.TryGetOrCreateUnderlyingStream(out Stream? stream, out string failure);

        await Assert.That(opened).IsFalse();
        await Assert.That(stream).IsNull();
        await Assert.That(failure).IsEqualTo("share offline");
    }

    [Test]
    public async Task TheStreamRequestReturnsTheSameStreamEachTime()
    {
        MemoryExportTarget target = new();
        using ExportOutput output = ExportOutput.FromTarget(target);

        output.TryGetOrCreateUnderlyingStream(out Stream? first, out _);
        output.TryGetOrCreateUnderlyingStream(out Stream? second, out _);

        await Assert.That(first).IsNotNull();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(target.EntryNames.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeClosesAnOwnedEntryAndLeavesACallerStreamOpen()
    {
        MemoryExportTarget target = new();
        using MemoryStream caller = new();

        using (ExportOutput owned = ExportOutput.FromTarget(target))
        {
            owned.Write([1]);
        }

        using (ExportOutput borrowed = ExportOutput.FromStream(caller))
        {
            borrowed.Write([2]);
        }

        caller.WriteByte(3);
        await Assert.That(target.IsClosed(string.Empty)).IsTrue();
        await Assert.That(caller.ToArray()).IsEquivalentTo(new byte[] { 2, 3 });
    }

    [Test]
    public async Task TheFactoriesRejectNullAndAStreamThatCannotBeWritten()
    {
        using MemoryStream readOnly = new([1], writable: false);

        await Assert.That(static () => ExportOutput.FromTarget(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => ExportOutput.FromStream(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => ExportOutput.FromStream(readOnly)).Throws<ArgumentException>();
        await Assert.That(static () => ExportOutput.File(string.Empty)).Throws<ArgumentException>();
    }
}
