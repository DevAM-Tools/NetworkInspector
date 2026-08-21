// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Unit tests for <see cref="SplitOutputManager"/>.
/// <para>
/// <see cref="SplitOutputManager.NeedsSplit"/> uses <c>&gt;=</c> comparisons so the split
/// boundary is inclusive: an output exactly at the size/count limit must trigger a new output.
/// Size checks use live <see cref="IExportByteProgress.EstimatedOutputBytes"/> values — never
/// filesystem probes.
/// </para>
/// <para>
/// <see cref="SplitOutputManager.NextPath"/> increments an internal counter and embeds
/// a zero-padded 5-digit index in the file name; when splitting is disabled it always
/// returns the original path unchanged.
/// </para>
/// <para>
/// <see cref="SplitOutputManager"/> is documented as not thread-safe (single-threaded contract);
/// no concurrent-access tests are included.
/// </para>
/// </summary>
internal sealed class SplitOutputManagerTests
{
    // === IsSplitting ===

    [Test]
    public async Task IsSplitting_BothZero_ReturnsFalse()
    {
        SplitOutputManager manager = new("output.pcapng", maxSize: 0, maxCount: 0);

        await Assert.That(manager.IsSplitting).IsFalse().Because("no limit is set");
    }

    [Test]
    public async Task IsSplitting_MaxSizeNonZero_ReturnsTrue()
    {
        SplitOutputManager manager = new("output.pcapng", maxSize: 100, maxCount: 0);

        await Assert.That(manager.IsSplitting).IsTrue();
        await Assert.That(manager.IsSizeSplitting).IsTrue();
    }

    [Test]
    public async Task IsSplitting_MaxCountNonZero_ReturnsTrue()
    {
        SplitOutputManager manager = new("output.pcapng", maxSize: 0, maxCount: 50);

        await Assert.That(manager.IsSplitting).IsTrue();
        await Assert.That(manager.IsSizeSplitting).IsFalse();
    }

    // === NeedsSplit — no limits ===

