// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

    [Test]
    public async Task ObjectLengthAboveMaxBlockReadSize_SkipsAndFindsTheNextObject()
    {
        byte[] can = FrameBuilders.BuildSocketCanClassic(0x123, [1, 2, 3, 4]);
        byte[] good = new BlfTestGenerator().AddCanFrame(1, can, 1_000_000).Build();

        byte[] data = new byte[good.Length + 16];
        good.AsSpan(0, 144).CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(144), BlfConstants.ObjectMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(148), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(150), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(152), 300u * 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(156), BlfConstants.ObjTypeCanMessage);
        good.AsSpan(144).CopyTo(data.AsSpan(160));

        using BlfSource source = BlfSource.FromData(
            data, "huge-object.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        SourceTestFixture.InitializeAndStartSource(source);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        using BlfStreamSource streamSource = BlfStreamSource.FromStream(
            new MemoryStream(data), "huge-object-stream.blf");
        SourceTestFixture.InitializeAndStartSource(streamSource);
        int streamCount = 0;
        while (streamSource.NextFrame() is not null)
        {
            streamCount++;
        }

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(streamCount).IsEqualTo(1);
    }
}
