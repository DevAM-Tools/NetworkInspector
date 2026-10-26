// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Boundary tests for <see cref="BlfObjectPayloads"/>: oversized trailing data, declared lengths beyond the bytes present, and the
/// LIN DLC limit.
/// </summary>
internal sealed class BlfObjectPayloadsValidationTests
{
    /// <summary>Builds a SocketCAN FD frame whose buffer carries <paramref name="trailingBytes"/> bytes after the 8-byte header.</summary>
    private static byte[] _BuildFdFrameWithTrailingBytes(int trailingBytes, byte declaredCount)
    {
        byte[] frame = new byte[8 + trailingBytes];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 0x123);
        frame[4] = declaredCount;
        frame[5] = 0x04; // FDF
        for (int index = 0; index < trailingBytes; index++)
        {
            frame[8 + index] = (byte)(index + 1);
        }

        return frame;
    }

    [Test]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(300)]
    public async Task CanFdMessagePayloadKeepsSixtyFourBytesWhenBufferIsLarger(int trailingBytes)
    {
        byte[] frame = _BuildFdFrameWithTrailingBytes(trailingBytes, 64);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanFdMessagePayload(frame, 1, buffer);
            byte validDataBytes = buffer.WrittenSpan[14];

            await Assert.That(built).IsTrue();
            await Assert.That(validDataBytes).IsEqualTo((byte)64);
            await Assert.That(buffer.Length).IsEqualTo(20 + 64);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(300)]
    public async Task CanFdMessage64PayloadKeepsSixtyFourBytesWhenBufferIsLarger(int trailingBytes)
    {
        byte[] frame = _BuildFdFrameWithTrailingBytes(trailingBytes, 64);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanFdMessage64Payload(frame, 1, buffer);
            byte validDataBytes = buffer.WrittenSpan[2];

            await Assert.That(built).IsTrue();
            await Assert.That(validDataBytes).IsEqualTo((byte)64);
            await Assert.That(buffer.Length).IsEqualTo(40 + 64);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task CanFdMessage64PayloadClampsDeclaredCountAboveSixtyFour()
    {
        byte[] frame = _BuildFdFrameWithTrailingBytes(300, 200);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanFdMessage64Payload(frame, 1, buffer);
            byte validDataBytes = buffer.WrittenSpan[2];

            await Assert.That(built).IsTrue();
            await Assert.That(validDataBytes).IsEqualTo((byte)64);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task LinMessage2PayloadClampsDlcToEightAndCopiesEightBytes()
    {
        // DLC nibble 15 with 12 bytes of data present: the LIN maximum is 8.
        byte[] lin = new byte[8 + 12];
        lin[4] = (byte)((15 << 4) | 0x02);
        lin[5] = 0x15;
        for (int index = 0; index < 12; index++)
        {
            lin[8 + index] = (byte)(0x10 + index);
        }

        PooledBuffer buffer = new(256);
        try
        {
            bool built = BlfObjectPayloads.TryBuildLinMessage2Payload(lin, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(payload[38]).IsEqualTo((byte)8);
            await Assert.That(payload[112..120]).IsEquivalentTo(lin[8..16]);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task LinMessage2PayloadCopiesAvailableBytesWhenFrameIsShorterThanDeclared()
    {
        // Declared DLC 8, but only 3 data bytes follow the header.
        byte[] lin = new byte[8 + 3];
        lin[4] = (byte)((8 << 4) | 0x01);
        lin[5] = 0x15;
        lin[8] = 0xAA;
        lin[9] = 0xBB;
        lin[10] = 0xCC;

        PooledBuffer buffer = new(256);
        try
        {
            bool built = BlfObjectPayloads.TryBuildLinMessage2Payload(lin, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(payload[38]).IsEqualTo((byte)3);
            await Assert.That(payload[112..120]).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0, 0, 0, 0, 0 });
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task LinMessage2PayloadWithDlcZeroWritesNoData()
    {
        byte[] lin = new byte[8];
        lin[4] = 0x01;
        lin[5] = 0x15;
        PooledBuffer buffer = new(256);
        try
        {
            bool built = BlfObjectPayloads.TryBuildLinMessage2Payload(lin, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(payload[38]).IsEqualTo((byte)0);
            await Assert.That(payload[112..120]).IsEquivalentTo(new byte[8]);
        }
        finally
        {
            buffer.Return();
        }
    }
}
