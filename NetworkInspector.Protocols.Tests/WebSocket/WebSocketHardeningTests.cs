// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for WebSocket parsing of crafted input: the bounded decompression of per-message DEFLATE payloads
/// (<c>ws.max_decompressed_bytes</c>) and the exactness of the eager index groups when a segment holds several frames,
/// frames cut by the end of the segment, or frames with a declared length but no payload bytes.
/// </summary>
internal sealed class WebSocketHardeningTests
{
    private const string _LimitSetting = "ws.max_decompressed_bytes";

    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private static readonly byte[] _Http101 = Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");

    /// <summary>Wraps WebSocket bytes behind a 101 response in one server to client TCP segment (the way WebSocket is reached).</summary>
    private static byte[] _Segment(byte[] webSocketBytes)
    {
        EthernetLayer eth = new(_ServerMac, _ClientMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(80, 49152, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame([.. _Http101, .. webSocketBytes]);
    }

    /// <summary>An unmasked frame with the first header octet <paramref name="first"/> and a payload below 126 octets.</summary>
    private static byte[] _Frame(byte first, params byte[] payload) => [first, (byte)payload.Length, .. payload];

    /// <summary>An unmasked frame with the 16-bit extended length form.</summary>
    private static byte[] _LongFrame(byte first, byte[] payload) =>
        [first, 126, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    private static byte[] _RawDeflate(byte[] plain)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(plain);
        }

        byte[] compressed = output.ToArray();

        // The decoder appends the sync-flush tail itself, so strip the one that the compressor wrote.
        return compressed.Length >= 4 && compressed[^4] == 0 && compressed[^3] == 0 && compressed[^2] == 0xFF && compressed[^1] == 0xFF
            ? compressed[..^4]
            : compressed;
    }

    #region Setting

    [Test]
    public async Task SettingHasSecureDefaultAndRange()
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);

        Setting? setting = captured?.GetSetting(_LimitSetting);

        await Assert.That(setting).IsNotNull();
        await Assert.That(setting!.Value.TryGetAsU64(out ulong value)).IsTrue();
        await Assert.That(value).IsEqualTo(64UL * 1024 * 1024);
        await Assert.That(setting.MinValue!.Value.TryGetAsU64(out ulong min) && min == 1024).IsTrue();
        await Assert.That(setting.MaxValue!.Value.TryGetAsU64(out ulong max) && max == 128UL * 1024 * 1024 * 1024).IsTrue();
    }

    [Test]
    [Arguments(1023UL)]
    [Arguments(0UL)]
    [Arguments(137438953473UL)]
    public async Task SettingOutOfRangeIsRejectedWithTheRange(ulong rejected)
    {
        SettingsManager? captured = null;
        using Stack stack = ProtocolTestHelper.BuildStack(settings => captured = settings);
        Setting setting = captured!.GetSetting(_LimitSetting)!;

        bool valid = setting.TryValidate(SettingValue.U64(rejected), out SettingProblem? problem);

        await Assert.That(valid).IsFalse();
        await Assert.That(problem!.ToString()).Contains("1024").And.Contains("137438953472");
    }

    #endregion

    #region Decompression limit

    [Test]
    public async Task CompressedFrameWithinTheLimitIsDecompressed()
    {
        byte[] plain = Encoding.UTF8.GetBytes(new string('x', 3000));
        byte[] frame = _Segment(_LongFrame(0xC2, _RawDeflate(plain)));

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "websocket.payload.decompressed", plain).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed.limit").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CompressedFrameOverTheLimitIsNotDecompressedAndNamesTheSetting()
    {
        byte[] frame = _Segment(_LongFrame(0xC2, _RawDeflate(new byte[200_000])));

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "websocket.payload.decompressed.limit", _LimitSetting)
            .ConfigureAwait(false);
    }

    [Test]
    public async Task CompressedFrameOfExactlyTheLimitIsDecompressed()
    {
        byte[] plain = new byte[1024];
        byte[] frame = _Segment(_LongFrame(0xC2, _RawDeflate(plain)));

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertBytesField(stack, packet, "websocket.payload.decompressed", plain).ConfigureAwait(false);
    }

    [Test]
    public async Task CompressedFrameOneByteOverTheLimitIsNotDecompressed()
    {
        byte[] frame = _Segment(_LongFrame(0xC2, _RawDeflate(new byte[1025])));

        using Stack stack = ProtocolTestHelper.BuildStackWithSettings((_LimitSetting, SettingValue.U64(1024)));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "websocket.payload.decompressed").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.decompressed.limit").ConfigureAwait(false);
    }

    [Test]
    public async Task LimitFieldIsIndexedExactly()
    {
        byte[] frame = _Segment(_LongFrame(0xC2, _RawDeflate(new byte[200_000])));

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame, (_LimitSetting, SettingValue.U64(1024))).ConfigureAwait(false);
    }

    #endregion

    #region Exact index groups

    /// <summary>Segments whose frames expose every way the first frame can differ from the frames that follow.</summary>
    public static IEnumerable<Func<(string, byte[])>> ExactnessCorpus()
    {
        yield return static () => ("declared-length-without-bytes", _Segment([0x81, 0x05]));
        yield return static () => ("extended-length-zero", _Segment([0x82, 0x7E, 0x00, 0x00]));
        yield return static () => ("ping-without-payload-then-text", _Segment([.. _Frame(0x89), .. _Frame(0x81, 0x68, 0x69)]));
        yield return static () => ("text-then-masked-binary", _Segment(
            [.. _Frame(0x81, 0x68), 0x82, 0x81, 1, 2, 3, 4, 0x55]));
        yield return static () => ("continuation-frame", _Segment(_Frame(0x00, 0x68, 0x69)));
        yield return static () => ("close-with-one-octet", _Segment(_Frame(0x88, 0x03)));
        yield return static () => ("close-with-status", _Segment(_Frame(0x88, 0x03, 0xE8)));
        yield return static () => ("compression-flag-on-second-frame", _Segment(
            [.. _Frame(0x81, 0x61), .. _Frame(0xC1, 0xFF, 0xFF, 0xFF, 0xFF)]));
        yield return static () => ("corrupt-compressed-first-frame", _Segment(_Frame(0xC1, 0xFF, 0xFF, 0xFF, 0xFF)));
        yield return static () => ("compressed-frame-without-bytes", _Segment([0xC1, 0x04]));
        yield return static () => ("frame-cut-inside-payload", _Segment([0x81, 0x0A, 0x61, 0x62]));
        yield return static () => ("pong-then-ping", _Segment([.. _Frame(0x8A, 0x01), .. _Frame(0x89, 0x02)]));
    }

    [Test]
    [MethodDataSource(nameof(ExactnessCorpus))]
    public async Task EagerIndexGroupsMatchTheFieldsOfEveryFrame(string name, byte[] frame)
    {
        _ = name; // shown as the test-case label

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
    }

    #endregion
}
