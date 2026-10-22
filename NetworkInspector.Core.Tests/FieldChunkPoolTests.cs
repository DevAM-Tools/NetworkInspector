// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="FieldChunkPool"/>: rent from an empty pool, return then rent, last-in-first-out order, the
/// capacity bound, and constructor and argument validation.
/// <para><b>Thread safety:</b> every test owns its pool; nothing is shared.</para>
/// </summary>
internal sealed class FieldChunkPoolTests
{
    #region Helpers

    private static FieldBodyChunk _Chunk(int slots, int offset) => new() { Buffer = new FieldBody[slots], Offset = offset };

    /// <summary>The pool's backing array, read by reflection, to check that vacated entries no longer pin a buffer.</summary>
    private static FieldBodyChunk[] _Items(FieldChunkPool pool) =>
        (FieldBodyChunk[])typeof(FieldChunkPool).GetField("_Items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;

    #endregion

    #region Rent and return

    [Test]
    public async Task RentFromAnEmptyPoolReturnsFalse()
    {
        FieldChunkPool pool = new(FieldStorage.ChunkSize);

        bool rented = pool.TryRent(out FieldBodyChunk chunk);

        await Assert.That(rented).IsFalse();
        await Assert.That(chunk.Buffer).IsNull();
        await Assert.That(chunk.Offset).IsEqualTo(0);
        await Assert.That(pool.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReturnThenRentGivesBackTheSameChunk()
    {
        FieldChunkPool pool = new(FieldStorage.ChunkSize);
        FieldBodyChunk returned = _Chunk(64, 16);

        bool accepted = pool.TryReturn(in returned);
        int countAfterReturn = pool.Count;
        bool rented = pool.TryRent(out FieldBodyChunk chunk);

        await Assert.That(accepted).IsTrue();
        await Assert.That(countAfterReturn).IsEqualTo(1);
        await Assert.That(rented).IsTrue();
        await Assert.That(ReferenceEquals(chunk.Buffer, returned.Buffer)).IsTrue();
        await Assert.That(chunk.Offset).IsEqualTo(16);
        await Assert.That(pool.Count).IsEqualTo(0);
        await Assert.That(_Items(pool)[0].Buffer).IsNull();
    }

    [Test]
    public async Task RentIsLastInFirstOut()
    {
        FieldChunkPool pool = new(FieldStorage.ChunkSize);
        FieldBodyChunk first = _Chunk(64, 0);
        FieldBodyChunk second = _Chunk(64, 32);
        _ = pool.TryReturn(in first);
        _ = pool.TryReturn(in second);

        _ = pool.TryRent(out FieldBodyChunk rentedFirst);
        _ = pool.TryRent(out FieldBodyChunk rentedSecond);

        await Assert.That(ReferenceEquals(rentedFirst.Buffer, second.Buffer)).IsTrue();
        await Assert.That(ReferenceEquals(rentedSecond.Buffer, first.Buffer)).IsTrue();
        await Assert.That(pool.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReturnToAFullPoolReturnsFalse()
    {
        FieldChunkPool pool = new(FieldStorage.ChunkSize);
        FieldBodyChunk chunk = _Chunk(FieldStorage.ChunkSize, 0);
        for (int i = 0; i < pool.Capacity; i++)
        {
            _ = pool.TryReturn(in chunk);
        }

        bool accepted = pool.TryReturn(in chunk);

        await Assert.That(pool.Capacity).IsEqualTo(FieldChunkPool.MaxPooledSlots / FieldStorage.ChunkSize);
        await Assert.That(accepted).IsFalse();
        await Assert.That(pool.Count).IsEqualTo(pool.Capacity);
    }

    #endregion

    #region Validation

    [Test]
    [Arguments(0)]
    [Arguments(FieldChunkPool.MaxPooledSlots + 1)]
    public async Task ConstructorRejectsChunkSizesOutsideTheRange(int chunkSize)
    {
        await Assert.That(() => new FieldChunkPool(chunkSize)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ReturnWithoutABufferThrows()
    {
        FieldChunkPool pool = new(FieldStorage.ChunkSize);
        FieldBodyChunk empty = default;

        await Assert.That(() => pool.TryReturn(in empty)).Throws<ArgumentNullException>();
        await Assert.That(pool.Count).IsEqualTo(0);
    }

    #endregion
}
