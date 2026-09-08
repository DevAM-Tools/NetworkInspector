// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Range and default tests for <see cref="ValueCacheBuildOptions"/>.</summary>
internal sealed class ValueCacheBuildOptionsTests
{
    [Test]
    public async Task Ctor_Default_UsesChunkShift12()
    {
        ValueCacheBuildOptions options = new();
        await Assert.That(options.ChunkShift).IsEqualTo(12);
    }

    [Test]
    [Arguments(ValueCacheBuildOptions.MinChunkShift)]
    [Arguments(12)]
    [Arguments(ValueCacheBuildOptions.MaxChunkShift)]
    public async Task ChunkShift_InRange_IsStored(int shift)
    {
        ValueCacheBuildOptions options = new() { ChunkShift = shift };
        await Assert.That(options.ChunkShift).IsEqualTo(shift);
    }

    [Test]
    [Arguments(int.MinValue)]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(3)]
    [Arguments(21)]
    [Arguments(int.MaxValue)]
    public async Task ChunkShift_OutOfRange_Throws(int shift)
    {
        await Assert.That(() => _ = new ValueCacheBuildOptions { ChunkShift = shift })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ObjectInitializer_OmitsChunkShift_KeepsDefault12()
    {
        ValueCacheBuildOptions options = new() { RecordAllFields = true };
        await Assert.That(options.ChunkShift).IsEqualTo(12);
    }
}
