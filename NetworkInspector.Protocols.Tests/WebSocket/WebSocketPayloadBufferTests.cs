// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that an unmasked or inflated WebSocket payload is attached to the packet as one additional buffer before the nested
/// dispatch, and that a payload which is neither masked nor compressed is not. Every frame goes through the full Ethernet,
/// IPv4, TCP, HTTP (101 response), and WebSocket stack. A <see cref="BoundSliceProbeProtocol"/> registered at key 0 of the
/// <c>ws.port</c> table applies the check that a stateful nested protocol makes: the bytes it is handed must be a slice of
/// one of the packet's buffers.
/// <para>
/// The number of buffers the transport already delivers is measured once on an unmasked, uncompressed frame
/// (<see cref="_TransportBufferCount"/>): WebSocket binds nothing for it, so the expectations do not hard-code what the TCP
/// and HTTP layers bind for a single segment.
/// </para>
/// </summary>
internal sealed class WebSocketPayloadBufferTests
{
    /// <summary>The masking key of the masked frames (RFC 6455 section 5.3); any non-zero key shows that unmasking changed the bytes.</summary>
    private const uint _MaskingKey = 0xDEADBEEF;

    /// <summary>The message bytes that every test expects to find in the new buffer and at the nested protocol.</summary>
    private static readonly byte[] _Payload = Encoding.UTF8.GetBytes("{\"ws\":\"payload that is long enough to compress a little\"}");

    /// <summary>Client MAC address of the frames the helper builds.</summary>
    private static readonly MacAddress _ClientMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    /// <summary>Server MAC address of the frames the helper builds.</summary>
    private static readonly MacAddress _ServerMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    /// <summary>Client IPv4 address of the frames the helper builds.</summary>
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    /// <summary>Server IPv4 address of the frames the helper builds.</summary>
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    /// <summary>The HTTP response that upgrades the connection; HTTP hands everything behind it to the WebSocket protocol.</summary>
    private const string _Http101Response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n";

