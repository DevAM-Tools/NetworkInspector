// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="LargeBuffer"/>.
/// Covers construction, span access, byte indexer, primitive read/write (BE/LE),
/// string roundtrips, bulk operations, boundary crossing, resize, and stream extensions.
/// </summary>
internal sealed class LargeBufferTests
{
    #region Construction

    [Test]
    public async Task Construction_ZeroCapacity()
    {
        LargeBuffer buffer = new(0);
        await Assert.That(buffer.Length).IsEqualTo(0L);
        await Assert.That(buffer.Capacity).IsGreaterThanOrEqualTo(0L);
        await Assert.That(buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Construction_SmallCapacity()
    {
        LargeBuffer buffer = new(1);
        await Assert.That(buffer.Length).IsEqualTo(1L);
        await Assert.That(buffer.Capacity).IsGreaterThanOrEqualTo(1L);
        await Assert.That(buffer.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Construction_ExactMultipleOf16()
    {
        LargeBuffer buffer = new(64);
        await Assert.That(buffer.Length).IsEqualTo(64L);
        // Capacity should be exactly 64 since 64 is evenly divisible by 16 (the element size)
        await Assert.That(buffer.Capacity).IsEqualTo(64L);
    }

    [Test]
    public async Task Construction_NonMultipleOf16()
    {
        LargeBuffer buffer = new(13);
        await Assert.That(buffer.Length).IsEqualTo(13L);
        // Capacity rounds up to the next multiple of 16 (the element size) = 16
        await Assert.That(buffer.Capacity).IsEqualTo(16L);
    }

    [Test]
    public async Task Construction_NegativeCapacity_Throws() =>
        await Assert.That(() => new LargeBuffer(-1)).Throws<ArgumentOutOfRangeException>();

    [Test]
    public async Task Construction_AllBytesInitializedToZero()
    {
        LargeBuffer buffer = new(100);
        for (int i = 0; i < 100; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0);
        }
    }

    [Test]
    public async Task MaxCapacity_IsPositive()
    {
        await Assert.That(LargeBuffer.MaxCapacity).IsGreaterThan(0L);
        // Should be significantly larger than int.MaxValue (2 GB)
        await Assert.That(LargeBuffer.MaxCapacity).IsGreaterThan((long)int.MaxValue);
    }

    #endregion

    #region Indexer

    [Test]
    public async Task Indexer_ReadWrite_SingleByte()
    {
        LargeBuffer buffer = new(16);
        buffer[0] = 0xAB;
        buffer[15] = 0xCD;
        await Assert.That(buffer[0]).IsEqualTo((byte)0xAB);
        await Assert.That(buffer[15]).IsEqualTo((byte)0xCD);
    }

    [Test]
    public async Task Indexer_AllPositionsInUlong()
    {
        // Each ulong within a LargeBufferElement has 8 byte positions — test all offsets within a single ulong
        LargeBuffer buffer = new(8);
        for (int i = 0; i < 8; i++)
        {
            buffer[i] = (byte)(0x10 + i);
        }

        for (int i = 0; i < 8; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)(0x10 + i));
        }
    }

    [Test]
    public async Task Indexer_OutOfRange_Throws()
    {
        LargeBuffer buffer = new(8);
        await Assert.That(() => _ = buffer[8]).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _ = buffer[-1]).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Span Access

    [Test]
    public async Task AsSpan_AlignedAccess()
    {
        LargeBuffer buffer = new(16);
        Span<byte> span = buffer.AsSpan(0, 8);
        span[0] = 0x11;
        span[7] = 0x22;
        await Assert.That(buffer[0]).IsEqualTo((byte)0x11);
        await Assert.That(buffer[7]).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task AsSpan_UnalignedAccess()
    {
        // Start at offset 3 (not aligned to ulong boundary)
        LargeBuffer buffer = new(32);
        Span<byte> span = buffer.AsSpan(3, 5);
        span[0] = 0xAA;
        span[4] = 0xBB;
        await Assert.That(buffer[3]).IsEqualTo((byte)0xAA);
        await Assert.That(buffer[7]).IsEqualTo((byte)0xBB);
    }

    [Test]
    public async Task AsSpan_CrossesUlongBoundary()
    {
        // Span from offset 6 with length 4 crosses the internal Low/High ulong boundary within an element (at byte 8)
        LargeBuffer buffer = new(16);
        Span<byte> span = buffer.AsSpan(6, 4);

        span[0] = 0x11; // byte 6
        span[1] = 0x22; // byte 7
        span[2] = 0x33; // byte 8 — next ulong
        span[3] = 0x44; // byte 9

        await Assert.That(buffer[6]).IsEqualTo((byte)0x11);
        await Assert.That(buffer[7]).IsEqualTo((byte)0x22);
        await Assert.That(buffer[8]).IsEqualTo((byte)0x33);
        await Assert.That(buffer[9]).IsEqualTo((byte)0x44);
    }

    [Test]
    public async Task AsReadOnlySpan_ReturnsCorrectData()
    {
        LargeBuffer buffer = new(8);
        buffer[3] = 0xFF;
        ReadOnlySpan<byte> span = buffer.AsReadOnlySpan(0, 8);
        await Assert.That(span[3]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task AsSpan_ZeroLength_ReturnsEmpty()
    {
        LargeBuffer buffer = new(8);
        Span<byte> span = buffer.AsSpan(0, 0);
        await Assert.That(span.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AsSpan_OutOfRange_Throws()
    {
        LargeBuffer buffer = new(8);
        await Assert.That(() => _ = buffer.AsSpan(0, 9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _ = buffer.AsSpan(5, 4)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _ = buffer.AsSpan(-1, 1)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Read — Big Endian

    [Test]
    public async Task ReadUInt16BE_AlignedAndUnaligned()
    {
        LargeBuffer buffer = new(16);
        // Write 0x0102 big-endian at offset 0
        buffer[0] = 0x01;
        buffer[1] = 0x02;
        await Assert.That(buffer.ReadUInt16BE(0)).IsEqualTo((ushort)0x0102);

        // Unaligned: at offset 3
        buffer[3] = 0xAB;
        buffer[4] = 0xCD;
        await Assert.That(buffer.ReadUInt16BE(3)).IsEqualTo((ushort)0xABCD);
    }

    [Test]
    public async Task ReadUInt32BE_CrossingUlongBoundary()
    {
        LargeBuffer buffer = new(16);
        // Place a u32 at offset 6, crossing the ulong boundary (bytes 6,7,8,9)
        buffer[6] = 0xDE;
        buffer[7] = 0xAD;
        buffer[8] = 0xBE;
        buffer[9] = 0xEF;
        await Assert.That(buffer.ReadUInt32BE(6)).IsEqualTo(0xDEADBEEFu);
    }

    [Test]
    public async Task ReadUInt64BE()
    {
        LargeBuffer buffer = new(16);
        buffer[0] = 0x01;
        buffer[1] = 0x02;
        buffer[2] = 0x03;
        buffer[3] = 0x04;
        buffer[4] = 0x05;
        buffer[5] = 0x06;
        buffer[6] = 0x07;
        buffer[7] = 0x08;
        await Assert.That(buffer.ReadUInt64BE(0)).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task ReadInt16BE()
    {
        LargeBuffer buffer = new(8);
        buffer[0] = 0xFF;
        buffer[1] = 0xFE; // -2 in big-endian signed 16-bit
        await Assert.That(buffer.ReadInt16BE(0)).IsEqualTo((short)-2);
    }

    [Test]
    public async Task ReadInt32BE()
    {
        LargeBuffer buffer = new(8);
        buffer[0] = 0xFF;
        buffer[1] = 0xFF;
        buffer[2] = 0xFF;
        buffer[3] = 0xFF; // -1 in big-endian signed 32-bit
        await Assert.That(buffer.ReadInt32BE(0)).IsEqualTo(-1);
    }

    [Test]
    public async Task ReadSingleBE()
    {
        LargeBuffer buffer = new(8);
        // IEEE 754: 1.0f = 0x3F800000
        buffer[0] = 0x3F;
        buffer[1] = 0x80;
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        await Assert.That(buffer.ReadSingleBE(0)).IsEqualTo(1.0f);
    }

    [Test]
    public async Task ReadDoubleBE()
    {
        LargeBuffer buffer = new(16);
        // IEEE 754: 1.0d = 0x3FF0000000000000
        buffer[0] = 0x3F;
        buffer[1] = 0xF0;
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        buffer[5] = 0x00;
        buffer[6] = 0x00;
        buffer[7] = 0x00;
        await Assert.That(buffer.ReadDoubleBE(0)).IsEqualTo(1.0);
    }

    #endregion

    #region Read — Little Endian

    [Test]
    public async Task ReadUInt16LE()
    {
        LargeBuffer buffer = new(8);
        buffer[0] = 0x02;
        buffer[1] = 0x01;
        await Assert.That(buffer.ReadUInt16LE(0)).IsEqualTo((ushort)0x0102);
    }

    [Test]
    public async Task ReadUInt32LE()
    {
        LargeBuffer buffer = new(8);
        buffer[0] = 0xEF;
        buffer[1] = 0xBE;
        buffer[2] = 0xAD;
        buffer[3] = 0xDE;
        await Assert.That(buffer.ReadUInt32LE(0)).IsEqualTo(0xDEADBEEFu);
    }

    [Test]
    public async Task ReadUInt64LE()
    {
        LargeBuffer buffer = new(8);
        buffer[0] = 0x08;
        buffer[1] = 0x07;
        buffer[2] = 0x06;
        buffer[3] = 0x05;
        buffer[4] = 0x04;
        buffer[5] = 0x03;
        buffer[6] = 0x02;
        buffer[7] = 0x01;
        await Assert.That(buffer.ReadUInt64LE(0)).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task ReadDoubleLE()
    {
        LargeBuffer buffer = new(16);
        // IEEE 754 LE: 1.0d = 00 00 00 00 00 00 F0 3F
        buffer[0] = 0x00;
        buffer[1] = 0x00;
        buffer[2] = 0x00;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        buffer[5] = 0x00;
        buffer[6] = 0xF0;
        buffer[7] = 0x3F;
        await Assert.That(buffer.ReadDoubleLE(0)).IsEqualTo(1.0);
    }

    #endregion

    #region Write — Big Endian

    [Test]
    public async Task WriteUInt16BE_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteUInt16BE(3, 0xCAFE);
        await Assert.That(buffer.ReadUInt16BE(3)).IsEqualTo((ushort)0xCAFE);
    }

    [Test]
    public async Task WriteUInt32BE_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteUInt32BE(6, 0xDEADBEEF);
        await Assert.That(buffer.ReadUInt32BE(6)).IsEqualTo(0xDEADBEEFu);
    }

    [Test]
    public async Task WriteUInt64BE_Roundtrip()
    {
        LargeBuffer buffer = new(24);
        buffer.WriteUInt64BE(5, 0x0102030405060708UL);
        await Assert.That(buffer.ReadUInt64BE(5)).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task WriteInt32BE_Roundtrip_Negative()
    {
        LargeBuffer buffer = new(8);
        buffer.WriteInt32BE(0, -12345);
        await Assert.That(buffer.ReadInt32BE(0)).IsEqualTo(-12345);
    }

    [Test]
    public async Task WriteSingleBE_Roundtrip()
    {
        LargeBuffer buffer = new(8);
        buffer.WriteSingleBE(0, 3.14f);
        await Assert.That(buffer.ReadSingleBE(0)).IsEqualTo(3.14f);
    }

    [Test]
    public async Task WriteDoubleBE_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteDoubleBE(0, 2.71828);
        await Assert.That(buffer.ReadDoubleBE(0)).IsEqualTo(2.71828);
    }

    #endregion

    #region Write — Little Endian

    [Test]
    public async Task WriteUInt32LE_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteUInt32LE(6, 0xDEADBEEF);
        await Assert.That(buffer.ReadUInt32LE(6)).IsEqualTo(0xDEADBEEFu);
    }

    [Test]
    public async Task WriteDoubleLE_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteDoubleLE(0, 1.23456789);
        await Assert.That(buffer.ReadDoubleLE(0)).IsEqualTo(1.23456789);
    }

    #endregion

    #region Bytes & Strings

    [Test]
    public async Task ReadBytes_ReturnsCorrectSlice()
    {
        LargeBuffer buffer = new(16);
        buffer[4] = 0xAA;
        buffer[5] = 0xBB;
        buffer[6] = 0xCC;
        ReadOnlySpan<byte> bytes = buffer.ReadBytes(4, 3);
        int len = bytes.Length;
        byte b0 = bytes[0];
        byte b1 = bytes[1];
        byte b2 = bytes[2];
        await Assert.That(len).IsEqualTo(3);
        await Assert.That(b0).IsEqualTo((byte)0xAA);
        await Assert.That(b1).IsEqualTo((byte)0xBB);
        await Assert.That(b2).IsEqualTo((byte)0xCC);
    }

    [Test]
    public async Task WriteBytes_Roundtrip()
    {
        LargeBuffer buffer = new(16);
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05];
        buffer.WriteBytes(3, data);
        ReadOnlySpan<byte> result = buffer.ReadBytes(3, 5);
        bool matches = result.SequenceEqual(data);
        await Assert.That(matches).IsTrue();
    }

    [Test]
    public async Task Utf8String_Roundtrip()
    {
        LargeBuffer buffer = new(256);
        string original = "Hello, LargeBuffer! 🌍";
        int bytesWritten = buffer.WriteUtf8String(10, original);
        string decoded = buffer.ReadUtf8String(10, bytesWritten);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task AsciiString_Roundtrip()
    {
        LargeBuffer buffer = new(64);
        string original = "ASCII only";
        int bytesWritten = buffer.WriteAsciiString(0, original);
        string decoded = buffer.ReadAsciiString(0, bytesWritten);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task WriteUtf8String_TightlySizedBuffer_AsciiContent_Succeeds()
    {
        // Regression: GetMaxByteCount(N) returns 3*N+3 for UTF-8, so a length-10 ASCII
        // string would have requested a 33-byte slice from an 11-byte buffer and thrown
        // ArgumentOutOfRangeException. The fix uses GetByteCount, which returns the
        // actual encoded length (10) and fits.
        string original = "ASCII only"; // 10 ASCII chars
        LargeBuffer buffer = new(original.Length);
        int bytesWritten = buffer.WriteUtf8String(0, original);
        await Assert.That(bytesWritten).IsEqualTo(original.Length);
        string decoded = buffer.ReadUtf8String(0, bytesWritten);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task WriteUtf8String_ExactByteCountFits_NoExtraBytesWritten()
    {
        // Multi-byte UTF-8: "ä" = 0xC3 0xA4 (2 bytes), "🌍" = F0 9F 8C 8D (4 bytes).
        // GetByteCount("ä🌍") = 6. GetMaxByteCount(2 chars) = 9.
        string original = "ä🌍";
        int exactBytes = System.Text.Encoding.UTF8.GetByteCount(original);
        // Buffer sized to exactly the encoded length plus a sentinel after it.
        LargeBuffer buffer = new(exactBytes + 1);
        buffer[exactBytes] = 0xEE;

        int bytesWritten = buffer.WriteUtf8String(0, original);

        await Assert.That(bytesWritten).IsEqualTo(exactBytes);
        await Assert.That(buffer[exactBytes]).IsEqualTo((byte)0xEE);
        string decoded = buffer.ReadUtf8String(0, bytesWritten);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task WriteAsciiString_TightlySizedBuffer_Succeeds()
    {
        // ASCII GetMaxByteCount and GetByteCount coincide, but verify the behaviour
        // is consistent with the WriteUtf8String fix.
        string original = "Hello";
        LargeBuffer buffer = new(original.Length);
        int bytesWritten = buffer.WriteAsciiString(0, original);
        await Assert.That(bytesWritten).IsEqualTo(original.Length);
        string decoded = buffer.ReadAsciiString(0, bytesWritten);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task Latin1String_Read()
    {
        LargeBuffer buffer = new(32);
        // Latin-1: ä = 0xE4, ö = 0xF6, ü = 0xFC
        buffer[0] = 0xE4;
        buffer[1] = 0xF6;
        buffer[2] = 0xFC;
        string decoded = buffer.ReadLatin1String(0, 3);
        await Assert.That(decoded).IsEqualTo("äöü");
    }

    #endregion

    #region Bulk Operations

    [Test]
    public async Task CopyTo_CopyFrom_Roundtrip()
    {
        LargeBuffer buffer = new(64);
        byte[] source = [0x10, 0x20, 0x30, 0x40, 0x50];
        buffer.CopyFrom(source, 10);

        byte[] dest = new byte[5];
        buffer.CopyTo(10, dest);

        for (int i = 0; i < 5; i++)
        {
            await Assert.That(dest[i]).IsEqualTo(source[i]);
        }
    }

    [Test]
    public async Task Clear_SetsRegionToZero()
    {
        LargeBuffer buffer = new(16);
        buffer.Fill(0, 16, 0xFF);
        buffer.Clear(4, 8);

        // Bytes 0-3 should still be 0xFF
        for (int i = 0; i < 4; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0xFF);
        }

        // Bytes 4-11 should be 0
        for (int i = 4; i < 12; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0);
        }

        // Bytes 12-15 should still be 0xFF
        for (int i = 12; i < 16; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0xFF);
        }
    }

    [Test]
    public async Task Fill_FillsRegion()
    {
        LargeBuffer buffer = new(32);
        buffer.Fill(8, 16, 0xAA);

        for (int i = 8; i < 24; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0xAA);
        }

        // Before and after should be zero
        await Assert.That(buffer[7]).IsEqualTo((byte)0);
        await Assert.That(buffer[24]).IsEqualTo((byte)0);
    }

    #endregion

    #region Resize

    [Test]
    public async Task Resize_Grow_PreservesData()
    {
        LargeBuffer buffer = new(8);
        buffer.WriteUInt64BE(0, 0x0102030405060708UL);

        LargeBuffer.Resize(ref buffer, 32);

        await Assert.That(buffer.Length).IsEqualTo(32L);
        await Assert.That(buffer.ReadUInt64BE(0)).IsEqualTo(0x0102030405060708UL);
        // New bytes should be zero
        await Assert.That(buffer.ReadUInt64BE(8)).IsEqualTo(0UL);
    }

    [Test]
    public async Task Resize_Shrink_TruncatesData()
    {
        LargeBuffer buffer = new(16);
        buffer.WriteUInt32BE(0, 0xDEADBEEF);
        buffer.WriteUInt32BE(12, 0xCAFEBABE);

        LargeBuffer.Resize(ref buffer, 8);

        await Assert.That(buffer.Length).IsEqualTo(8L);
        await Assert.That(buffer.ReadUInt32BE(0)).IsEqualTo(0xDEADBEEFu);
        // Offset 12 no longer accessible
        await Assert.That(() => buffer.ReadUInt32BE(12)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Resize_ToZero()
    {
        LargeBuffer buffer = new(100);
        LargeBuffer.Resize(ref buffer, 0);
        await Assert.That(buffer.Length).IsEqualTo(0L);
        await Assert.That(buffer.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Resize_NegativeCapacity_Throws()
    {
        LargeBuffer buffer = new(8);
        await Assert.That(() => LargeBuffer.Resize(ref buffer, -1)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Stream Extensions

    [Test]
    public async Task Stream_Sync_ReadWriteRoundtrip()
    {
        LargeBuffer buffer = new(256);

        // Write known data into buffer
        byte[] original = new byte[200];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(i & 0xFF);
        }

        buffer.CopyFrom(original, 0);

        // Write buffer → stream
        using MemoryStream stream = new();
        await stream.WriteFromAsync(buffer, 0, 200).ConfigureAwait(false);

        // Read stream → new buffer
        LargeBuffer buffer2 = new(256);
        stream.Position = 0;
        int bytesRead = await stream.ReadIntoAsync(buffer2, 0, 200).ConfigureAwait(false);

        await Assert.That(bytesRead).IsEqualTo(200);

        for (int i = 0; i < 200; i++)
        {
            await Assert.That(buffer2[i]).IsEqualTo((byte)(i & 0xFF));
        }
    }

    [Test]
    public async Task Stream_Async_ReadWriteRoundtrip()
    {
        LargeBuffer buffer = new(128);

        byte[] original = new byte[100];
        for (int i = 0; i < original.Length; i++)
        {
            original[i] = (byte)(0xFF - (i & 0xFF));
        }

        buffer.CopyFrom(original, 0);

        using MemoryStream stream = new();
        await stream.WriteFromAsync(buffer, 0, 100).ConfigureAwait(false);

        LargeBuffer buffer2 = new(128);
        stream.Position = 0;
        int bytesRead = await stream.ReadIntoAsync(buffer2, 0, 100).ConfigureAwait(false);

        await Assert.That(bytesRead).IsEqualTo(100);

        for (int i = 0; i < 100; i++)
        {
            await Assert.That(buffer2[i]).IsEqualTo((byte)(0xFF - (i & 0xFF)));
        }
    }

    [Test]
    public async Task Stream_Sync_ReadInto_ReadsCorrectBytes()
    {
        // Arrange — MemoryStream with known data
        byte[] source = new byte[50];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(0xC0 + (i & 0x3F));
        }

        using MemoryStream stream = new(source);
        LargeBuffer buffer = new(50);

        // Act — sync local function used to satisfy CA1849: this test intentionally exercises the synchronous API path.
        int bytesRead = SyncRead();
        int SyncRead()
        {
            return stream.ReadInto(buffer, 0, 50);
        }

        // Assert
        await Assert.That(bytesRead).IsEqualTo(50);
        for (int i = 0; i < 50; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo(source[i]);
        }
    }

    [Test]
    public async Task Stream_Sync_WriteFrom_WritesCorrectBytes()
    {
        // Arrange
        LargeBuffer buffer = new(50);
        for (int i = 0; i < 50; i++)
        {
            buffer[i] = (byte)(0xD0 + (i & 0x3F));
        }

        // Act — sync local function used to satisfy CA1849: this test intentionally exercises the synchronous API path.
        using MemoryStream stream = new();
        void SyncWrite()
        {
            stream.WriteFrom(buffer, 0, 50);
        }
        SyncWrite();

        // Assert
        byte[] written = stream.ToArray();
        await Assert.That(written.Length).IsEqualTo(50);
        for (int i = 0; i < 50; i++)
        {
            await Assert.That(written[i]).IsEqualTo(buffer[i]);
        }
    }

    [Test]
    public async Task Stream_SyncAsync_Consistency()
    {
        // Sync and async roundtrips must produce identical results
        byte[] source = new byte[200];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i & 0xFF);
        }

        LargeBuffer writeBuffer = new(200);
        writeBuffer.CopyFrom(source, 0);

        // Sync write then async read
        using MemoryStream ms1 = new();
        void WriteMs1()
        {
            ms1.WriteFrom(writeBuffer, 0, 200);
        }
        WriteMs1(); // sync local function: avoids CA1849 while preserving sync-write test intent
        ms1.Position = 0;
        LargeBuffer asyncReadBuffer = new(200);
        await ms1.ReadIntoAsync(asyncReadBuffer, 0, 200).ConfigureAwait(false);

        // Async write then sync read
        using MemoryStream ms2 = new();
        await ms2.WriteFromAsync(writeBuffer, 0, 200).ConfigureAwait(false);
        ms2.Position = 0;
        LargeBuffer syncReadBuffer = new(200);
        int ReadMs2()
        {
            return ms2.ReadInto(syncReadBuffer, 0, 200);
        }
        ReadMs2(); // sync local function: avoids CA1849 while preserving sync-read test intent

        // Both must match the original
        for (int i = 0; i < 200; i++)
        {
            await Assert.That(asyncReadBuffer[i]).IsEqualTo(source[i]);
            await Assert.That(syncReadBuffer[i]).IsEqualTo(source[i]);
        }
    }

    [Test]
    public async Task Stream_ReadIntoAsync_Cancellation_ThrowsOperationCanceled()
    {
        // Arrange — use a stream that blocks until cancelled
        using CancellationTokenSource cts = new();
        using BlockingReadStream blockingStream = new(cts);
        LargeBuffer buffer = new(100);

        // Act + Assert — cancellation must surface as OperationCanceledException
        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.That(async () => await blockingStream.ReadIntoAsync(buffer, 0, 100, cts.Token).ConfigureAwait(false))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Stream_ReadInto_Sync_ZeroCount_ReadsZeroBytes()
    {
        // Arrange
        using MemoryStream stream = new([0x01, 0x02, 0x03]);
        LargeBuffer buffer = new(10);

        // Act — sync local function used to satisfy CA1849: this test intentionally exercises the synchronous API path.
        int bytesRead = SyncRead();
        int SyncRead()
        {
            return stream.ReadInto(buffer, 0, 0);
        }

        // Assert — zero-count read must return zero and not advance stream
        await Assert.That(bytesRead).IsEqualTo(0);
        await Assert.That(stream.Position).IsEqualTo(0L);
    }

    [Test]
    public async Task Stream_ReadIntoAsync_ZeroCount_ReadsZeroBytes()
    {
        using MemoryStream stream = new([0x01, 0x02, 0x03]);
        LargeBuffer buffer = new(10);

        int bytesRead = await stream.ReadIntoAsync(buffer, 0, 0).ConfigureAwait(false);

        await Assert.That(bytesRead).IsEqualTo(0);
        await Assert.That(stream.Position).IsEqualTo(0L);
    }

    [Test]
    public async Task Stream_ReadInto_Sync_ShortRead_ReturnsActualBytesRead()
    {
        // Stream has only 30 bytes, but we ask for 50 — must return 30
        byte[] data = new byte[30];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        using MemoryStream stream = new(data);
        LargeBuffer buffer = new(100);

        // Act — sync local function used to satisfy CA1849: this test intentionally exercises the synchronous API path.
        int bytesRead = SyncRead();
        int SyncRead()
        {
            return stream.ReadInto(buffer, 0, 50);
        }

        // Assert
        await Assert.That(bytesRead).IsEqualTo(30);
        for (int i = 0; i < 30; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo(data[i]);
        }
    }

    [Test]
    public async Task Stream_ReadIntoAsync_ShortRead_ReturnsActualBytesRead()
    {
        byte[] data = new byte[30];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1);
        }

        using MemoryStream stream = new(data);
        LargeBuffer buffer = new(100);

        int bytesRead = await stream.ReadIntoAsync(buffer, 0, 50).ConfigureAwait(false);

        await Assert.That(bytesRead).IsEqualTo(30);
        for (int i = 0; i < 30; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo(data[i]);
        }
    }

    [Test]
    public async Task Stream_ReadInto_Sync_WithOffset_WritesAtCorrectPosition()
    {
        // Arrange — write 10 bytes into buffer starting at offset 20
        byte[] data = new byte[10];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(0xE0 + i);
        }

        using MemoryStream stream = new(data);
        LargeBuffer buffer = new(50);

        // Act — sync local function used to satisfy CA1849: this test intentionally exercises the synchronous API path.
        int bytesRead = SyncRead();
        int SyncRead()
        {
            return stream.ReadInto(buffer, 20, 10);
        }

        // Assert — bytes at [20..29] match; [0..19] still zero
        await Assert.That(bytesRead).IsEqualTo(10);
        for (int i = 0; i < 20; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)0);
        }
        for (int i = 0; i < 10; i++)
        {
            await Assert.That(buffer[20 + i]).IsEqualTo(data[i]);
        }
    }

    [Test]
    public async Task Stream_ReadInto_Sync_AtBufferEnd_Succeeds()
    {
        // Arrange — 4 bytes written near the very end of a 32-byte buffer
        byte[] data = [0x11, 0x22, 0x33, 0x44];
        using MemoryStream stream = new(data);
        LargeBuffer buffer = new(32);

        // Act — offset = 28, count = 4 (last 4 bytes of buffer) — sync local function used to satisfy CA1849
        int bytesRead = SyncRead();
        int SyncRead()
        {
            return stream.ReadInto(buffer, 28, 4);
        }

        // Assert
        await Assert.That(bytesRead).IsEqualTo(4);
        await Assert.That(buffer[28]).IsEqualTo((byte)0x11);
        await Assert.That(buffer[29]).IsEqualTo((byte)0x22);
        await Assert.That(buffer[30]).IsEqualTo((byte)0x33);
        await Assert.That(buffer[31]).IsEqualTo((byte)0x44);
    }

    #endregion

    #region Edge Cases

    [Test]
    public async Task WriteByte_ReadByte_AtEveryElementBytePosition()
    {
        // Write a pattern across multiple LargeBufferElement entries to verify all byte positions
        LargeBuffer buffer = new(64);
        for (int i = 0; i < 64; i++)
        {
            buffer.WriteByte(i, (byte)(i * 3 + 7));
        }

        for (int i = 0; i < 64; i++)
        {
            await Assert.That(buffer.ReadByte(i)).IsEqualTo((byte)(i * 3 + 7));
        }
    }

    [Test]
    public async Task SpanWrite_ThenIndexerRead_Consistent()
    {
        LargeBuffer buffer = new(16);
        Span<byte> span = buffer.AsSpan(0, 16);
        for (int i = 0; i < 16; i++)
        {
            span[i] = (byte)(0xA0 + i);
        }

        for (int i = 0; i < 16; i++)
        {
            await Assert.That(buffer[i]).IsEqualTo((byte)(0xA0 + i));
        }
    }

    [Test]
    public async Task IndexerWrite_ThenSpanRead_Consistent()
    {
        LargeBuffer buffer = new(16);
        for (int i = 0; i < 16; i++)
        {
            buffer[i] = (byte)(0xB0 + i);
        }

        // Read span values into array before awaiting (Span cannot live across await)
        ReadOnlySpan<byte> span = buffer.AsReadOnlySpan(0, 16);
        byte[] values = span.ToArray();
        for (int i = 0; i < 16; i++)
        {
            await Assert.That(values[i]).IsEqualTo((byte)(0xB0 + i));
        }
    }

    [Test]
    public async Task LargeCapacity_1MB()
    {
        // Verify the buffer works at larger sizes (1 MB)
        int capacity = 1024 * 1024;
        LargeBuffer buffer = new(capacity);
        await Assert.That(buffer.Length).IsEqualTo((long)capacity);

        // Write at various offsets
        buffer.WriteUInt32BE(0, 0xAAAAAAAA);
        buffer.WriteUInt32BE(500_000, 0xBBBBBBBB);
        buffer.WriteUInt32BE(capacity - 4, 0xCCCCCCCC);

        await Assert.That(buffer.ReadUInt32BE(0)).IsEqualTo(0xAAAAAAAAu);
        await Assert.That(buffer.ReadUInt32BE(500_000)).IsEqualTo(0xBBBBBBBBu);
        await Assert.That(buffer.ReadUInt32BE(capacity - 4)).IsEqualTo(0xCCCCCCCCu);
    }

    #endregion
}

/// <summary>
/// A <see cref="Stream"/> that blocks on <see cref="ReadAsync"/> until the
/// <see cref="CancellationToken"/> it was constructed with is cancelled.
/// Used exclusively to test cancellation paths in <see cref="LargeBufferStreamExtensions"/>.
/// </summary>
internal sealed class BlockingReadStream(CancellationTokenSource cts) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        cts.Token.WaitHandle.WaitOne();
        cts.Token.ThrowIfCancellationRequested();
        return 0;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => new(ReadAsync(Array.Empty<byte>(), 0, 0, cancellationToken));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
