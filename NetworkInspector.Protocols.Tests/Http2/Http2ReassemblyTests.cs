// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Http2FrameDetector = NetworkInspector.Protocols.Http2.Http2FrameDetector;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for HTTP/2 over TCP: frames that span segments are parsed whole, header blocks are decoded against the
/// connection's dynamic table, blocks that continue in CONTINUATION frames are shown on the frame that completes them, and
/// the frame boundary detector rejects bytes that are not HTTP/2.
/// </summary>
/// <remarks>
/// The conversations go through the whole stack on the HTTP/2 port with the segmentation chosen by the test. Thread safety:
/// every test owns its stack.
/// </remarks>
internal sealed class Http2ReassemblyTests
{
    #region Helpers

    private const byte _Headers = Http2PayloadBuilder.FrameType.Headers;
    private const byte _Continuation = Http2PayloadBuilder.FrameType.Continuation;
    private const byte _EndHeaders = 0x04;

    /// <summary>Literal header field with incremental indexing and plain (non-Huffman) strings, names and values below 127 bytes.</summary>
    private static byte[] _LiteralIndexed(string name, string value) =>
    [
        0x40,
        (byte)name.Length, .. Encoding.ASCII.GetBytes(name),
        (byte)value.Length, .. Encoding.ASCII.GetBytes(value),
    ];

    private static byte[] _HeadersFrame(uint streamId, byte flags, byte[] block) =>
        Http2PayloadBuilder.BuildFrame(_Headers, flags, streamId, block);

    private static async Task _AssertNoPacketError(Stack stack, Packet[] packets)
    {
        foreach (Packet packet in packets)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    private static TcpTestConversation _Conversation() => new(serverPort: 8443);

    #endregion

    #region Frames across segments

    [Test]
    public async Task DataFrameAcrossSegmentsIsOneFrame()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[3000]);
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(frame, mss: 1000);

        Packet[] packets = conversation.ParseAll(stack);

