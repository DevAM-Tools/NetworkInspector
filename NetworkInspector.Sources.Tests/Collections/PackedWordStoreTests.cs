// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Collections;

/// <summary>Reservation rules of <see cref="PackedWordStore"/>.</summary>
internal sealed class PackedWordStoreTests
{
    #region Tests

    [Test]
    public async Task ReserveRejectsZeroAndMoreThanOneChunk()
    {
        PackedWordStore store = new();

        await Assert.That(() => store.Reserve(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => store.Reserve(PackedWordStore.ChunkWords + 1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReservationThatDoesNotFitStartsAtTheNextChunk()
    {
        // Arrange: leave 3 words free in the first chunk.
        PackedWordStore store = new();
        long first = store.Reserve(PackedWordStore.ChunkWords - 3);

        // Act
        long second = store.Reserve(4);
        long third = store.Reserve(2);

        // Assert: the 4-word reservation skips the tail; the next one continues behind it.
        await Assert.That(first).IsEqualTo(0L);
        await Assert.That(second).IsEqualTo((long)PackedWordStore.ChunkWords);
        await Assert.That(third).IsEqualTo((long)PackedWordStore.ChunkWords + 4);
        await Assert.That(store.UsedWords).IsEqualTo((long)PackedWordStore.ChunkWords + 6);
    }

    [Test]
    public async Task WrittenWordsReadBack()
    {
        PackedWordStore store = new();
        long start = store.Reserve(3);
        Span<ulong> words = store.GetWritable(start, 3);
        words[0] = 1;
        words[1] = ulong.MaxValue;
        words[2] = 42;

        ulong[] read = store.Get(start, 3).ToArray();

        await Assert.That(read).IsEquivalentTo(new ulong[] { 1, ulong.MaxValue, 42 });
    }

    #endregion
}
