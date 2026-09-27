// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// DLT_LIN payload padding: 4 bytes when the data length is 0..4, 8 bytes when it is 5..8.
/// </summary>
internal sealed class LinLayerTests
{
    [Test]
    [Arguments(0, 12)]
    [Arguments(1, 12)]
    [Arguments(4, 12)]
    [Arguments(5, 16)]
    [Arguments(8, 16)]
    public async Task HeaderSize_PadsPayloadToFourOrEight(int dataLength, int expectedSize)
    {
        byte[] data = new byte[dataLength];
        LinLayer layer = new(frameId: 1, data);

        await Assert.That(layer.HeaderSize).IsEqualTo(expectedSize);
    }

    [Test]
    public async Task WriteHeader_OneDataByte_PadsThreeZerosAndKeepsLengthNibble()
    {
        LinLayer layer = new(frameId: 1, data: [0x5A]);
        byte[] frame = new byte[layer.HeaderSize];

        layer.WriteHeader(frame);

        await Assert.That(frame.Length).IsEqualTo(12);
        await Assert.That(frame[4] >> 4).IsEqualTo((byte)1);
        await Assert.That(frame[8]).IsEqualTo((byte)0x5A);
        await Assert.That(frame[9]).IsEqualTo((byte)0);
        await Assert.That(frame[10]).IsEqualTo((byte)0);
        await Assert.That(frame[11]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task WriteHeader_FiveDataBytes_PadsThreeZeros()
    {
        LinLayer layer = new(frameId: 2, data: [1, 2, 3, 4, 5]);
        byte[] frame = new byte[layer.HeaderSize];

        layer.WriteHeader(frame);

        await Assert.That(frame.Length).IsEqualTo(16);
        await Assert.That(frame[4] >> 4).IsEqualTo((byte)5);
        await Assert.That(frame[8]).IsEqualTo((byte)1);
        await Assert.That(frame[12]).IsEqualTo((byte)5);
        await Assert.That(frame[13]).IsEqualTo((byte)0);
        await Assert.That(frame[14]).IsEqualTo((byte)0);
        await Assert.That(frame[15]).IsEqualTo((byte)0);
    }
}
