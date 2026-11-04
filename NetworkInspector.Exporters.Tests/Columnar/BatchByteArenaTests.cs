// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>Tests for <see cref="BatchByteArena"/>: the pooled byte store behind frame bytes and bytes values.</summary>
internal sealed class BatchByteArenaTests
{
    // One regular chunk is 1 MiB; the tests use payloads relative to it.
    private const int _ChunkSize = 1 << 20;

    /// <summary>A payload reads back equal to what was stored, also when the source changes afterwards.</summary>
    [Test]
    public async Task AppendedPayloadsReadBackEqual()
    {
        using BatchByteArena arena = new();
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5];

        ArenaRef firstRef = arena.Append(first);
        ArenaRef secondRef = arena.Append(second);
        first[0] = 99;

        await Assert.That(_Read(arena, firstRef)).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(_Read(arena, secondRef)).IsEquivalentTo(new byte[] { 4, 5 });
    }

    /// <summary>The memory view reads back the stored bytes, wraps into an array-backed value without a copy, and is empty for no payload.</summary>
    [Test]
    public async Task MemoryViewReadsBackTheStoredBytesWithoutACopy()
    {
        using BatchByteArena arena = new();
        ArenaRef reference = arena.Append(new byte[] { 1, 2, 3 });

        ReadOnlyMemory<byte> memory = arena.GetMemory(reference);
        FieldValueData wrapped = FieldValueData.NewBytes(memory);

        await Assert.That(memory.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(wrapped.TryGetAsBytes(out ReadOnlyMemory<byte> viaValue)).IsTrue();
        await Assert.That(viaValue.Span.SequenceEqual(memory.Span)).IsTrue();
        await Assert.That(arena.GetMemory(arena.Append(ReadOnlySpan<byte>.Empty)).Length).IsEqualTo(0);
    }

    /// <summary>An empty payload needs no chunk and reads back empty (collection size 0).</summary>
    [Test]
    public async Task EmptyPayloadReadsBackEmpty()
    {
        using BatchByteArena arena = new();

        ArenaRef reference = arena.Append(ReadOnlySpan<byte>.Empty);

        await Assert.That(reference.Length).IsEqualTo(0);
        await Assert.That(_Read(arena, reference).Length).IsEqualTo(0);
        await Assert.That(arena.Mark()).IsEqualTo(new ArenaMark(0, 0));
    }

    /// <summary>A payload that does not fit the rest of a chunk starts a new chunk, and both stay readable.</summary>
    [Test]
    public async Task FullChunkRollsOverToANewChunk()
    {
        using BatchByteArena arena = new();
        byte[] large = new byte[(_ChunkSize / 2) + 1];
        Array.Fill(large, (byte)7);
        byte[] other = new byte[(_ChunkSize / 2) + 1];
        Array.Fill(other, (byte)9);

        ArenaRef firstRef = arena.Append(large);
        ArenaRef secondRef = arena.Append(other);

        await Assert.That(firstRef.Chunk).IsEqualTo(0);
        await Assert.That(secondRef.Chunk).IsEqualTo(1);
        await Assert.That(_Holds(arena, firstRef, large)).IsTrue();
        await Assert.That(_Holds(arena, secondRef, other)).IsTrue();
    }

    /// <summary>A payload larger than a chunk gets a chunk of its own, and later small payloads start a new chunk.</summary>
    [Test]
    public async Task OversizedPayloadGetsItsOwnChunk()
    {
        using BatchByteArena arena = new();
        byte[] oversized = new byte[_ChunkSize + 10];
        Array.Fill(oversized, (byte)5);

        ArenaRef bigRef = arena.Append(oversized);
        ArenaRef smallRef = arena.Append(new byte[] { 1, 2 });

        await Assert.That(_Holds(arena, bigRef, oversized)).IsTrue();
        await Assert.That(smallRef.Chunk).IsEqualTo(bigRef.Chunk + 1);
        await Assert.That(_Read(arena, smallRef)).IsEquivalentTo(new byte[] { 1, 2 });
    }

    /// <summary>Truncating to a mark drops later payloads (also whole chunks) and the space is reused.</summary>
    [Test]
    public async Task TruncateToDropsLaterPayloadsAndReusesTheSpace()
    {
        using BatchByteArena arena = new();
        ArenaRef kept = arena.Append(new byte[] { 1, 2, 3 });
        ArenaMark mark = arena.Mark();

        arena.Append(new byte[_ChunkSize]);
        arena.Append(new byte[10]);
        arena.TruncateTo(mark);
        ArenaRef reused = arena.Append(new byte[] { 8, 9 });

        await Assert.That(arena.Mark()).IsEqualTo(new ArenaMark(1, 5));
        await Assert.That(reused).IsEqualTo(new ArenaRef(0, 3, 2));
        await Assert.That(_Read(arena, kept)).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(_Read(arena, reused)).IsEquivalentTo(new byte[] { 8, 9 });
    }

    /// <summary>Reset forgets the payloads and the arena is used again from the start.</summary>
    [Test]
    public async Task ResetForgetsPayloadsAndAllowsReuse()
    {
        using BatchByteArena arena = new();
        arena.Append(new byte[_ChunkSize]);
        arena.Append(new byte[10]);

        arena.Reset();
        ArenaRef reference = arena.Append(new byte[] { 4, 5, 6 });

        await Assert.That(reference).IsEqualTo(new ArenaRef(0, 0, 3));
        await Assert.That(_Read(arena, reference)).IsEquivalentTo(new byte[] { 4, 5, 6 });
        await Assert.That(arena.Mark()).IsEqualTo(new ArenaMark(1, 3));
    }

    /// <summary>Dispose empties the arena, may be repeated, and the arena can be used again.</summary>
    [Test]
    public async Task DisposeEmptiesTheSlab()
    {
        BatchByteArena arena = new();
        arena.Append(new byte[] { 1 });

        arena.Dispose();
        arena.Dispose();

        await Assert.That(arena.Mark()).IsEqualTo(new ArenaMark(0, 0));
    }

    /// <summary>Returns a copy of the stored bytes (a span cannot live in an async test).</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="reference">Location of the payload.</param>
    /// <returns>The payload.</returns>
    private static byte[] _Read(BatchByteArena arena, ArenaRef reference) => arena.Get(reference).ToArray();

    /// <summary>
    /// Whether the stored bytes equal <paramref name="expected"/>. A span comparison keeps the test fast for megabyte
    /// payloads, which a collection assertion compares element by element through the assertion library.
    /// </summary>
    /// <param name="arena">The arena.</param>
    /// <param name="reference">Location of the payload.</param>
    /// <param name="expected">The expected bytes.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    private static bool _Holds(BatchByteArena arena, ArenaRef reference, byte[] expected) => arena.Get(reference).SequenceEqual(expected);
}
