// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>Position → container row resolution of <see cref="BlfFrameIndex"/>, including a reader's row cursor.</summary>
internal sealed class BlfFrameIndexTests
{
    [Test]
    public async Task CursorMovesToARowThatWasAddedAfterItWasSet()
    {
        // Arrange: row 0 with two objects; the cursor resolves frame 0 while row 0 is the newest row.
        BlfFrameIndex index = new();
        int row0 = index.AddContainer(fileOffset: 1_000, decompressedLength: 96);
        index.PushContainerObject(row0, 0);
        index.PushContainerObject(row0, 48);
        BlfRowCursor cursor = BlfRowCursor.Empty;
        bool first = index.TryGetLocation(0, ref cursor, out BlfFrameLocation location0);

        // Act: the scan adds row 1 and its object; the same cursor resolves the next frames.
        int row1 = index.AddContainer(fileOffset: 2_000, decompressedLength: 48);
        index.PushContainerObject(row1, 0);
        bool second = index.TryGetLocation(1, ref cursor, out BlfFrameLocation location1);
        bool third = index.TryGetLocation(2, ref cursor, out BlfFrameLocation location2);

        // Assert
        await Assert.That(first && location0 == new BlfFrameLocation(1_000, 0, -1)).IsTrue();
        await Assert.That(second && location1 == new BlfFrameLocation(1_000, 48, -1)).IsTrue();
        await Assert.That(third && location2 == new BlfFrameLocation(2_000, 0, -1)).IsTrue();
    }

