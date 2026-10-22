// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// <see cref="RandomAccessSourceTable"/> answers exactly like the dictionary it replaces: lookup, replace, remove, and
/// copy-on-write (an earlier table instance never changes).
/// </summary>
internal sealed class RandomAccessSourceTableTests
{
    #region Tests

    [Test]
    public async Task EmptyTableFindsNothing()
    {
        bool found = RandomAccessSourceTable.Empty.TryGet(new FrameSourceId(0), out IRandomAccessFrameSource? source);

        await Assert.That(found).IsFalse();
        await Assert.That(source).IsNull();
        await Assert.That(RandomAccessSourceTable.Empty.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(20)]
    public async Task WithInRandomIdOrderFindsEverySourceAndKeepsEarlierTablesUnchanged(int count)
    {
        // Arrange: ids in random order, including id 0 and the largest valid id.
        System.Random random = new(count);
        List<int> ids = [0, ArrayIndexIdRange.MaxValue];
        while (ids.Count < count + 1)
        {
            int candidate = random.Next(1, 10_000);
            if (!ids.Contains(candidate))
            {
                ids.Add(candidate);
            }
        }

        ids = [.. ids.Take(Math.Max(count, 1)).OrderBy(_ => random.Next())];
        Dictionary<int, StubSource> expected = [];
        RandomAccessSourceTable table = RandomAccessSourceTable.Empty;
        List<(RandomAccessSourceTable Table, int Count)> history = [];

        // Act
        foreach (int id in ids)
        {
            StubSource source = new(id);
            expected[id] = source;
            table = table.With(new FrameSourceId(id), source);
            history.Add((table, expected.Count));
        }

        // Assert: every source is found, absent ids are not, and every earlier table still has its old size.
        List<string> problems = [];
        foreach ((int id, StubSource source) in expected)
        {
            if (!table.TryGet(new FrameSourceId(id), out IRandomAccessFrameSource? found) || !ReferenceEquals(found, source))
            {
                problems.Add(FormattableString.Invariant($"id {id} not found"));
            }
        }

        if (table.TryGet(new FrameSourceId(10_001), out _))
        {
            problems.Add("absent id found");
        }

        for (int i = 0; i < history.Count; i++)
        {
            if (history[i].Table.Count != history[i].Count)
            {
                problems.Add(FormattableString.Invariant($"table {i} changed to {history[i].Table.Count}"));
            }
        }

        await Assert.That(problems).IsEmpty();
        await Assert.That(table.Count).IsEqualTo(expected.Count);
    }

    [Test]
    public async Task WithOnAnExistingIdReplacesTheSourceInANewTable()
    {
        StubSource first = new(1);
        StubSource second = new(2);
        RandomAccessSourceTable before = RandomAccessSourceTable.Empty.With(new FrameSourceId(5), first);

        RandomAccessSourceTable after = before.With(new FrameSourceId(5), second);

        await Assert.That(after.Count).IsEqualTo(1);
        await Assert.That(after.TryGet(new FrameSourceId(5), out IRandomAccessFrameSource? now) && ReferenceEquals(now, second)).IsTrue();
        await Assert.That(before.TryGet(new FrameSourceId(5), out IRandomAccessFrameSource? old) && ReferenceEquals(old, first)).IsTrue();
    }

    [Test]
    public async Task WithoutOnAMissingIdReturnsTheSameInstance()
    {
        RandomAccessSourceTable table = RandomAccessSourceTable.Empty.With(new FrameSourceId(3), new StubSource(3));

        RandomAccessSourceTable same = table.Without(new FrameSourceId(4));

        await Assert.That(ReferenceEquals(same, table)).IsTrue();
    }

    [Test]
    public async Task WithoutRemovesOnlyThatId()
    {
        RandomAccessSourceTable table = RandomAccessSourceTable.Empty;
        foreach (int id in new[] { 7, 2, 9, 4 })
        {
            table = table.With(new FrameSourceId(id), new StubSource(id));
        }

        List<string> problems = [];
        foreach (int removed in new[] { 7, 2, 9, 4 })
        {
            RandomAccessSourceTable smaller = table.Without(new FrameSourceId(removed));
            foreach (int id in new[] { 7, 2, 9, 4 })
            {
                bool expectedPresent = id != removed;
                if (smaller.TryGet(new FrameSourceId(id), out _) != expectedPresent || !table.TryGet(new FrameSourceId(id), out _))
                {
                    problems.Add(FormattableString.Invariant($"removing {removed}, id {id}"));
                }
            }

            if (smaller.Count != 3 || ReferenceEquals(smaller, table))
            {
                problems.Add(FormattableString.Invariant($"removing {removed}: count {smaller.Count}"));
            }
        }

        await Assert.That(problems).IsEmpty();
        await Assert.That(table.Count).IsEqualTo(4);
    }

    [Test]
    public async Task WithRejectsANullSource()
    {
        await Assert.That(() => RandomAccessSourceTable.Empty.With(new FrameSourceId(1), null!)).Throws<ArgumentNullException>();
    }

    #endregion

    #region Nested types

    /// <summary>A source that is only identified by reference.</summary>
    private sealed class StubSource(int tag) : IRandomAccessFrameSource
    {
        public int Tag { get; } = tag;

        public string UiName => "stub";

        public string? Description => null;

        public int? EstimatedFrameCount => null;

        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default) => null;

        public void Dispose()
        {
        }
    }

    #endregion
}