    /// <summary>
    /// The buffer count of a packet whose WebSocket frame is neither masked nor compressed: only what the transport delivers.
    /// Measured once per test run.
    /// </summary>
    private static readonly Lazy<int> _TransportBufferCountValue = new(() =>
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStack(probe);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, _UpgradeFrame(_Frame(_Payload)), 0, Timestamp.FromMillis(0));
        return packet.BufferCount;
    });

    /// <summary>The number of buffers (frame included) that the transport delivers for a single-segment upgrade message.</summary>
    private static int _TransportBufferCount => _TransportBufferCountValue.Value;

    #region Message building

    /// <summary>Encodes one WebSocket frame with <see cref="WebSocketLayer"/> (the layer masks the payload when a key is given).</summary>
    /// <param name="payload">The payload; for a masked frame the plain bytes, the layer masks them.</param>
    /// <param name="opcode">The frame opcode.</param>
    /// <param name="maskingKey">The masking key, or <see langword="null"/> for an unmasked frame.</param>
    /// <param name="compressed">Whether RSV1 (per-message compressed) is set; the caller passes already compressed bytes.</param>
    private static byte[] _Frame(byte[] payload, byte opcode = WebSocketOpcode.Text, uint? maskingKey = null, bool compressed = false)
    {
        WebSocketLayer layer = new(payload, opcode, new WebSocketFrameOptions(Fin: true, Rsv1: compressed, MaskingKey: maskingKey));
        ArrayBufferWriter<byte> writer = new(initialCapacity: 256);
        layer.WriteStream(writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Compresses <paramref name="data"/> as one RFC 7692 per-message payload: raw DEFLATE without the trailing sync-flush
    /// tail <c>00 00 FF FF</c>, which the receiver appends itself.
    /// </summary>
    private static byte[] _Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        byte[] compressed = output.ToArray();
        return compressed.Length >= 4 && compressed[^4] == 0x00 && compressed[^3] == 0x00 && compressed[^2] == 0xFF && compressed[^1] == 0xFF
            ? compressed[..^4]
            : compressed;
    }

    /// <summary>Builds a frame whose TCP payload is a 101 response followed by <paramref name="webSocketBytes"/>.</summary>
    private static byte[] _UpgradeFrame(byte[] webSocketBytes)
    {
        byte[] head = Encoding.ASCII.GetBytes(_Http101Response);
        byte[] combined = [.. head, .. webSocketBytes];
        EthernetLayer eth = new(_ServerMac, _ClientMac, maxFrameSize: 20000);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(80, 49152, seqNum: 1, ackNum: 1, flags: TcpFlags.PshAck, windowSize: 65535);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(combined);
    }

    #endregion

    #region Parsing

    /// <summary>Builds a stack with a fresh probe and parses <paramref name="frame"/> as packet 0.</summary>
    private static (Stack Stack, Packet Packet, BoundSliceProbeProtocol Probe) _Parse(byte[] frame)
    {
        BoundSliceProbeProtocol probe = new();
        Stack stack = BoundSliceProbeProtocol.BuildStack(probe);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        return (stack, packet, probe);
    }

    #endregion

    #region Derived payloads are bound

    /// <summary>
    /// A masked text or binary payload is bound once, after the transport's buffers, with the unmasked bytes; the nested
    /// protocol is handed exactly that bound slice.
    /// </summary>
    /// <param name="opcode">The data opcode of the frame (text or binary); both reach the nested protocol through the same table.</param>
    [Test]
    [Arguments(WebSocketOpcode.Text)]
    [Arguments(WebSocketOpcode.Binary)]
    public async Task MaskedPayloadIsBoundAsOneMoreBufferAndReachesTheNestedProtocolAsThatSlice(byte opcode)
    {
        byte[] frame = _UpgradeFrame(_Frame(_Payload, opcode, _MaskingKey));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Payload)).IsTrue()
                .Because("the new buffer holds the unmasked payload, not the masked wire bytes");

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue().Because("the nested protocol must get a packet slice");
            await Assert.That(seen.BufferIndex).IsEqualTo(_TransportBufferCount);
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Payload)).IsTrue();
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A per-message compressed (RSV1) frame without a mask is bound once with the inflated bytes, not the compressed ones.
    /// </summary>
    [Test]
    public async Task CompressedUnmaskedPayloadIsBoundAsTheInflatedBytes()
    {
        byte[] frame = _UpgradeFrame(_Frame(_Deflate(_Payload), compressed: true));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Payload)).IsTrue()
                .Because("the new buffer holds the inflated payload, not the compressed bytes");

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue();
            await Assert.That(seen.BufferIndex).IsEqualTo(_TransportBufferCount);
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Payload)).IsTrue();
        }
    }

    /// <summary>
    /// Unmasking and inflating are two steps but one derived payload: only the final bytes become a buffer.
    /// </summary>
    [Test]
    public async Task MaskedAndCompressedPayloadAddsOneBufferNotTwo()
    {
        byte[] frame = _UpgradeFrame(_Frame(_Deflate(_Payload), maskingKey: _MaskingKey, compressed: true));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            // The unmasked-but-still-compressed bytes are an intermediate result; only the final inflated payload is bound.
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Payload)).IsTrue();
            await Assert.That(probe.Observations.Single().BufferIndex).IsEqualTo(_TransportBufferCount);
        }
    }

    /// <summary>
    /// Two masked frames in one segment take two buffers, in frame order, and the nested protocol is called once per frame
    /// with the slice of its own buffer.
    /// </summary>
    [Test]
    public async Task MaskedFramesOfOnePacketAreBoundInFrameOrder()
    {
        byte[] first = Encoding.UTF8.GetBytes("first frame payload");
        byte[] second = Encoding.UTF8.GetBytes("second frame payload");
        byte[] frame = _UpgradeFrame([.. _Frame(first, maskingKey: _MaskingKey), .. _Frame(second, maskingKey: 0x01020304)]);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 2);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(first)).IsTrue();
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount + 1).AsSpan().SequenceEqual(second)).IsTrue();

            BoundSliceProbeProtocol.Observation[] seen = [.. probe.Observations];
            await Assert.That(seen.Length).IsEqualTo(2);
            await Assert.That(seen[0].BufferIndex).IsEqualTo(_TransportBufferCount);
            await Assert.That(seen[1].BufferIndex).IsEqualTo(_TransportBufferCount + 1);
        }
    }

    /// <summary>
    /// Every masked frame takes one buffer, so a packet with more masked frames than the table has room for fills it: the
    /// first frame that finds it full reports the limit by name, the frames before it were parsed, and the frames after it are
    /// not dispatched.
    /// </summary>
    [Test]
    public async Task ManyMaskedFramesFillTheBufferTableAndTheLimitIsReportedOnce()
    {
        const int frames = 256;
        const int tableSize = 255;
        byte[] one = _Frame("a"u8.ToArray(), maskingKey: _MaskingKey);
        List<byte> payload = new(frames * one.Length);
        for (int index = 0; index < frames; index++)
        {
            payload.AddRange(one);
        }

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(_UpgradeFrame([.. payload]));
        using (stack)
        {
            // The transport's buffers use some of the 255 slots; the frames fill what is left and no more.
            int room = tableSize - (_TransportBufferCount - 1);
            await Assert.That(packet.BufferCount).IsEqualTo(1 + tableSize);
            await Assert.That(probe.Observations.Count).IsEqualTo(room).Because("only the frames that got a buffer are dispatched");
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "255").ConfigureAwait(false);
        }
    }

    #endregion

    #region Total size of the bound payloads

    /// <summary>The name of the setting that bounds the total of the unmasked and inflated payloads that WebSocket binds to one packet.</summary>
    private const string _LimitSetting = "ws.max_derived_bytes_per_packet";

    /// <summary>The smallest value the setting accepts (1 KiB); small payloads then reach the total without large allocations.</summary>
    private const ulong _SmallLimit = 2048;

    /// <summary>Length of the Ethernet, IPv4, and TCP headers (no options) in front of the stream bytes of a test frame.</summary>
    private const int _TransportHeaderLength = 14 + 20 + 20;

    /// <summary>
    /// The bytes of <paramref name="frame"/> that the transport binds as buffers before WebSocket sees them (the TCP payload: the
    /// upgrade response and the frames). The packet's sum counts them too, whichever protocol bound them, so a test adds them to
    /// the 1 KiB it means to leave for the unmasked payloads.
    /// </summary>
    private static ulong _TransportBytes(byte[] frame) => (ulong)(frame.Length - _TransportHeaderLength);

    /// <summary>
    /// Builds a stack whose <c>ws.max_derived_bytes_per_packet</c> is <paramref name="limit"/> and parses <paramref name="frame"/> as packet 0.
    /// </summary>
    /// <param name="frame">The Ethernet frame.</param>
    /// <param name="limit">The value of the setting.</param>
    /// <returns>The stack (the caller disposes it), the packet, and the probe that recorded the nested dispatches.</returns>
    private static (Stack Stack, Packet Packet, BoundSliceProbeProtocol Probe) _ParseWithLimit(byte[] frame, ulong limit)
    {
        BoundSliceProbeProtocol probe = new();
        Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, (_LimitSetting, SettingValue.U64(limit + _TransportBytes(frame))));
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        return (stack, packet, probe);
    }

    /// <summary>
    /// One TCP segment that upgrades the connection and carries one masked frame per given size, for the tests that treat the
    /// three protocols alike (<c>DerivedBytesLimitConsistencyTests</c>): each frame binds one unmasked payload of that many bytes.
    /// </summary>
    /// <param name="payloadSizes">The payload size of each frame.</param>
    internal static byte[] FrameWithDerivedPayloads(params int[] payloadSizes) => _UpgradeFrame(_MaskedFrames(payloadSizes));

    /// <summary>Encodes masked text frames of the given payload sizes (zero bytes) back to back, as one WebSocket byte stream.</summary>
    /// <param name="sizes">The payload size of each frame.</param>
    private static byte[] _MaskedFrames(params int[] sizes)
    {
        List<byte> bytes = [];
        foreach (int size in sizes)
        {
            bytes.AddRange(_Frame(new byte[size], maskingKey: _MaskingKey));
        }

        return [.. bytes];
    }

    /// <summary>
    /// Masked payloads whose sum stays at or below the limit are all bound and dispatched, and the packet reports no error;
    /// the sum may equal the limit.
    /// </summary>
    /// <param name="first">Size of the first payload.</param>
    /// <param name="second">Size of the second payload.</param>
    [Test]
    [Arguments(800, 800)]
    [Arguments(1024, 1024)]
    public async Task PayloadsWhoseSumStaysWithinTheLimitAreAllBound(int first, int second)
    {
        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithLimit(_UpgradeFrame(_MaskedFrames(first, second)), _SmallLimit);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 2);
            await Assert.That(probe.Observations.Count).IsEqualTo(2);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The payload that would pass the total is not bound or dispatched, the earlier payloads stay bound, the later frames are
    /// not dispatched, and the packet reports an error that names the setting and says it is the total per packet.
    /// </summary>
    [Test]
    public async Task APayloadThatPassesTheTotalIsRefusedWithTheNamedError()
    {
        // 1000 + 1000 = 2000 fit, +200 passes 2048; the fourth frame fits again but must not be dispatched after the error.
        byte[] frame = _UpgradeFrame(_MaskedFrames(1000, 1000, 200, 20));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithLimit(frame, _SmallLimit);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 2);
            await Assert.That(probe.Observations.Count).IsEqualTo(2).Because("the refused frame and the one after it are not dispatched");
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", _LimitSetting).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "total per packet").ConfigureAwait(false);
            string effectiveTotal = (_SmallLimit + _TransportBytes(frame)).ToString(CultureInfo.InvariantCulture);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", effectiveTotal).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One payload of exactly the limit is bound (the limit is inclusive); one byte more is refused with the same named error.
    /// </summary>
    [Test]
    public async Task ASinglePayloadAtTheLimitIsBoundAndOneByteMoreIsRefused()
    {
        (Stack atLimitStack, Packet atLimit, BoundSliceProbeProtocol atLimitProbe) = _ParseWithLimit(
            _UpgradeFrame(_MaskedFrames((int)_SmallLimit)), _SmallLimit);
        using (atLimitStack)
        {
            await Assert.That(atLimit.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(atLimitProbe.Observations.Count).IsEqualTo(1);
            await ProtocolTestHelper.AssertFieldNotPresent(atLimitStack, atLimit, "packet.error").ConfigureAwait(false);
        }

        (Stack overStack, Packet over, BoundSliceProbeProtocol overProbe) = _ParseWithLimit(
            _UpgradeFrame(_MaskedFrames((int)_SmallLimit + 1)), _SmallLimit);
        using (overStack)
        {
            await Assert.That(over.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(overProbe.Observations.Count).IsEqualTo(0);
            await ProtocolTestHelper.AssertStringFieldContains(overStack, over, "packet.error", _LimitSetting).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inflated payloads count by their inflated size, not their compressed size: two compressed frames that each inflate to
    /// 1200 bytes are far below the limit as sent but pass it together once inflated.
    /// </summary>
    [Test]
    public async Task InflatedPayloadsCountByTheirInflatedSize()
    {
        byte[] compressed = _Deflate(new byte[1200]);
        byte[] frame = _UpgradeFrame([.. _Frame(compressed, compressed: true), .. _Frame(compressed, compressed: true)]);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithLimit(frame, _SmallLimit);
        using (stack)
        {
            await Assert.That(compressed.Length).IsLessThan(100).Because("the frames are tiny as sent");
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(probe.Observations.Count).IsEqualTo(1);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "total per packet").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Unmasked, uncompressed payloads bind nothing, so they do not count: many of them together pass the limit without an
    /// error and without a buffer.
    /// </summary>
    [Test]
    public async Task UnmaskedUncompressedPayloadsAreNotCounted()
    {
        byte[] plain = new byte[1000];
        byte[] frame = _UpgradeFrame([.. _Frame(plain), .. _Frame(plain), .. _Frame(plain)]);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithLimit(frame, _SmallLimit);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(probe.Observations.Count).IsEqualTo(3);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// At the largest value the setting accepts (128 GiB, far above any array) payloads are still bound and no arithmetic
    /// overflows: the check compares a payload with the remaining budget instead of adding to the running sum.
    /// </summary>
    [Test]
    public async Task TheLargestSettingValueBindsPayloadsWithoutOverflow()
    {
        const ulong maxSetting = 137_438_953_472;
        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithLimit(
            _UpgradeFrame(_MaskedFrames(1000, 1000, 1000)), maxSetting);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 3);
            await Assert.That(probe.Observations.Count).IsEqualTo(3);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The per-payload inflate limit and the per-packet total are two settings: with the total at its default, two payloads
    /// that inflate to 1400 bytes each pass <c>ws.max_decompressed_bytes</c> of 2048 one by one and are both bound although their
    /// sum (2800) is above it.
    /// </summary>
    [Test]
    public async Task ThePerPayloadInflateLimitDoesNotBoundThePacketSum()
    {
        byte[] compressed = _Deflate(new byte[1400]);
        byte[] frame = _UpgradeFrame([.. _Frame(compressed, compressed: true), .. _Frame(compressed, compressed: true)]);
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, ("ws.max_decompressed_bytes", SettingValue.U64(2048)));

        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 2);
        await Assert.That(probe.Observations.Count).IsEqualTo(2);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }

    /// <summary>
    /// A payload that inflates beyond the per-payload limit is still left undecoded and flagged by its own field, and the total
    /// is not involved: no packet error, nothing is charged for the payload that was not inflated (it stays the raw frame slice).
    /// </summary>
    [Test]
    public async Task APayloadAboveThePerPayloadLimitStaysUndecodedWithoutATotalError()
    {
        byte[] frame = _UpgradeFrame(_Frame(_Deflate(new byte[4000]), compressed: true));
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(
            probe, ("ws.max_decompressed_bytes", SettingValue.U64(2048)), (_LimitSetting, SettingValue.U64(2048)));

        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
        await Assert.That(probe.Observations.Count).IsEqualTo(1);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldExists(stack, packet, "websocket.payload.decompressed.limit").ConfigureAwait(false);
    }

    /// <summary>
    /// The count belongs to the packet, not to the protocol instance: a packet that fits (two payloads of 800 bytes at a total of
    /// 2048) binds the same buffers again when it is reparsed, because the reparse starts at zero and not at the 1600 bytes of the
    /// first parse; and a packet that used up its total on another stack does not influence it. The refused packet is parsed on a
    /// second stack: the transport tracks the TCP connection, and the same sequence number on one stack would be taken for a
    /// retransmission.
    /// </summary>
    [Test]
    public async Task TheTotalIsCountedPerPacketAndAReparseStartsAtZero()
    {
        byte[] failing = _UpgradeFrame(_MaskedFrames(1400, 1400));
        byte[] fitting = _UpgradeFrame(_MaskedFrames(800, 800));
        BoundSliceProbeProtocol probe = new();
        BoundSliceProbeProtocol otherProbe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, (_LimitSetting, SettingValue.U64(_SmallLimit + _TransportBytes(fitting))));
        using Stack other = BoundSliceProbeProtocol.BuildStackWithSettings(
            otherProbe, (_LimitSetting, SettingValue.U64(_SmallLimit + _TransportBytes(failing))));

        Packet refused = ProtocolTestHelper.ParseFrame(other, failing, 0, Timestamp.FromMillis(0));
        Packet first = ProtocolTestHelper.ParseFrame(stack, fitting, 0, Timestamp.FromMillis(0));
        Packet reparse = ProtocolTestHelper.ParseFrame(stack, fitting, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(other, refused, "packet.error", _LimitSetting).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, first, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
        await Assert.That(reparse.BufferCount).IsEqualTo(_TransportBufferCount + 2);
    }

    /// <summary>
    /// Several threads reparse a packet whose payloads (1800 of 2048 bytes) fit the total at the same time. The count lives on
    /// each packet: if it were shared with another thread or an earlier parse, a later reparse would pass the total and fail
    /// with the refusal.
    /// </summary>
    [Test]
    public async Task ConcurrentReparsesEachKeepTheirOwnTotal()
    {
        const int threads = 8;
        byte[] frame = _UpgradeFrame(_MaskedFrames(1000, 800));
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, (_LimitSetting, SettingValue.U64(_SmallLimit + _TransportBytes(frame))));

        // The first parse records the stream state; the parallel parses are reparses of the same packet, like the other
        // concurrency tests of this class.
        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        int expectedCount = first.BufferCount;

        await Assert.That(expectedCount).IsEqualTo(_TransportBufferCount + 2);
        await Parallel.ForAsync(0, threads, async (_, _) =>
        {
            Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

            await Assert.That(packet.BufferCount).IsEqualTo(expectedCount);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    #endregion

    #region Raw payloads are not bound

    /// <summary>
    /// A payload that is neither masked nor compressed is already a packet slice: no buffer is added and the nested protocol
    /// is handed it as it is.
    /// </summary>
    [Test]
    public async Task UnmaskedUncompressedPayloadAddsNoBufferAndTheNestedProtocolGetsAFrameSlice()
    {
        byte[] frame = _UpgradeFrame(_Frame(_Payload));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue();
            await Assert.That(seen.BufferIndex).IsLessThan(_TransportBufferCount)
                .Because("a payload that needed no decoding is a slice of a buffer the transport delivered");
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Payload)).IsTrue();
        }
    }

    /// <summary>
    /// An unmasked RSV1 frame whose bytes cannot be inflated stays as sent, is dispatched unchanged, and adds no buffer.
    /// </summary>
    [Test]
    public async Task UnmaskedCorruptCompressedPayloadStaysAsSentAndAddsNoBuffer()
    {
        // RSV1 set, but the bytes are not DEFLATE: inflating fails and the payload stays as sent, which for an unmasked
        // frame is still a packet slice.
        byte[] corrupt = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] frame = _UpgradeFrame(_Frame(corrupt, compressed: true));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(probe.Observations.Single().Bytes.AsSpan().SequenceEqual(corrupt)).IsTrue();
        }
    }

    /// <summary>
    /// A masked RSV1 frame whose bytes cannot be inflated is still bound, as the unmasked bytes: the unmasked copy is a fresh
    /// array whether or not inflating works, so the binding condition is "masked or inflated".
    /// </summary>
    [Test]
    public async Task MaskedCorruptCompressedPayloadIsBoundAsTheUnmaskedBytes()
    {
        // The unmasked copy is a fresh array even when inflating fails, so a masked frame binds it either way.
        byte[] corrupt = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] frame = _UpgradeFrame(_Frame(corrupt, maskingKey: _MaskingKey, compressed: true));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(corrupt)).IsTrue();
            await Assert.That(probe.Observations.Single().BufferIndex).IsEqualTo(_TransportBufferCount);
        }
    }

    /// <summary>
    /// Only text and binary frames reach a nested protocol; a masked ping frame is not unmasked for dispatch and takes no
    /// buffer.
    /// </summary>
    [Test]
    public async Task ControlFramesAreNeitherDispatchedNorBound()
    {
        byte[] frame = _UpgradeFrame(_Frame(Encoding.ASCII.GetBytes("ping"), WebSocketOpcode.Ping, _MaskingKey));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(probe.Observations.Count).IsEqualTo(0);
        }
    }

    #endregion

    #region Reparse

    /// <summary>
    /// Parsing the same frame again binds the same bytes once on the new packet and gives an equal field tree and no error.
    /// </summary>
    /// <param name="compressed"><see langword="true"/> for an unmasked RSV1 frame, <see langword="false"/> for a masked plain frame.</param>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReparseBindsTheSameBufferAndGivesTheSameTree(bool compressed)
    {
        byte[] frame = compressed
            ? _UpgradeFrame(_Frame(_Deflate(_Payload), compressed: true))
            : _UpgradeFrame(_Frame(_Payload, maskingKey: _MaskingKey));
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStack(probe);

        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        Packet reparse = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
        for (int index = 0; index < first.BufferCount; index++)
        {
            await Assert.That(ProtocolTestHelper.BufferBytes(reparse, index).AsSpan().SequenceEqual(ProtocolTestHelper.BufferBytes(first, index))).IsTrue()
                .Because($"buffer {index} of the reparse equals the first parse");
        }

        await Assert.That(ProtocolTestHelper.BufferBytes(reparse, first.BufferCount - 1).AsSpan().SequenceEqual(_Payload)).IsTrue();
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await PacketFieldComparer.AssertFieldIdentical(stack, first, reparse).ConfigureAwait(false);
    }

    /// <summary>
    /// Several threads reparse the same frame at once, each into its own packet. The new bind sits on the packet and the
    /// unmasked or inflated bytes are built per call, so nothing may be shared between the threads.
    /// </summary>
    [Test]
    public async Task ConcurrentReparsesEachBindTheirOwnBuffer()
    {
        const int threads = 8;
        byte[] frame = _UpgradeFrame(_Frame(_Payload, maskingKey: _MaskingKey));
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStack(probe);
        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        int expectedCount = first.BufferCount;

        await Parallel.ForAsync(0, threads, async (_, _) =>
        {
            Packet reparse = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

            await Assert.That(reparse.BufferCount).IsEqualTo(expectedCount);
            await Assert.That(ProtocolTestHelper.BufferBytes(reparse, expectedCount - 1).AsSpan().SequenceEqual(_Payload)).IsTrue();
        }).ConfigureAwait(false);
    }

    #endregion
}