    [Test]
    public async Task NeedsSplit_NoLimits_AlwaysFalse()
    {
        SplitOutputManager manager = new("output.pcapng", maxSize: 0, maxCount: 0);

        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: long.MaxValue, itemCount: int.MaxValue)).IsFalse();
    }

    // === NeedsSplit — size boundary (>=) ===

    [Test]
    [Arguments(100L, 100L, true)]   // exactly at limit
    [Arguments(101L, 100L, true)]   // one above limit
    [Arguments(99L, 100L, false)]   // one below limit
    [Arguments(0L, 100L, false)]    // zero size
    public async Task NeedsSplit_SizeBoundary_CorrectResult(long estimatedBytes, long maxSize, bool expected)
    {
        SplitOutputManager manager = new("output.pcapng", maxSize, maxCount: 0);

        await Assert.That(manager.NeedsSplit(estimatedBytes, itemCount: 0)).IsEqualTo(expected);
    }

    // === NeedsSplit — count boundary (>=) ===

    [Test]
    [Arguments(10, 10, true)]    // exactly at limit
    [Arguments(11, 10, true)]    // one above limit
    [Arguments(9, 10, false)]    // one below limit
    [Arguments(0, 10, false)]    // zero count
    public async Task NeedsSplit_CountBoundary_CorrectResult(int frameCount, int maxCount, bool expected)
    {
        SplitOutputManager manager = new("output.pcapng", maxSize: 0, maxCount);

        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: 0, itemCount: frameCount)).IsEqualTo(expected);
    }

    // === NeedsSplit — size wins when both limits are set ===

    [Test]
    public async Task NeedsSplit_SizeExceedsMaxCountBelow_ReturnsTrue()
    {
        SplitOutputManager manager = new("out.pcapng", maxSize: 100, maxCount: 50);

        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: 100, itemCount: 1)).IsTrue();
    }

    // === NeedsSplit — count wins when size is below ===

    [Test]
    public async Task NeedsSplit_CountExceedsMaxSizeBelow_ReturnsTrue()
    {
        SplitOutputManager manager = new("out.pcapng", maxSize: 1000, maxCount: 50);

        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: 1, itemCount: 50)).IsTrue();
    }

    // === NextPath — no splitting: always returns original path ===

    [Test]
    public async Task NextPath_NoSplitting_AlwaysReturnsSamePath()
    {
        SplitOutputManager manager = new("capture.pcapng", maxSize: 0, maxCount: 0);

        string first = manager.NextPath();
        string second = manager.NextPath();
        string third = manager.NextPath();

        await Assert.That(first).IsEqualTo("capture.pcapng");
        await Assert.That(second).IsEqualTo("capture.pcapng");
        await Assert.That(third).IsEqualTo("capture.pcapng");
    }

    // === NextPath — split mode: sequential 5-digit index ===

    [Test]
    public async Task NextPath_SplitMode_ReturnsSequentiallyNumberedPaths()
    {
        SplitOutputManager manager = new("capture.pcapng", maxSize: 0, maxCount: 1);

        string first = manager.NextPath();
        string second = manager.NextPath();
        string third = manager.NextPath();

        await Assert.That(first).IsEqualTo("capture_00001.pcapng");
        await Assert.That(second).IsEqualTo("capture_00002.pcapng");
        await Assert.That(third).IsEqualTo("capture_00003.pcapng");
    }

    [Test]
    public async Task NextPath_SplitMode_PreservesExtension()
    {
        SplitOutputManager manager = new("/tmp/output.blf", maxSize: 500, maxCount: 0);

        string path = manager.NextPath();

        await Assert.That(path).IsEqualTo("/tmp/output_00001.blf");
    }

    [Test]
    public async Task NextPath_SplitMode_PathWithNoExtension_IndexIsAppended()
    {
        SplitOutputManager manager = new("capture", maxSize: 100, maxCount: 0);

        string path = manager.NextPath();

        await Assert.That(path).IsEqualTo("capture_00001");
    }

    [Test]
    public async Task NextPath_SplitMode_StartsAtOne_NotZero()
    {
        SplitOutputManager manager = new("out.pcapng", maxSize: 1, maxCount: 0);

        string first = manager.NextPath();

        await Assert.That(first).StartsWith("out_00001").Because("index must start at 1");
    }

    // === Directory-oriented outputs (Parquet) ===

    [Test]
    public async Task NextPath_DirectoryMode_NoSplitting_ReturnsBasePathUnchanged()
    {
        SplitOutputManager manager = new("out_parquet", maxSize: 0, maxCount: 0, isDirectoryOutput: true);

        await Assert.That(manager.IsDirectoryOutput).IsTrue();
        await Assert.That(manager.NextPath()).IsEqualTo("out_parquet");
        await Assert.That(manager.NextPath()).IsEqualTo("out_parquet");
    }

    [Test]
    public async Task NextPath_DirectoryMode_Split_ReturnsNumberedSiblingDirectories()
    {
        SplitOutputManager manager = new("out_parquet", maxSize: 0, maxCount: 1, isDirectoryOutput: true);

        await Assert.That(manager.NextPath()).IsEqualTo("out_parquet_00001");
        await Assert.That(manager.NextPath()).IsEqualTo("out_parquet_00002");
    }

    [Test]
    public async Task NextPath_DirectoryMode_KeepsDotInBaseName()
    {
        SplitOutputManager manager = new("dataset.parquet", maxSize: 0, maxCount: 1, isDirectoryOutput: true);

        await Assert.That(manager.NextPath()).IsEqualTo("dataset.parquet_00001");
    }

    [Test]
    public async Task NeedsSplit_DirectoryMode_UsesEstimatedBytesNotFilesystem()
    {
        SplitOutputManager manager = new("out_parquet", maxSize: 300, maxCount: 0, isDirectoryOutput: true);

        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: 299, itemCount: 1)).IsFalse();
        await Assert.That(manager.NeedsSplit(estimatedOutputBytes: 300, itemCount: 1)).IsTrue();
    }
}
