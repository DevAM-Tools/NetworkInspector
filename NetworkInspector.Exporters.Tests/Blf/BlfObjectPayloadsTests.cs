// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Blf;

/// <summary>
/// Layout tests for <see cref="BlfObjectPayloads"/> (Type 1, Type 139, Type 50, Type 71).
/// </summary>
internal sealed class BlfObjectPayloadsTests
{
    [Test]
    public async Task TryBuildCanMessagePayload_EffId_WritesBit31AndFlagsZero()
    {
        byte[] socketCan = SocketCanGenerators.BuildCanClassic(0x123, [0xAA, 0xBB], extended: true);
        PooledBuffer buffer = new(32);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanMessagePayload(socketCan, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            byte flags = payload[2];
            byte dlc = payload[3];
            uint blfId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(16);
            await Assert.That(flags).IsEqualTo((byte)0);
            await Assert.That(dlc).IsEqualTo((byte)2);
            await Assert.That(blfId).IsEqualTo(0x8000_0123u);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanMessagePayload_Rtr_WritesFlags80()
    {
        byte[] socketCan = SocketCanGenerators.BuildCanClassic(0x123, [], rtr: true);
        PooledBuffer buffer = new(32);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanMessagePayload(socketCan, 1, buffer);
            byte flags = buffer.WrittenSpan[2];
            byte dlc = buffer.WrittenSpan[3];

            await Assert.That(built).IsTrue();
            await Assert.That(flags).IsEqualTo((byte)0x80);
            await Assert.That(dlc).IsEqualTo((byte)0);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanMessagePayload_TooShort_ReturnsFalse()
    {
        PooledBuffer buffer = new(16);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanMessagePayload(new byte[7], 1, buffer);

            await Assert.That(built).IsFalse();
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanXlChannelFramePayload_Writes104HeaderAndPayload()
    {
        byte[] xl = SocketCanGenerators.BuildCanXl(
            0x7AB, [0xAA, 0xBB, 0xCC, 0xDD], vcid: 0x1A, sdt: 0x42, acceptanceField: 0xDEAD_BEEF, sec: true, rrs: true);
        PooledBuffer buffer = new(256);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanXlChannelFramePayload(xl, 5, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            uint frameIdentifier = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
            ushort dlc = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(18));
            ushort dataLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(20));
            uint acceptance = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(28));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(48));
            byte[] data = payload[104..];

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(108);
            await Assert.That(payload[0]).IsEqualTo((byte)5);
            await Assert.That(frameIdentifier).IsEqualTo(0x7ABu);
            await Assert.That(payload[16]).IsEqualTo((byte)0x42);
            await Assert.That(dlc).IsEqualTo((ushort)3);
            await Assert.That(dataLength).IsEqualTo((ushort)4);
            await Assert.That(payload[26]).IsEqualTo((byte)0x1A);
            await Assert.That(acceptance).IsEqualTo(0xDEAD_BEEFu);
            await Assert.That(flags & BlfConstants.BlfCanXlFlagXlf).IsEqualTo(BlfConstants.BlfCanXlFlagXlf);
            await Assert.That(flags & BlfConstants.BlfCanXlFlagSec).IsEqualTo(BlfConstants.BlfCanXlFlagSec);
            await Assert.That(flags & BlfConstants.BlfCanXlFlagRrs).IsEqualTo(BlfConstants.BlfCanXlFlagRrs);
            await Assert.That(data).IsEquivalentTo(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanXlChannelFramePayload_EmptyData_DlcZero()
    {
        byte[] xl = SocketCanGenerators.BuildCanXl(0x01, []);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanXlChannelFramePayload(xl, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            ushort dlc = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(18));
            ushort dataLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(20));

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(104);
            await Assert.That(dlc).IsEqualTo((ushort)0);
            await Assert.That(dataLength).IsEqualTo((ushort)0);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanXlChannelFramePayload_XlfClear_ReturnsFalse()
    {
        byte[] xl = SocketCanGenerators.BuildCanXl(0x01, [0xAA]);
        xl[4] = 0;
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanXlChannelFramePayload(xl, 1, buffer);

            await Assert.That(built).IsFalse();
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanXlChannelFramePayload_DeclaredLengthExceedsBuffer_ReturnsFalse()
    {
        byte[] xl = SocketCanGenerators.BuildCanXl(0x01, [0xAA]);
        BinaryPrimitives.WriteUInt16LittleEndian(xl.AsSpan(6), 8);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanXlChannelFramePayload(xl, 1, buffer);

            await Assert.That(built).IsFalse();
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanFdMessagePayload_Writes20ByteHeader()
    {
        byte[] socketCan = SocketCanGenerators.BuildCanFd(0x200, [0xAA, 0xBB, 0xCC, 0xDD], extended: true, brs: true);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanFdMessagePayload(socketCan, 2, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            uint blfId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(24);
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload)).IsEqualTo((ushort)2);
            await Assert.That(payload[3]).IsEqualTo((byte)4);
            await Assert.That(blfId).IsEqualTo(0x8000_0200u);
            await Assert.That((payload[13] & BlfConstants.BlfCanFdEdl) != 0).IsTrue();
            await Assert.That(payload[20]).IsEqualTo((byte)0xAA);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildCanFdMessage64Payload_Writes40ByteHeader()
    {
        byte[] socketCan = SocketCanGenerators.BuildCanFd(0x456, [0x01, 0x02, 0x03, 0x04], brs: true);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildCanFdMessage64Payload(socketCan, 5, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
            uint blfId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(44);
            await Assert.That(payload[0]).IsEqualTo((byte)5);
            await Assert.That(payload[1]).IsEqualTo((byte)4);
            await Assert.That(payload[2]).IsEqualTo((byte)4);
            await Assert.That(blfId).IsEqualTo(0x456u);
            await Assert.That(flags & BlfConstants.CanFd64FlagEdl).IsEqualTo(BlfConstants.CanFd64FlagEdl);
            await Assert.That(flags & BlfConstants.CanFd64FlagBrs).IsEqualTo(BlfConstants.CanFd64FlagBrs);
            await Assert.That(payload.AsSpan(40, 4).ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildEthernetFramePayload_WritesEthertypeLittleEndian()
    {
        byte[] ethernet = new byte[18];
        ethernet.AsSpan(0, 6).Fill(0xFF);
        ethernet.AsSpan(6, 6).Fill(0x11);
        ethernet[12] = 0x08;
        ethernet[13] = 0x00;
        ethernet[14] = 0xDE;
        ethernet[15] = 0xAD;
        ethernet[16] = 0xBE;
        ethernet[17] = 0xEF;
        PooledBuffer buffer = new(64);
        try
        {
            bool built = BlfObjectPayloads.TryBuildEthernetFramePayload(ethernet, 1, 0, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16))).IsEqualTo((ushort)0x0800);
            await Assert.That(payload[16]).IsEqualTo((byte)0x00);
            await Assert.That(payload[17]).IsEqualTo((byte)0x08);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    [Arguments((ushort)0x8100)]
    [Arguments((ushort)0x9100)]
    [Arguments((ushort)0x88A8)]
    public async Task TryBuildEthernetFramePayload_VlanTpid_WritesTpidTciAndInnerType(ushort tpid)
    {
        byte[] ethernet = new byte[20];
        ethernet.AsSpan(0, 6).Fill(0xFF);
        ethernet.AsSpan(6, 6).Fill(0x11);
        BinaryPrimitives.WriteUInt16BigEndian(ethernet.AsSpan(12), tpid);
        BinaryPrimitives.WriteUInt16BigEndian(ethernet.AsSpan(14), 0x00AB);
        BinaryPrimitives.WriteUInt16BigEndian(ethernet.AsSpan(16), 0x0800);
        ethernet[18] = 0xDE;
        ethernet[19] = 0xAD;
        PooledBuffer buffer = new(64);
        try
        {
            bool built = BlfObjectPayloads.TryBuildEthernetFramePayload(ethernet, 1, 0, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16))).IsEqualTo((ushort)0x0800);
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(18))).IsEqualTo(tpid);
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(20))).IsEqualTo((ushort)0x00AB);
            await Assert.That(payload.AsSpan(32, 2).ToArray()).IsEquivalentTo(new byte[] { 0xDE, 0xAD });
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildFlexRayRcvMessagePayload_WritesFrameFlags()
    {
        byte[] fr = FlexRayGenerators.BuildFlexRayFrame(0, 10, 3, 0xABCD, [0xDE, 0xAD], sync: true);
        PooledBuffer buffer = new(128);
        try
        {
            bool built = BlfObjectPayloads.TryBuildFlexRayRcvMessagePayload(fr, 1, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(36));

            await Assert.That(built).IsTrue();
            await Assert.That(flags & 0x04).IsEqualTo(0x04u);
            await Assert.That(flags & 0x01).IsEqualTo(0u);
        }
        finally
        {
            buffer.Return();
        }
    }

    [Test]
    public async Task TryBuildLinMessage2Payload_ReadsDltLinHeader()
    {
        byte[] lin = LinGenerators.BuildLinFrame(0x15, [0xDE, 0xAD, 0xBE, 0xEF], checksum: 0xAB);
        PooledBuffer buffer = new(160);
        try
        {
            bool built = BlfObjectPayloads.TryBuildLinMessage2Payload(lin, 2, buffer);
            byte[] payload = buffer.WrittenSpan.ToArray();

            await Assert.That(built).IsTrue();
            await Assert.That(payload.Length).IsEqualTo(136);
            await Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12))).IsEqualTo((ushort)2);
            await Assert.That(payload[37]).IsEqualTo((byte)0x15);
            await Assert.That(payload[38]).IsEqualTo((byte)4);
            await Assert.That(payload[120]).IsEqualTo((byte)0xAB);
            await Assert.That(payload.AsSpan(112, 4).ToArray()).IsEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        }
        finally
        {
            buffer.Return();
        }
    }
}
