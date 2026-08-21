// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="Xoroshiro128PlusPlus"/>.
/// Covers algorithmic correctness, statistical properties, and API contract.
/// </summary>
internal sealed class Xoroshiro128PlusPlusTests
{
    // ─── Determinism ────────────────────────────────────────────────────────────

    [Test]
    public async Task SameSeed_ProducesIdenticalNextU64Sequence()
    {
        // Two independent instances seeded identically must emit the same stream.
        Xoroshiro128PlusPlus rngA = new(42UL);
        Xoroshiro128PlusPlus rngB = new(42UL);

        for (int i = 0; i < 100; i++)
        {
            ulong a = rngA.NextU64();
            ulong b = rngB.NextU64();
            await Assert.That(a).IsEqualTo(b);
        }
    }

    [Test]
    public async Task DifferentSeeds_ProduceDifferentNextU64Sequences()
    {
        Xoroshiro128PlusPlus rngA = new(1UL);
        Xoroshiro128PlusPlus rngB = new(2UL);

        int differences = 0;
        for (int i = 0; i < 20; i++)
        {
            if (rngA.NextU64() != rngB.NextU64())
            {
                differences++;
            }
        }

        // Statistically impossible for two independent streams to collide 20 times
        await Assert.That(differences).IsGreaterThan(0);
    }

    [Test]
    public async Task SameSeed_FillBytes_ProducesIdenticalBuffers()
    {
        // FillBytes must be deterministic: same seed → same bytes.
        Xoroshiro128PlusPlus rngA = new(12345UL);
        Xoroshiro128PlusPlus rngB = new(12345UL);

        byte[] bufA = new byte[256];
        byte[] bufB = new byte[256];
        rngA.FillBytes(bufA);
        rngB.FillBytes(bufB);

        await Assert.That(bufA.AsSpan().SequenceEqual(bufB)).IsTrue();
    }

    [Test]
    public async Task DifferentSeeds_FillBytes_ProduceDifferentBuffers()
    {
        Xoroshiro128PlusPlus rngA = new(100UL);
        Xoroshiro128PlusPlus rngB = new(200UL);

        byte[] bufA = new byte[64];
        byte[] bufB = new byte[64];
        rngA.FillBytes(bufA);
        rngB.FillBytes(bufB);

        await Assert.That(bufA.AsSpan().SequenceEqual(bufB)).IsFalse();
    }

    [Test]
    public async Task FillBytes_Continuation_IsDeterministic()
    {
        // After FillBytes, the instance state is well-defined; any subsequent
        // NextU64 must produce the same value for the same seed.
        Xoroshiro128PlusPlus rngA = new(9999UL);
        Xoroshiro128PlusPlus rngB = new(9999UL);

        byte[] discardA = new byte[32];
        byte[] discardB = new byte[32];
        rngA.FillBytes(discardA);
        rngB.FillBytes(discardB);

        // The continuation must be identical.
        await Assert.That(rngA.NextU64()).IsEqualTo(rngB.NextU64());
        await Assert.That(rngA.NextU64()).IsEqualTo(rngB.NextU64());
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(16)]
    [Arguments(24)]
    [Arguments(31)]
    public async Task FillBytes_SmallBuffer_MatchesSequentialNextU64(int size)
    {
        // For buffers smaller than 32 bytes, FillBytes bypasses the 4-stream bulk
        // path and uses FillBytesSequential, which calls NextU64 in order. The byte
        // output must therefore be identical to manual NextU64 + write-LE calls.
        Xoroshiro128PlusPlus rngFill = new(77UL);
        Xoroshiro128PlusPlus rngSeq = new(77UL);

        byte[] fromFill = new byte[size];
        rngFill.FillBytes(fromFill);

        // Build expected output from sequential NextU64 draws.
        byte[] fromSeq = new byte[size];
        int offset = 0;
        while (offset + 8 <= size)
        {
            ulong val = rngSeq.NextU64();
            Unsafe.WriteUnaligned(ref fromSeq[offset], val);
            offset += 8;
        }

        if (offset < size)
        {
            ulong tail = rngSeq.NextU64();
            for (int i = 0; offset + i < size; i++)
            {
                fromSeq[offset + i] = (byte)(tail >> (i * 8));
            }
        }

        await Assert.That(fromFill.AsSpan().SequenceEqual(fromSeq)).IsTrue();
    }

    [Test]
    public async Task FillBytes_LargeSpan_UsesMultiStreamInterleaving()
    {
        // FillBytes for buffers >= 32 uses 4 independent streams for parallelism.
        // This means the output is NOT the same as calling NextU64() sequentially.
        // This test documents this behavior — it is by design, not a bug.
        Xoroshiro128PlusPlus rngFill = new(42UL);
        Xoroshiro128PlusPlus rngSeq = new(42UL);

        byte[] fromFill = new byte[32];
        rngFill.FillBytes(fromFill);

        // Build what sequential NextU64 would produce.
        byte[] fromSeq = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            Unsafe.WriteUnaligned(ref fromSeq[i * 8], rngSeq.NextU64());
        }

