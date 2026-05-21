// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Application;

/// <summary>
/// Regression coverage for <see cref="PduTransportMultiLayer"/> concatenated slot framing.
/// </summary>
internal sealed class PduTransportMultiLayerTests
{
    [Test]
    public async Task WriteHeader_EncodesConcatenatedBigEndianTuples()
    {
        PduTransportConfigFb fb = new(
            ImmutableArray.Create(
                new PduEntry
                {
                    PduId = 0x301,
                    Name = "Alpha",
                },
                new PduEntry
                {
                    PduId = 0x302,
                    Name = "Beta",
                }));

        PduTransportMultiLayer multi = PduTransportMultiLayer.Create(
            fb,
            new PduTransportSlot
            {
                PduId = 0x0301,
                Payload = new byte[] { 0x11 },
            },
            new PduTransportSlot
            {
                PduId = 0x0302,
                Payload = new byte[] { 0x22, 0x33 },
            });

        await Assert.That(multi.HeaderSize).IsEqualTo((4 + 4 + 1) + (4 + 4 + 2))
            .Because("Concatenated header + payload sizing.");

        /*
         Heap buffer avoids CS4007: Span<byte> cannot cross await boundaries when asserting per byte.
        */
        byte[] buffer = new byte[multi.HeaderSize];
        multi.WriteHeader(buffer);

        int offset = 0;
        /*
         Slot 1: BE id 4B, BE len 4B, payload
        */
        uint id1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        await Assert.That((int)id1).IsEqualTo(0x0301);

        offset += 4;

        uint len1 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        await Assert.That((int)len1).IsEqualTo(1);

        offset += 4;
        byte payload1 = buffer[offset];
        offset += 1;
        await Assert.That(payload1).IsEqualTo((byte)0x11);

        uint id2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        await Assert.That((int)id2).IsEqualTo(0x0302);

        offset += 4;

        uint len2 = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        await Assert.That((int)len2).IsEqualTo(2);

        offset += 4;

        byte payload2a = buffer[offset];
        byte payload2b = buffer[offset + 1];
        await Assert.That(payload2a).IsEqualTo((byte)0x22);
        await Assert.That(payload2b).IsEqualTo((byte)0x33);
    }
}