    [Test]
    public async Task EmptyContainerRowsResolveToTheRowThatHoldsTheObject()
    {
        BlfFrameIndex index = new();
        int row0 = index.AddContainer(10, 48);
        index.PushContainerObject(row0, 0);
        _ = index.AddContainer(20, 0);
        int row2 = index.AddContainer(30, 48);
        index.PushContainerObject(row2, 0);
        index.Complete();

        BlfRowCursor cursor = BlfRowCursor.Empty;
        bool a = index.TryGetLocation(1, ref cursor, out BlfFrameLocation byCursor);
        bool b = index.TryGetLocation(1, out BlfFrameLocation bySearch);

        await Assert.That(a && byCursor == new BlfFrameLocation(30, 0, -1)).IsTrue();
        await Assert.That(b && bySearch == byCursor).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RowLookupMatchesALinearScanAcrossEmptyContainersAndGroupEdges(bool complete)
    {
        // Arrange: raw object, three empty containers, containers with 1, 64, and 65 objects (a spanning object at frame 63,
        // an empty container between the last two), and trailing raw objects.
        BlfFrameIndex index = new();
        List<BlfFrameLocation> expected = [];
        _AddRaw(index, expected, 144);
        for (int i = 0; i < 3; i++)
        {
            _ = index.AddContainer(100 + i, 0);
        }

        _AddContainer(index, expected, fileOffset: 1_000, objects: 1, spanningAt: -1);
        _AddContainer(index, expected, fileOffset: 2_000, objects: 64, spanningAt: 61);
        _ = index.AddContainer(2_500, 0);
        _AddContainer(index, expected, fileOffset: 3_000, objects: 65, spanningAt: -1);
        _AddRaw(index, expected, 4_096);
        _AddRaw(index, expected, 4_200);
        if (complete)
        {
            index.Complete();
        }

        // Act / Assert
        List<string> mismatches = _FindLocationMismatches(index, expected);

        await Assert.That(expected[63].ObjectLength).IsEqualTo(48);
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RowLookupMatchesALinearScanOverManyRows(bool complete)
    {
        // Arrange: 200 rows (more than three groups of 64); every seventh is empty, some objects span.
        BlfFrameIndex index = new();
        List<BlfFrameLocation> expected = [];
        for (int row = 0; row < 200; row++)
        {
            if (row % 7 == 3)
            {
                _ = index.AddContainer(10_000 + row, 0);
                continue;
            }

            _AddContainer(index, expected, fileOffset: 10_000 + row, objects: 3, spanningAt: row % 11 == 0 ? 1 : -1);
        }

        if (complete)
        {
            index.Complete();
        }

        // Act / Assert
        List<string> mismatches = _FindLocationMismatches(index, expected);

        await Assert.That(expected.Count).IsGreaterThan(500);
        await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task RawAndSpanningObjectsResolveWithTheirFileOffsetAndLength()
    {
        BlfFrameIndex index = new();
        index.PushRawObject(144);
        int row = index.AddContainer(500, 40);
        index.PushSpanningObject(row, 20, objectLength: 48);
        index.Complete();

        bool raw = index.TryGetLocation(0, out BlfFrameLocation rawLocation);
        bool spanning = index.TryGetLocation(1, out BlfFrameLocation spanningLocation);
        bool missing = index.TryGetLocation(2, out _);

        await Assert.That(raw && rawLocation == new BlfFrameLocation(144, -1, -1)).IsTrue();
        await Assert.That(spanning && spanningLocation == new BlfFrameLocation(500, 20, 48)).IsTrue();
        await Assert.That(missing).IsFalse();
    }

    #region Helpers

    private const int _ObjectSize = 48;

    /// <summary>Adds a container of <paramref name="objects"/> 48-byte objects; the object at <paramref name="spanningAt"/> is a spanning object.</summary>
    private static void _AddContainer(BlfFrameIndex index, List<BlfFrameLocation> expected, long fileOffset, int objects, int spanningAt)
    {
        int row = index.AddContainer(fileOffset, objects * _ObjectSize);
        for (int i = 0; i < objects; i++)
        {
            int inner = i * _ObjectSize;
            if (i == spanningAt)
            {
                index.PushSpanningObject(row, inner, _ObjectSize);
                expected.Add(new BlfFrameLocation(fileOffset, inner, _ObjectSize));
            }
            else
            {
                index.PushContainerObject(row, inner);
                expected.Add(new BlfFrameLocation(fileOffset, inner, -1));
            }
        }
    }

    private static void _AddRaw(BlfFrameIndex index, List<BlfFrameLocation> expected, long fileOffset)
    {
        index.PushRawObject(fileOffset);
        expected.Add(new BlfFrameLocation(fileOffset, -1, -1));
    }

    /// <summary>
    /// Compares the lookup with and without a cursor to the expected locations, in ascending, descending, and random
    /// order; out-of-range ids must fail. Returns the differences.
    /// </summary>
    private static List<string> _FindLocationMismatches(BlfFrameIndex index, List<BlfFrameLocation> expected)
    {
        List<string> mismatches = [];
        System.Random random = new(expected.Count);
        BlfRowCursor cursor = BlfRowCursor.Empty;
        for (int walk = 0; walk < 3; walk++)
        {
            cursor = BlfRowCursor.Empty;
            for (int n = 0; n < expected.Count; n++)
            {
                int id = walk switch
                {
                    0 => n,
                    1 => expected.Count - 1 - n,
                    _ => random.Next(expected.Count),
                };
                bool plain = index.TryGetLocation(id, out BlfFrameLocation without);
                bool withCursor = index.TryGetLocation(id, ref cursor, out BlfFrameLocation with);
                if (!plain || !withCursor || without != expected[id] || with != expected[id])
                {
                    mismatches.Add(Invariant($"frame {id}: expected {expected[id]}, got {without} / {with}"));
                }
            }
        }

        foreach (int outside in new[] { -1, expected.Count, int.MaxValue })
        {
            if (index.TryGetLocation(outside, out _) || index.TryGetLocation(outside, ref cursor, out _))
            {
                mismatches.Add(Invariant($"frame {outside} out of range was accepted"));
            }
        }

        return mismatches;
    }

    #endregion
}
