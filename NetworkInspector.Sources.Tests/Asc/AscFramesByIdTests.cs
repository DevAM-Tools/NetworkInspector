// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Asc;

/// <summary>
/// Sources without an override (ASC) answer a batch through the default implementation of
/// <see cref="IRandomAccessFrameSource.FramesById"/>: every entry equals the single read, in every id order, with
/// duplicates and invalid ids.
/// </summary>
internal sealed class AscFramesByIdTests
{
    #region Tests

    [Test]
    [MethodDataSource(typeof(AscRandomAccessEquivalenceTests), nameof(AscRandomAccessEquivalenceTests.Corpus))]
    public async Task BatchEqualsPerIdReadsForBothBackends(string name, string text)
    {
        List<string> mismatches = [];

        // In-memory backend.
        using (AscSource memory = AscSource.FromText(text, name))
        {
            List<Frame> frames = RandomAccessEquivalence.StartAndReadAll(memory);
            _CollectMismatches(memory, frames.Count, $"{name}/memory", mismatches);
        }

        // Disk backend.
        using TempCaptureFile file = new(Encoding.Latin1.GetBytes(text), ".asc");
        using AscSource disk = AscSource.Open(file.Path, new AscSourceOptions { PreloadBudget = null });
        List<Frame> diskFrames = RandomAccessEquivalence.StartAndReadAll(disk);
        _CollectMismatches(disk, diskFrames.Count, $"{name}/disk", mismatches);

        await Assert.That(mismatches).IsEmpty();
    }

    #endregion

    #region Helpers

    private static void _CollectMismatches(IRandomAccessFrameSource source, int frameCount, string label, List<string> mismatches)
    {
        foreach ((string name, FrameId first, int length) in RandomAccessEquivalence.BatchRanges(frameCount))
        {
            string? mismatch = RandomAccessEquivalence.FindBatchMismatch(source, first, length, $"{label}/{name}");
            if (mismatch is not null)
            {
                mismatches.Add(mismatch);
            }
        }
    }

    #endregion
}
