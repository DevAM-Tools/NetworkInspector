// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Layout, checksums, and the generated speed pattern of <see cref="SampleFrameWriter"/>.</summary>
internal sealed class SampleFrameWriterTests
{
    #region Helpers

    // A valid header sums to 0xFFFF (one's complement) including its checksum field.
    private static bool _ChecksumHolds(ReadOnlySpan<byte> data, uint seed)
    {
        uint sum = seed;
        int index = 0;
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
        }

        if (index < data.Length)
        {
            sum += (uint)(data[index] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return sum == 0xFFFF;
    }

    #endregion

    #region Speed pattern

    [Test]
    [Arguments(0L)]
    [Arguments(50L)]
    [Arguments(100L)]
    [Arguments(150L)]
    [Arguments(200L)]
    public async Task SpeedIsErrorEveryFiftiethFrame(long index)
    {
        await Assert.That(SampleFrameWriter.SpeedFor(index)).IsEqualTo((byte)254);
    }

    [Test]
    [Arguments(70L)]
    [Arguments(76L)]
    [Arguments(170L)]
    [Arguments(176L)]
    public async Task SpeedIsNotAvailableInTheSeventiesWindow(long index)
    {
        await Assert.That(SampleFrameWriter.SpeedFor(index)).IsEqualTo((byte)255);
    }

    [Test]
    [Arguments(1L, 1)]
    [Arguments(69L, 69)]
    [Arguments(77L, 77)]
    [Arguments(199L, 199)]
    [Arguments(201L, 1)]
    public async Task SpeedOutsideTheSpecialWindowsIsIndexModulo200(long index, int expected)
    {
        await Assert.That(SampleFrameWriter.SpeedFor(index)).IsEqualTo((byte)expected);
    }

    [Test]
    public async Task SpeedCountsOverOneThousandFramesAreStable()
    {
        int errors = 0;
        int notAvailable = 0;
        int[] others = new int[256];
        for (int index = 0; index < 1000; index++)
        {
            byte speed = SampleFrameWriter.SpeedFor(index);
            if (speed == 254)
            {
                errors++;
            }
            else if (speed == 255)
            {
                notAvailable++;
            }
            else
            {
                others[speed]++;
            }
        }

        await Assert.That(errors).IsEqualTo(20);
        await Assert.That(notAvailable).IsEqualTo(70);
        await Assert.That(others.Max()).IsLessThanOrEqualTo(5);

        // 200-253 never occur as regular values: the window between regular speeds and the special values stays empty.
        await Assert.That(others[200..254].Sum()).IsEqualTo(0);
    }

    [Test]
    public async Task SpeedRefusesNegativeIndex()
    {
        await Assert.That(() => SampleFrameWriter.SpeedFor(-1)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region UDP frame

    [Test]
    public async Task UdpFrameHasTheDocumentedLayout()
    {
        byte[] frame = SampleFrameWriter.Write(counter: 0x0102030405060708, gear: 4, speed: 200, udpPort: 40000);

        await Assert.That(frame.Length).IsEqualTo(56);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12))).IsEqualTo((ushort)0x0800);
        await Assert.That(frame[14]).IsEqualTo((byte)0x45);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16))).IsEqualTo((ushort)42);
        await Assert.That(frame[23]).IsEqualTo((byte)17);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34))).IsEqualTo((ushort)50000);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36))).IsEqualTo((ushort)40000);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(38))).IsEqualTo((ushort)22);
        await Assert.That(frame.AsSpan(42, 4).SequenceEqual("NIS1"u8)).IsTrue();
        await Assert.That(BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(46))).IsEqualTo(0x0102030405060708UL);
        await Assert.That(frame[54]).IsEqualTo((byte)4);
        await Assert.That(frame[55]).IsEqualTo((byte)200);
    }

    [Test]
    public async Task UdpFrameHasAValidIpv4Checksum()
    {
        byte[] frame = SampleFrameWriter.Write(1, 2, 3, 40000);

        await Assert.That(_ChecksumHolds(frame.AsSpan(14, 20), 0)).IsTrue();
    }

    [Test]
    public async Task UdpFrameRefusesPortZero()
    {
        await Assert.That(() => SampleFrameWriter.Write(1, 2, 3, 0)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion

    #region TCP frame

    [Test]
    public async Task TcpFrameHasTheDocumentedLayout()
    {
        byte[] frame = SampleFrameWriter.WriteTcp(counter: 9, gear: 1, speed: 2, tcpPort: 41234, sequence: 0xFFFFFFF0, sourcePort: 50123);

        await Assert.That(frame.Length).IsEqualTo(68);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(16))).IsEqualTo((ushort)54);
        await Assert.That(frame[23]).IsEqualTo((byte)6);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(34))).IsEqualTo((ushort)50123);
        await Assert.That(BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(36))).IsEqualTo((ushort)41234);
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(38))).IsEqualTo(0xFFFFFFF0u);
        await Assert.That(frame[46]).IsEqualTo((byte)0x50);
        await Assert.That(frame[47]).IsEqualTo((byte)0x18);
        await Assert.That(frame.AsSpan(54, 4).SequenceEqual("NIS1"u8)).IsTrue();
    }

    [Test]
    public async Task TcpFrameHasValidIpv4AndTcpChecksums()
    {
        byte[] frame = SampleFrameWriter.WriteTcp(1, 2, 3, 41234, 1);

        // Pseudo header: 192.0.2.1, 192.0.2.2, protocol 6, TCP length 34.
        uint pseudo = 0xC000 + 0x0201 + 0xC000 + 0x0202 + 6 + 34;
        await Assert.That(_ChecksumHolds(frame.AsSpan(14, 20), 0)).IsTrue();
        await Assert.That(_ChecksumHolds(frame.AsSpan(34, 34), pseudo)).IsTrue();
    }

    [Test]
    public async Task TcpFrameRefusesPortZero()
    {
        await Assert.That(() => SampleFrameWriter.WriteTcp(1, 2, 3, 0, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SampleFrameWriter.WriteTcp(1, 2, 3, 41234, 1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    #endregion
}