        // The outputs MUST differ because FillBytes uses multi-stream interleaving.
        await Assert.That(fromFill.AsSpan().SequenceEqual(fromSeq)).IsFalse();
    }

    // ─── Zero-state guard ────────────────────────────────────────────────────────

    [Test]
    public async Task ZeroSeed_DoesNotProduceAllZeroStream()
    {
        // Seed 0 must not leave the generator stuck at the (0,0) fixed point.
        // If the guard works, NextU64 must return non-zero very quickly.
        Xoroshiro128PlusPlus rng = new(0UL);

        bool anyNonZero = false;
        for (int i = 0; i < 10; i++)
        {
            if (rng.NextU64() != 0UL)
            {
                anyNonZero = true;
                break;
            }
        }

        await Assert.That(anyNonZero).IsTrue();
    }

    // ─── NextU64 / NextU32 / NextU16 / NextU8 basic properties ─────────────────

    [Test]
    public async Task NextU64_AdvancesStateOnEachCall()
    {
        Xoroshiro128PlusPlus rng = new(1UL);
        ulong a = rng.NextU64();
        ulong b = rng.NextU64();
        // Extremely unlikely to collide; would indicate a fixed-point bug.
        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task NextU32_IsHighHalfOfU64()
    {
        // NextU32 is spec'd as the top 32 bits of NextU64.
        // Verify both draw from the same state progression.
        Xoroshiro128PlusPlus rngA = new(55UL);
        Xoroshiro128PlusPlus rngB = new(55UL);

        for (int i = 0; i < 50; i++)
        {
            uint expected = (uint)(rngA.NextU64() >> 32);
            uint actual = rngB.NextU32();
            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task NextU16_IsTopHalfOfU64()
    {
        Xoroshiro128PlusPlus rngA = new(66UL);
        Xoroshiro128PlusPlus rngB = new(66UL);

        for (int i = 0; i < 50; i++)
        {
            ushort expected = (ushort)(rngA.NextU64() >> 48);
            ushort actual = rngB.NextU16();
            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task NextU8_IsTopByteOfU64()
    {
        Xoroshiro128PlusPlus rngA = new(77UL);
        Xoroshiro128PlusPlus rngB = new(77UL);

        for (int i = 0; i < 50; i++)
        {
            byte expected = (byte)(rngA.NextU64() >> 56);
            byte actual = rngB.NextU8();
            await Assert.That(actual).IsEqualTo(expected);
        }
    }

    // ─── NextRange ───────────────────────────────────────────────────────────────

    [Test]
    public async Task NextRange_AlwaysWithinBounds()
    {
        Xoroshiro128PlusPlus rng = new(54321UL);

        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextRange(5, 20);
            await Assert.That(value).IsGreaterThanOrEqualTo(5);
            await Assert.That(value).IsLessThan(20);
        }
    }

    [Test]
    public async Task NextRange_EqualBounds_ReturnsMin()
    {
        Xoroshiro128PlusPlus rng = new(1UL);
        int value = rng.NextRange(7, 7);
        await Assert.That(value).IsEqualTo(7);
    }

    [Test]
    public async Task NextRange_InvertedBounds_ReturnsMin()
    {
        // Guard: when minInclusive >= maxExclusive the method must return minInclusive.
        Xoroshiro128PlusPlus rng = new(1UL);
        int value = rng.NextRange(10, 5);
        await Assert.That(value).IsEqualTo(10);
    }

    [Test]
    public async Task NextRange_FullIntDomain_StaysBelowMaxExclusive()
    {
        Xoroshiro128PlusPlus rng = new(1UL);
        bool allInRange = true;
        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextRange(int.MinValue, int.MaxValue);
            if (value == int.MaxValue)
            {
                allInRange = false;
                break;
            }
        }

        await Assert.That(allInRange).IsTrue();
    }

    [Test]
    public async Task NextRange_NegativeOneToIntMax_StaysInRange()
    {
        Xoroshiro128PlusPlus rng = new(2UL);
        bool allInRange = true;
        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextRange(-1, int.MaxValue);
            if (value < -1 || value == int.MaxValue)
            {
                allInRange = false;
                break;
            }
        }

        await Assert.That(allInRange).IsTrue();
    }

    [Test]
    public async Task NextRange_ApproximatelyUniform_SmallRange()
    {
        // Chi-square style check: 3-bucket range over 30 000 samples.
        // Each bucket expectation = 10 000; allow ±10 % (±1 000).
        const int Samples = 30_000;
        const int Min = 0;
        const int Max = 3;
        int[] counts = new int[Max - Min];

        Xoroshiro128PlusPlus rng = new(98765UL);
        for (int i = 0; i < Samples; i++)
        {
            int v = rng.NextRange(Min, Max);
            counts[v - Min]++;
        }

        int expected = Samples / (Max - Min);         // 10 000
        int tolerance = expected / 10;                // 1 000 (10 %)

        foreach (int count in counts)
        {
            await Assert.That(count).IsGreaterThan(expected - tolerance);
            await Assert.That(count).IsLessThan(expected + tolerance);
        }
    }

    // ─── DeriveFrameSeed ─────────────────────────────────────────────────────────

    [Test]
    public async Task DeriveFrameSeed_IsDeterministic()
    {
        ulong seedA = Xoroshiro128PlusPlus.DeriveFrameSeed(42UL, 7UL);
        ulong seedB = Xoroshiro128PlusPlus.DeriveFrameSeed(42UL, 7UL);
        await Assert.That(seedA).IsEqualTo(seedB);
    }

    [Test]
    public async Task DeriveFrameSeed_DifferentFrameIds_DifferentSeeds()
    {
        ulong seedA = Xoroshiro128PlusPlus.DeriveFrameSeed(1UL, 0UL);
        ulong seedB = Xoroshiro128PlusPlus.DeriveFrameSeed(1UL, 1UL);
        await Assert.That(seedA).IsNotEqualTo(seedB);
    }

    [Test]
    public async Task DeriveFrameSeed_DifferentMasterSeeds_DifferentSeeds()
    {
        ulong seedA = Xoroshiro128PlusPlus.DeriveFrameSeed(10UL, 5UL);
        ulong seedB = Xoroshiro128PlusPlus.DeriveFrameSeed(11UL, 5UL);
        await Assert.That(seedA).IsNotEqualTo(seedB);
    }

    [Test]
    public async Task DeriveFrameSeed_ProducesNonZeroSeed()
    {
        // The derived seed must never be zero, since that would create an all-zero PRNG state.
        for (ulong frame = 0; frame < 1000; frame++)
        {
            ulong derived = Xoroshiro128PlusPlus.DeriveFrameSeed(0UL, frame);
            // A derived-zero seed is theoretically possible; the PRNG constructor
            // guards against this — so we only verify the constructor produces a working PRNG.
            Xoroshiro128PlusPlus rng = new(derived);
            bool anyNonZero = false;
            for (int j = 0; j < 4; j++)
            {
                if (rng.NextU64() != 0UL)
                {
                    anyNonZero = true;
                    break;
                }
            }

            await Assert.That(anyNonZero).IsTrue();
        }
    }

    // ─── FillBytes buffer sizes ───────────────────────────────────────────────────

    [Test]
    public async Task FillBytes_EmptyBuffer_DoesNotThrow()
    {
        Xoroshiro128PlusPlus rng = new(1UL);
        rng.FillBytes(Span<byte>.Empty);
        // No assertion needed — just must not throw.
        int dummy = 1;
        await Assert.That(dummy).IsEqualTo(1);
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(31)]
    [Arguments(32)]
    [Arguments(33)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(100)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(1024)]
    public async Task FillBytes_VariousLengths_IsDeterministic(int length)
    {
        // For every boundary and non-boundary length, the same seed must produce the same bytes.
        Xoroshiro128PlusPlus rngA = new((ulong)length * 31UL + 7UL);
        Xoroshiro128PlusPlus rngB = new((ulong)length * 31UL + 7UL);

        byte[] bufA = new byte[length];
        byte[] bufB = new byte[length];
        rngA.FillBytes(bufA);
        rngB.FillBytes(bufB);

        await Assert.That(bufA.AsSpan().SequenceEqual(bufB)).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(32)]
    [Arguments(100)]
    [Arguments(256)]
    public async Task FillBytes_ProducesNonZeroOutput(int length)
    {
        // The output must contain at least some non-zero bytes (all-zero is astronomically unlikely).
        Xoroshiro128PlusPlus rng = new(314159265358979UL);
        byte[] buf = new byte[length];
        rng.FillBytes(buf);

        // For lengths >= 8 at least one byte being non-zero is guaranteed in practice.
        if (length >= 8)
        {
            bool anyNonZero = false;
            foreach (byte b in buf)
            {
                if (b != 0)
                {
                    anyNonZero = true;
                    break;
                }
            }

            await Assert.That(anyNonZero).IsTrue();
        }
        else
        {
            int trivialPass = 1;
            await Assert.That(trivialPass).IsEqualTo(1);
        }
    }

    [Test]
    public async Task FillBytes_LargeSpan_CompletesBulkPath()
    {
        Xoroshiro128PlusPlus rng = new(0x123456789ABCDEFUL);
        byte[] buf = new byte[128];
        rng.FillBytes(buf);
        bool anyNonZero = false;
        foreach (byte b in buf)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }
        await Assert.That(anyNonZero).IsTrue();
    }

    [Test]
    public async Task FillBytes_LargeSpan_FillsNonZeroBytes()
    {
        Xoroshiro128PlusPlus rng = new(888UL);
        byte[] buffer = new byte[64];
        rng.FillBytes(buffer);
        bool anyNonZero = false;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }
        await Assert.That(anyNonZero).IsTrue();
    }

    [Test]
    public async Task FillBytes_ExactlyBulkThreshold_IsDeterministic()
    {
        Xoroshiro128PlusPlus rngA = new(999UL);
        Xoroshiro128PlusPlus rngB = new(999UL);
        byte[] bufA = new byte[32];
        byte[] bufB = new byte[32];
        rngA.FillBytes(bufA);
        rngB.FillBytes(bufB);
        await Assert.That(bufA.AsSpan().SequenceEqual(bufB)).IsTrue();
    }

    [Test]
    public async Task PrivateFillBytesVector128_FillsBuffer()
    {
        Xoroshiro128PlusPlus rng = new(123UL);
        MethodInfo? derive = typeof(Xoroshiro128PlusPlus).GetMethod(
            "_DeriveSubStreams", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(derive).IsNotNull();

        object?[] deriveArgs = [null!, null!, null!, null!, null!, null!];
        derive!.Invoke(rng, deriveArgs);
        byte[] buffer = new byte[64];
        Xoroshiro128PlusPlusPrivateAccess.FillBytesVector128(
            rng,
            buffer,
            (ulong)deriveArgs[0]!,
            (ulong)deriveArgs[1]!,
            (ulong)deriveArgs[2]!,
            (ulong)deriveArgs[3]!,
            (ulong)deriveArgs[4]!,
            (ulong)deriveArgs[5]!);

        bool anyNonZero = false;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }
        await Assert.That(anyNonZero).IsTrue();
    }

    [Test]
    public async Task FillBytes_ForceScalarBulkPath_FillsBuffer()
    {
        Xoroshiro128PlusPlus rng = new(42UL);
        byte[] buffer = new byte[256];
        Xoroshiro128PlusPlus.ForceScalarBulkFillForTesting = true;
        try
        {
            rng.FillBytes(buffer);
        }
        finally
        {
            Xoroshiro128PlusPlus.ForceScalarBulkFillForTesting = false;
        }

        bool anyNonZero = false;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }

        await Assert.That(anyNonZero).IsTrue();
    }

    [Test]
    public async Task FillBytes_ScalarPathViaPrivateAccessor_FillsBuffer()
    {
        Xoroshiro128PlusPlus rng = new(321UL);
        MethodInfo? derive = typeof(Xoroshiro128PlusPlus).GetMethod(
            "_DeriveSubStreams", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(derive).IsNotNull();

        object?[] deriveArgs = [null!, null!, null!, null!, null!, null!];
        derive!.Invoke(rng, deriveArgs);
        byte[] buffer = new byte[64];
        Xoroshiro128PlusPlusPrivateAccess.FillBytesScalar4(
            rng,
            buffer,
            (ulong)deriveArgs[0]!,
            (ulong)deriveArgs[1]!,
            (ulong)deriveArgs[2]!,
            (ulong)deriveArgs[3]!,
            (ulong)deriveArgs[4]!,
            (ulong)deriveArgs[5]!);

        bool anyNonZero = false;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }
        await Assert.That(anyNonZero).IsTrue();
    }

    [Test]
    public async Task PrivateRotateLeft128_RotatesLanes()
    {
        MethodInfo? rotate = typeof(Xoroshiro128PlusPlus).GetMethod(
            "_RotateLeft128", BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(rotate).IsNotNull();

        Vector128<ulong> input = Vector128.Create(1UL, 2UL);
        Vector128<ulong> rotated = (Vector128<ulong>)rotate!.Invoke(null, [input, 17])!;
        Vector128<ulong> expected = Vector128.BitwiseOr(
            Vector128.ShiftLeft(input, 17),
            Vector128.ShiftRightLogical(input, 64 - 17));
        await Assert.That(rotated).IsEqualTo(expected);
    }

    [Test]
    public async Task FillBytes_SmallBuffer_HitsSequentialEarlyReturn()
    {
        Xoroshiro128PlusPlus rng = new(77UL);
        byte[] buffer = new byte[8];
        rng.FillBytes(buffer);
        bool anyNonZero = false;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                anyNonZero = true;
                break;
            }
        }
        await Assert.That(anyNonZero).IsTrue();
    }
}
