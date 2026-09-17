// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// Packed (unpadded) LOBJ layout and containers that split one inner object across two blobs.
/// </summary>
internal sealed class BlfPackedObjectTests
{
    private const uint _FileMagic = 0x47474F4C;
    private const uint _ObjectMagic = 0x4A424F4C;
    private const int _FileHeaderSize = 144;
    private const int _ObjectHeaderOverhead = 32;
    private const ushort _HeaderTypeV1 = 1;
    private const uint _TimestampFlagsNs = 0x02;

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    [Test]
    public async Task PackedUnpaddedConsecutiveLobj_BothFramesVisibleOnFileAndStream()
    {
        byte[] firstPayload = _ClassicCanPayload(channel: 1, extraByte: 0x11);
        byte[] secondPayload = _ClassicCanPayload(channel: 2, extraByte: 0x22);
        await Assert.That((32 + firstPayload.Length) % 4).IsEqualTo(1);

        byte[] blfData = _BuildFileWithUnpaddedObjects(
        [
            (BlfConstants.ObjTypeCanMessage, firstPayload),
            (BlfConstants.ObjTypeCanMessage, secondPayload),
        ]);

        using BlfSource fileSource = BlfSource.FromData(
            blfData, "packed.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        SourceTestFixture.InitializeAndStartSource(fileSource);
        List<Frame> fileFrames = _ReadAll(fileSource);

        using BlfStreamSource streamSource = BlfStreamSource.FromStream(new MemoryStream(blfData), "packed-stream.blf");
        SourceTestFixture.InitializeAndStartSource(streamSource);
        List<Frame> streamFrames = _ReadAll(streamSource);

        await Assert.That(fileFrames.Count).IsEqualTo(2);
        await Assert.That(streamFrames.Count).IsEqualTo(2);
        await Assert.That(fileFrames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(fileFrames[1].LinkType).IsEqualTo(LinkType.CanSocketcan);
    }

    [Test]
    public async Task PackedUnpaddedConsecutiveInnerLobj_BothFramesVisibleOnFileAndStream()
    {
        byte[] firstPayload = _ClassicCanPayload(channel: 1, extraByte: 0x11);
        byte[] secondPayload = _ClassicCanPayload(channel: 2, extraByte: 0x22);
        await Assert.That((32 + firstPayload.Length) % 4).IsEqualTo(1);

        byte[] first = _WriteUnpaddedObject(BlfConstants.ObjTypeCanMessage, firstPayload, 1_000_000);
        byte[] second = _WriteUnpaddedObject(BlfConstants.ObjTypeCanMessage, secondPayload, 2_000_000);
        byte[] blfData = _Concatenate(_FileHeader(), _BuildUncompressedLogContainer(_Concatenate(first, second)));

        using BlfSource fileSource = BlfSource.FromData(
            blfData, "packed-inner.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        SourceTestFixture.InitializeAndStartSource(fileSource);
        List<Frame> fileFrames = _ReadAll(fileSource);

        using BlfStreamSource streamSource = BlfStreamSource.FromStream(
            new MemoryStream(blfData), "packed-inner-stream.blf");
        SourceTestFixture.InitializeAndStartSource(streamSource);
        List<Frame> streamFrames = _ReadAll(streamSource);

        await Assert.That(fileFrames.Count).IsEqualTo(2);
        await Assert.That(streamFrames.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SplitEthernetType120AcrossUncompressedContainers_YieldsOneFrame()
    {
        byte[] ethernet = FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [0xAA]);
        byte[] innerObject = _BuildType120Object(ethernet, channel: 1);
        int splitAt = 40;
        await Assert.That(innerObject.Length).IsGreaterThan(splitAt);

        byte[] container1 = _BuildUncompressedLogContainer(innerObject.AsSpan(0, splitAt).ToArray());
        byte[] container2 = _BuildUncompressedLogContainer(innerObject.AsSpan(splitAt).ToArray());
        byte[] blfData = _Concatenate(_FileHeader(), container1, container2);

        using BlfSource fileSource = BlfSource.FromData(
            blfData, "split.blf", new BlfSourceOptions { ScanMode = ScanMode.Full });
        SourceTestFixture.InitializeAndStartSource(fileSource);
        List<Frame> fileFrames = _ReadAll(fileSource);

        using BlfStreamSource streamSource = BlfStreamSource.FromStream(new MemoryStream(blfData), "split-stream.blf");
        SourceTestFixture.InitializeAndStartSource(streamSource);
        List<Frame> streamFrames = _ReadAll(streamSource);

        await Assert.That(fileFrames.Count).IsEqualTo(1);
        await Assert.That(streamFrames.Count).IsEqualTo(1);
        await Assert.That(fileFrames[0].LinkType).IsEqualTo(LinkType.Ethernet);
        await Assert.That(fileFrames[0].Data.Span.SequenceEqual(ethernet)).IsTrue();
        await Assert.That(streamFrames[0].Data.Span.SequenceEqual(ethernet)).IsTrue();

        Frame? byId = fileSource.FrameById(new FrameId(0));
        await Assert.That(byId).IsNotNull();
        await Assert.That(byId!.Value.Data.Span.SequenceEqual(ethernet)).IsTrue();
    }

    private static List<Frame> _ReadAll(IFrameSource source)
    {
        List<Frame> frames = [];
        Frame? next;
        while ((next = source.NextFrame()) is not null)
        {
            frames.Add(next.Value);
        }

        return frames;
    }

    /// <summary>
    /// Type 1 payload of 17 bytes so unpadded <c>object_length</c> is 49 (≡ 1 mod 4).
    /// </summary>
    private static byte[] _ClassicCanPayload(ushort channel, byte extraByte)
    {
        byte[] payload = new byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, channel);
        payload[2] = 0;
        payload[3] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x123);
        payload[8] = extraByte;
        return payload;
    }

    private static byte[] _BuildType120Object(byte[] ethernet, ushort channel)
    {
        byte[] payload = new byte[32 + ethernet.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), channel);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), (ushort)ethernet.Length);
        ethernet.CopyTo(payload.AsSpan(32));
        return _WriteUnpaddedObject(BlfConstants.ObjTypeEthernetFrameEx, payload, 1_000_000);
    }

    private static byte[] _BuildUncompressedLogContainer(byte[] innerBytes)
    {
        byte[] containerPayload = new byte[16 + innerBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(containerPayload, BlfConstants.CompressionNone);
        BinaryPrimitives.WriteUInt32LittleEndian(containerPayload.AsSpan(8), (uint)innerBytes.Length);
        innerBytes.CopyTo(containerPayload.AsSpan(16));
        return _WriteUnpaddedObject(BlfConstants.ObjTypeLogContainer, containerPayload, 0);
    }

    private static byte[] _BuildFileWithUnpaddedObjects((uint ObjectType, byte[] Payload)[] objects)
    {
        int total = _FileHeaderSize;
        foreach ((uint _, byte[] payload) in objects)
        {
            total += _ObjectHeaderOverhead + payload.Length;
        }

        byte[] result = new byte[total];
        _FileHeader().CopyTo(result, 0);
        int offset = _FileHeaderSize;
        long ts = 1_000_000;
        foreach ((uint objectType, byte[] payload) in objects)
        {
            byte[] obj = _WriteUnpaddedObject(objectType, payload, ts);
            obj.CopyTo(result.AsSpan(offset));
            offset += obj.Length;
            ts += 1_000_000;
        }

        return result;
    }

    private static byte[] _WriteUnpaddedObject(uint objectType, byte[] payload, long offsetNanos)
    {
        int objectLength = _ObjectHeaderOverhead + payload.Length;
        byte[] dest = new byte[objectLength];
        BinaryPrimitives.WriteUInt32LittleEndian(dest, _ObjectMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(4), (ushort)_ObjectHeaderOverhead);
        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(6), _HeaderTypeV1);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(8), (uint)objectLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(12), objectType);
        BinaryPrimitives.WriteUInt32LittleEndian(dest.AsSpan(16), _TimestampFlagsNs);
        BinaryPrimitives.WriteUInt64LittleEndian(dest.AsSpan(24), (ulong)offsetNanos);
        payload.CopyTo(dest.AsSpan(_ObjectHeaderOverhead));
        return dest;
    }

    private static byte[] _FileHeader() => new BlfTestGenerator().Build();

    private static byte[] _Concatenate(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] part in parts)
        {
            total += part.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result.AsSpan(offset));
            offset += part.Length;
        }

        return result;
    }
}
