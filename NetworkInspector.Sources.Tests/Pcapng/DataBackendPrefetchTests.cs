// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// The block prefetch is a hint that must stay inside the file: <see cref="DataBackend.PrefetchLength"/> bounds it by the
/// end bound, the file end, and <see cref="CachePrefetch.MaxBytes"/>, and <see cref="DataBackend.PrefetchBlock"/> never
/// throws or touches memory outside the data (a touch load past a mapped view is an access violation on ARM64).
/// </summary>
internal sealed class DataBackendPrefetchTests
{
    #region Constants

    private const int _FileSize = 10_000;

    #endregion

    #region PrefetchLength

    [Test]
    [Arguments(0L, long.MaxValue, 4_096L)]
    [Arguments(0L, 100L, 100L)]
    [Arguments(0L, 4_096L, 4_096L)]
    [Arguments(0L, 5_000L, 4_096L)]
    [Arguments(9_999L, long.MaxValue, 1L)]
    [Arguments(9_000L, long.MaxValue, 1_000L)]
    [Arguments(9_000L, 9_500L, 500L)]
    [Arguments(100L, 100L, 0L)]
    [Arguments(100L, 50L, 0L)]
    [Arguments(10_000L, long.MaxValue, 0L)]
    [Arguments(-1L, long.MaxValue, 0L)]
    [Arguments(long.MaxValue, long.MaxValue, 0L)]
    [Arguments(0L, long.MinValue, 0L)]
    public async Task PrefetchLengthIsTheSmallestOfBoundFileEndAndCap(long offset, long bound, long expected)
    {
        long length = DataBackend.PrefetchLength(offset, bound, _FileSize);

        await Assert.That(length).IsEqualTo(expected);
    }

    [Test]
    public async Task PrefetchLengthOfAnEmptyFileIsZero()
    {
        await Assert.That(DataBackend.PrefetchLength(0, long.MaxValue, 0)).IsEqualTo(0L);
    }

    #endregion

    #region PrefetchBlock

    [Test]
    public async Task PrefetchBlockNeverThrowsOnInMemoryOrMappedData()
    {
        byte[] data = new byte[_FileSize];
        new System.Random(7).NextBytes(data);
        using TempCaptureFile file = new(data, ".bin");
        using DataBackend memory = DataBackend.FromMemory(data);
        using DataBackend mapped = DataBackend.FromMmap(file.Path);
        long[] offsets = [0, 1, 63, 64, 9_999, 10_000, 10_001, -1, long.MinValue, long.MaxValue];
        long[] bounds = [long.MinValue, -1, 0, 1, 64, 4_096, 9_999, 10_000, long.MaxValue];
        List<string> failures = [];

        foreach (DataBackend backend in new[] { memory, mapped })
        {
            foreach (long offset in offsets)
            {
                foreach (long bound in bounds)
                {
                    try
                    {
                        backend.PrefetchBlock(offset, bound);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(Invariant($"{(backend.IsInMemory ? "memory" : "mapped")} offset {offset} bound {bound}: {exception.GetType().Name}"));
                    }
                }
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task PrefetchBlockLeavesTheDataUnchanged()
    {
        byte[] data = new byte[_FileSize];
        new System.Random(11).NextBytes(data);
        byte[] copy = [.. data];
        using DataBackend memory = DataBackend.FromMemory(data);

        memory.PrefetchBlock(0, long.MaxValue);
        memory.PrefetchBlock(9_000, long.MaxValue);

        await Assert.That(data.AsSpan().SequenceEqual(copy)).IsTrue();
    }

    #endregion

    #region CachePrefetch

    [Test]
    public async Task RangeOfLengthZeroDoesNothing()
    {
        byte[] data = [1, 2, 3, 4];

        unsafe
        {
            fixed (byte* start = data)
            {
                CachePrefetch.Range(start, 0);
            }
        }

        await Assert.That(data).IsEquivalentTo(new byte[] { 1, 2, 3, 4 }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RangeOfTheFullCapWithinAnArrayIsAccepted()
    {
        // The cap is 64 lines; a buffer of exactly the cap is read line by line without leaving it.
        byte[] data = new byte[CachePrefetch.MaxBytes];

        unsafe
        {
            fixed (byte* start = data)
            {
                CachePrefetch.Range(start, CachePrefetch.MaxBytes);
            }
        }

        await Assert.That(data.Length).IsEqualTo(CachePrefetch.MaxBytes);
    }

    #endregion
}
