// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Sources.Blf;
using NetworkInspector.Sources.Blf.Format;
using NetworkInspector.Sources.Tests.Generators;

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Tests for corrupt/invalid BLF file handling.
/// Verifies graceful error handling for malformed data — no panics, no crashes.
/// </summary>
internal sealed class BlfCorruptTests
{
    // ========================================================================
    // Invalid magic
    // ========================================================================

    [Test]
    public async Task InvalidMagic_ThrowsBlfException()
    {
        byte[] data = "NOTALOGG_FILE_AT_ALL_0000"u8.ToArray();
        await Assert.That(() => BlfSource.FromData(data, "corrupt.blf"))
            .Throws<BlfException>();
    }

    // ========================================================================
    // Empty data
    // ========================================================================

    [Test]
    public async Task EmptyData_ThrowsBlfException()
    {
        await Assert.That(() => BlfSource.FromData([], "empty.blf"))
            .Throws<BlfException>();
    }

    // ========================================================================
    // Truncated header
    // ========================================================================

    [Test]
    public async Task TruncatedHeader_ThrowsBlfException()
    {
        // Just the magic, much shorter than 144 bytes
        byte[] data = "LOGG"u8.ToArray();
        await Assert.That(() => BlfSource.FromData(data, "truncated.blf"))
            .Throws<BlfException>();
    }

    // ========================================================================
    // Truncated file
    // ========================================================================

    [Test]
    public async Task TruncatedFile_DoesNotCrash()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            0x0800, [0xDE, 0xAD]);

        byte[] fullData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        // Truncate to half — keeps header but corrupts objects
        byte[] truncated = fullData[..(fullData.Length / 2)];

        // Should either fail gracefully (exception or 0 frames) but not crash
        try
        {
            using BlfSource source = BlfSource.FromData(
                truncated, "truncated.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
            long frameCount = source.EstimatedFrameCount ?? 0;
            await Assert.That(frameCount <= 1).IsTrue();
        }
        catch (BlfException)
        {
            // Error is acceptable for truncated data
        }
    }

    // ========================================================================
    // Random bytes with LOGG magic
    // ========================================================================

    [Test]
    public async Task RandomBytesWithLoggMagic_DoesNotCrash()
    {
        byte[] data = new byte[204]; // 4 (magic) + 200 random
        "LOGG"u8.CopyTo(data);
        byte[] pattern = [0xDE, 0xAD, 0xBE, 0xEF];
        for (int i = 4; i < data.Length; i++)
        {
            data[i] = pattern[i % 4];
        }

        // Should not throw unhandled exceptions or crash
        bool didNotCrash = false;
        try
        {
            using BlfSource source = BlfSource.FromData(data, "random.blf");
            // If it somehow parses, that's fine
            didNotCrash = true;
        }
        catch (BlfException)
        {
            // Expected case for garbage data
            didNotCrash = true;
        }

        await Assert.That(didNotCrash).IsTrue();
    }

    // ========================================================================
    // Valid header, corrupt objects
    // ========================================================================

    [Test]
    public async Task ValidHeaderCorruptObjects_DoesNotCrash()
    {
        byte[] eth = FrameBuilders.BuildEthernetFrame(
            [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            0x0800, [0xAA]);

        byte[] blfData = new BlfTestGenerator()
            .AddEthernetFrame(1, eth, 1_000_000)
            .Build();

        // Corrupt bytes after file header (offset 200 onwards)
        if (blfData.Length > 200)
        {
            for (int i = 200; i < blfData.Length; i++)
            {
                blfData[i] = 0xFF;
            }
        }

        bool didNotCrash = false;
        try
        {
            using BlfSource source = BlfSource.FromData(
                blfData, "bad_objects.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
            await Assert.That(source.EstimatedFrameCount!.Value <= 1).IsTrue();
            didNotCrash = true;
        }
        catch (BlfException)
        {
            // Error is acceptable
            didNotCrash = true;
        }

        await Assert.That(didNotCrash).IsTrue();
    }
}