        // The 3009-byte frame covers four segments: the first three show nothing, the completing one shows the whole frame.
        await ProtocolTestHelper.AssertProtocolNotPresent(stack, packets[4], "http2").ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "http2.frame.length", 3000).ConfigureAwait(false);
        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
    }

    [Test]
    public async Task PrefaceIsAUnitOfItsOwnAndShowsTheMagic()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);

        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertFieldExists(stack, packets[^1], "http2.magic").ConfigureAwait(false);
    }

    [Test]
    public async Task FourByteTailSegmentProducesNoError()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[500]);
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);

        // Cut so that the last segment holds 4 bytes: shorter than a frame header.
        conversation.ClientSend(frame, mss: frame.Length - 4);

        Packet[] packets = conversation.ParseAll(stack);

        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, packets[^1], "http2.frame.length", 500).ConfigureAwait(false);
    }

    #endregion

    #region HPACK across frames

    [Test]
    public async Task DynamicTableHeaderResolvesInSecondHeadersFrame()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(_HeadersFrame(1, _EndHeaders, _LiteralIndexed("x-key", "x-val")));

        // The second request refers to the entry the first one added (index 62 = the newest dynamic entry).
        conversation.ClientSend(_HeadersFrame(3, _EndHeaders, [0x80 | 62]));
        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "http2.header.name", "x-key").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "http2.header.value", "x-val").ConfigureAwait(false);
    }

    [Test]
    public async Task EachDirectionHasItsOwnDynamicTable()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(_HeadersFrame(1, _EndHeaders, _LiteralIndexed("x-client", "c")));

        // The server's table is empty: index 62 on the server side does not resolve to the client's entry.
        conversation.ServerSend(_HeadersFrame(1, _EndHeaders, [0x80 | 62]));
        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^1], "http2.header").ConfigureAwait(false);
    }

    [Test]
    public async Task ReparseOfTheSecondHeadersFrameIsIdentical()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(_HeadersFrame(1, _EndHeaders, _LiteralIndexed("x-key", "x-val")));
        conversation.ClientSend(_HeadersFrame(3, _EndHeaders, [0x80 | 62]));
        Packet[] packets = conversation.ParseAll(stack);
        int lastId = packets.Length - 1;

        // A replay must not touch the decoders (they have moved on) and must show the same headers.
        Packet reparse = ProtocolTestHelper.ParseFrame(stack, conversation.Frames[lastId], lastId, Timestamp.FromMillis(lastId));

        await PacketFieldComparer.AssertFieldIdentical(stack, packets[lastId], reparse).ConfigureAwait(false);
    }

    [Test]
    public async Task ConcurrentReparseOfAHeadersFrameIsIdentical()
    {
        const int readers = 4;
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(_HeadersFrame(1, _EndHeaders, _LiteralIndexed("x-key", "x-val")));
        conversation.ClientSend(_HeadersFrame(3, _EndHeaders, [0x80 | 62]));
        Packet[] packets = conversation.ParseAll(stack);
        int lastId = packets.Length - 1;
        PacketFieldComparer.PacketFieldSnapshot reference = PacketFieldComparer.CaptureFields(packets[lastId]);

        await Parallel.ForAsync(0, readers, async (_, _) =>
        {
            Packet reparse = ProtocolTestHelper.ParseFrame(stack, conversation.Frames[lastId], lastId, Timestamp.FromMillis(lastId));
            await PacketFieldComparer.AssertMatchesSnapshot(stack, reference, reparse);
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task HeaderBlockContinuedInContinuationIsShownOnTheLastFrame()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);

        // One block of two literals, cut between them: HEADERS (no END_HEADERS) carries the first, CONTINUATION the second.
        conversation.ClientSend(_HeadersFrame(1, 0, _LiteralIndexed("x-one", "1")));
        conversation.ClientSend(Http2PayloadBuilder.BuildFrame(_Continuation, _EndHeaders, 1, _LiteralIndexed("x-two", "2")));
        Packet[] packets = conversation.ParseAll(stack);

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packets[^2], "http2.header").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "http2.header.name", "x-one").ConfigureAwait(false);
    }

    [Test]
    public async Task ContinuationFloodBeyondThePendingLimitDropsTheBlockAndKeepsTheConnection()
    {
        // A HEADERS frame without END_HEADERS followed by more CONTINUATION payload than the dissector collects: the block is
        // dropped, no error is raised, and the next block on the connection decodes normally.
        using Stack stack = ProtocolTestHelper.BuildStack();
        TcpTestConversation conversation = _Conversation();
        conversation.Handshake();
        conversation.ClientSend(Http2FrameDetectorTestData.Preface);
        conversation.ClientSend(_HeadersFrame(1, 0, [0x00]));
        byte[] filler = new byte[16_000];
        for (int index = 0; index < 20; index++)
        {
            conversation.ClientSend(Http2PayloadBuilder.BuildFrame(_Continuation, 0, 1, filler));
        }

        conversation.ClientSend(Http2PayloadBuilder.BuildFrame(_Continuation, _EndHeaders, 1, [0x00]));
        conversation.ClientSend(_HeadersFrame(3, _EndHeaders, _LiteralIndexed("x-after", "ok")));
        Packet[] packets = conversation.ParseAll(stack);

        await _AssertNoPacketError(stack, packets).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringField(stack, packets[^1], "http2.header.name", "x-after").ConfigureAwait(false);
    }

    #endregion

    #region Frame boundary detector

    private static PduBoundaryResult _Detect(byte[] data) => new Http2FrameDetector().Detect(data);

    [Test]
    public async Task DetectorFramesThePrefaceAsAUnit()
    {
        byte[] data = [.. Http2FrameDetectorTestData.Preface, 0x00, 0x00];

        PduBoundaryResult result = _Detect(data);

        await Assert.That(result.Length).IsEqualTo(Http2FrameDetectorTestData.Preface.Length);
    }

    [Test]
    public async Task DetectorWaitsWhileThePrefaceIsIncomplete()
    {
        // 15 bytes of the preface would be read as a frame header with length 0x505249 if it were not recognized as a prefix.
        PduBoundaryResult result = _Detect(Http2FrameDetectorTestData.Preface[..15]);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectorFramesAFrameAndWaitsForItsPayload()
    {
        byte[] frame = Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[100]);

        PduBoundaryResult whole = _Detect(frame);
        PduBoundaryResult partial = _Detect(frame[..^1]);

        await Assert.That(whole.Length).IsEqualTo(frame.Length);
        await Assert.That(partial.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectorRejectsBytesThatLookLikeAnEncryptedRecord()
    {
        // A TLS record header read as a frame header gives a "length" of about 1.4 MiB. It must not be buffered as a frame.
        PduBoundaryResult tlsRecord = _Detect([0x17, 0x03, 0x03, 0x00, 0x20, 0x00, 0x00, 0x00, 0x01, 0xAA, 0xBB]);
        PduBoundaryResult oversized = _Detect(Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[16_385]));
        PduBoundaryResult unknownType = _Detect([0x00, 0x00, 0x00, 0x7F, 0x00, 0x00, 0x00, 0x00, 0x01]);

        await Assert.That(tlsRecord.IsInvalid).IsTrue();
        await Assert.That(oversized.IsInvalid).IsTrue();
        await Assert.That(unknownType.IsInvalid).IsTrue();
    }

    [Test]
    public async Task AnnouncedMaxFrameSizeRaisesTheLimitForTheOtherDirection()
    {
        // The server announces SETTINGS_MAX_FRAME_SIZE = 65536: the CLIENT may then send frames up to that size. Frames of the
        // server's own direction stay at the default limit.
        Http2FrameDetector detector = new();
        StreamDetectionContext server = new() { StreamId = 7, IsForward = false };
        StreamDetectionContext client = new() { StreamId = 7, IsForward = true };
        byte[] settings = Http2PayloadBuilder.BuildFrame(
            Http2PayloadBuilder.FrameType.Settings, 0, 0, Http2PayloadBuilder.BuildSettingsBody((5, 65_536)));
        byte[] bigFrame = Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[40_000]);

        PduBoundaryResult beforeAnnouncement = detector.Detect(bigFrame, client);
        PduBoundaryResult settingsResult = detector.Detect(settings, server);
        PduBoundaryResult clientAfter = detector.Detect(bigFrame, client);
        PduBoundaryResult serverAfter = detector.Detect(bigFrame, server);

        await Assert.That(beforeAnnouncement.IsInvalid).IsTrue();
        await Assert.That(settingsResult.Length).IsEqualTo(settings.Length);
        await Assert.That(clientAfter.Length).IsEqualTo(bigFrame.Length);
        await Assert.That(serverAfter.IsInvalid).IsTrue();
    }

    [Test]
    public async Task ResetStreamForgetsTheAnnouncedLimit()
    {
        Http2FrameDetector detector = new();
        StreamDetectionContext client = new() { StreamId = 9, IsForward = true };
        byte[] settings = Http2PayloadBuilder.BuildFrame(
            Http2PayloadBuilder.FrameType.Settings, 0, 0, Http2PayloadBuilder.BuildSettingsBody((5, 65_536)));
        _ = detector.Detect(settings, new StreamDetectionContext { StreamId = 9, IsForward = false });
        detector.ResetStream(9);

        PduBoundaryResult result = detector.Detect(
            Http2PayloadBuilder.BuildFrame(Http2PayloadBuilder.FrameType.Data, 0, 1, new byte[40_000]), client);

        await Assert.That(result.IsInvalid).IsTrue();
    }

    #endregion
}

/// <summary>Constants shared by the HTTP/2 reassembly tests.</summary>
internal static class Http2FrameDetectorTestData
{
    /// <summary>The 24-octet client connection preface.</summary>
    internal static byte[] Preface { get; } = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();
}
