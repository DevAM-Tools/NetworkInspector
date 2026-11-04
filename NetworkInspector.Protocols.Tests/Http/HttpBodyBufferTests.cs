// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests that a decoded HTTP/1 body (dechunked and/or inflated) is attached to the packet as one additional buffer before
/// the nested dispatch, and that a body which needed no decoding is not. Every frame goes through the full Ethernet, IPv4,
/// TCP, and HTTP stack. A <see cref="BoundSliceProbeProtocol"/> registered under the content type
/// <see cref="BoundSliceProbeProtocol.ContentType"/> applies the check that a stateful nested protocol makes: the bytes it
/// is handed must be a slice of one of the packet's buffers.
/// <para>
/// The number of buffers the transport already delivers is not hard-coded: <see cref="_TransportBufferCount"/> is measured
/// on a request without a body, where HTTP binds nothing, so the expectations stay right if the TCP layer changes what it
/// binds for a single segment.
/// </para>
/// </summary>
internal sealed class HttpBodyBufferTests
{
    /// <summary>The body bytes after decoding that every test expects to find in the new buffer and at the nested protocol.</summary>
    private static readonly byte[] _Decoded = Encoding.ASCII.GetBytes("{\"decoded\":\"body bytes of the message\"}");

    /// <summary>
    /// The buffer count of a packet whose HTTP message has no body: only what the transport delivers, because HTTP binds
    /// nothing without a body. Measured once per test run.
    /// </summary>
    private static readonly Lazy<int> _TransportBufferCountValue = new(() =>
    {
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStack(probe);
        Packet packet = ProtocolTestHelper.ParseFrame(
            stack, HttpHardeningTests.BuildHttpFrame(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n")), 0, Timestamp.FromMillis(0));
        return packet.BufferCount;
    });

    /// <summary>The number of buffers (frame included) that the transport delivers for a single-segment HTTP message.</summary>
    private static int _TransportBufferCount => _TransportBufferCountValue.Value;

    #region Message building

    /// <summary>Encodes <paramref name="chunks"/> as a chunked body with the terminal chunk (RFC 9112 section 7.1).</summary>
    private static byte[] _Chunked(params byte[][] chunks)
    {
        using MemoryStream output = new();
        foreach (byte[] chunk in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(chunk.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"));
            output.Write(chunk);
            output.Write("\r\n"u8);
        }

        output.Write("0\r\n\r\n"u8);
        return output.ToArray();
    }

    /// <summary>Compresses <paramref name="plain"/> as one gzip member (RFC 1952).</summary>
    private static byte[] _Gzip(byte[] plain)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(plain);
        }

        return output.ToArray();
    }

    /// <summary>Builds a frame whose TCP payload is a 200 response with the given header lines and body.</summary>
    /// <param name="headerLines">Header lines, each ended by CRLF.</param>
    /// <param name="body">The body bytes exactly as sent.</param>
    private static byte[] _ResponseFrame(string headerLines, byte[] body) => HttpHardeningTests.BuildHttpFrame(_ResponseBytes(headerLines, body));

    /// <summary>The bytes of one 200 response (head and body) as they travel in the TCP payload; several of them make one segment.</summary>
    /// <param name="headerLines">Header lines, each ended by CRLF.</param>
    /// <param name="body">The body bytes exactly as sent.</param>
    private static byte[] _ResponseBytes(string headerLines, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + headerLines + "\r\n");
        return [.. head, .. body];
    }

    /// <summary>The <c>Content-Type</c> header line (with CRLF) that routes the body to the probe protocol.</summary>
    private static string _ProbeType => "Content-Type: " + BoundSliceProbeProtocol.ContentType + "\r\n";

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

    #region Decoded bodies are bound

    /// <summary>
    /// A chunked body is bound once, after the transport's buffers, with the dechunked bytes; the nested protocol is handed
    /// exactly that bound slice (a packet slice in the new buffer).
    /// </summary>
    [Test]
    public async Task ChunkedBodyIsBoundAsOneMoreBufferAndReachesTheNestedProtocolAsThatSlice()
    {
        byte[] frame = _ResponseFrame(_ProbeType + "Transfer-Encoding: chunked\r\n", _Chunked(_Decoded[..10], _Decoded[10..]));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Decoded)).IsTrue()
                .Because("the new buffer holds the dechunked body");

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue().Because("the nested protocol must get a packet slice");
            await Assert.That(seen.BufferIndex).IsEqualTo(_TransportBufferCount);
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Decoded)).IsTrue();
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The built-in JSON fallback dispatch receives the bound slice as well: a chunked JSON body adds one buffer and the
    /// <c>json</c> protocol is present.
    /// </summary>
    [Test]
    public async Task ChunkedJsonBodyIsBoundAndParsedByTheJsonProtocol()
    {
        byte[] frame = _ResponseFrame(
            "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\n", _Chunked(_Decoded[..7], _Decoded[7..]));

        (Stack stack, Packet packet, _) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Decoded)).IsTrue();
            await ProtocolTestHelper.AssertProtocolPresent(stack, packet, "json").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A gzip body is bound once with the inflated bytes, not the compressed ones, and the nested protocol gets that slice.
    /// </summary>
    [Test]
    public async Task GzipBodyIsBoundAsOneMoreBufferAndReachesTheNestedProtocolAsThatSlice()
    {
        byte[] compressed = _Gzip(_Decoded);
        byte[] frame = _ResponseFrame(
            _ProbeType + "Content-Encoding: gzip\r\nContent-Length: " + compressed.Length.ToString(CultureInfo.InvariantCulture) + "\r\n",
            compressed);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Decoded)).IsTrue()
                .Because("the new buffer holds the inflated body, not the compressed bytes");

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue();
            await Assert.That(seen.BufferIndex).IsEqualTo(_TransportBufferCount);
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Decoded)).IsTrue();
        }
    }

    /// <summary>
    /// Dechunking and inflating are two steps but one decoded body: only the final bytes become a buffer.
    /// </summary>
    [Test]
    public async Task GzipBodyInsideAChunkedBodyAddsOneBufferNotTwo()
    {
        byte[] frame = _ResponseFrame(
            _ProbeType + "Transfer-Encoding: chunked\r\nContent-Encoding: gzip\r\n", _Chunked(_Gzip(_Decoded)));

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            // The dechunked-but-still-compressed bytes are an intermediate result; only the final decoded body is bound.
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount + 1);
            await Assert.That(ProtocolTestHelper.BufferBytes(packet, _TransportBufferCount).AsSpan().SequenceEqual(_Decoded)).IsTrue();
            await Assert.That(probe.Observations.Single().BufferIndex).IsEqualTo(_TransportBufferCount);
        }
    }

    #endregion

    #region Raw bodies are not bound

    /// <summary>
    /// A body that needed no decoding is already a packet slice: no buffer is added and the nested protocol is handed it as
    /// it is.
    /// </summary>
    [Test]
    public async Task PlainBodyAddsNoBufferAndTheNestedProtocolGetsAFrameSlice()
    {
        byte[] frame = _ResponseFrame(
            _ProbeType + "Content-Length: " + _Decoded.Length.ToString(CultureInfo.InvariantCulture) + "\r\n", _Decoded);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);

            BoundSliceProbeProtocol.Observation seen = probe.Observations.Single();
            await Assert.That(seen.IsPacketSlice).IsTrue();
            await Assert.That(seen.BufferIndex).IsLessThan(_TransportBufferCount)
                .Because("a body that needed no decoding is a slice of a buffer the transport delivered");
            await Assert.That(seen.Bytes.AsSpan().SequenceEqual(_Decoded)).IsTrue();
        }
    }

    /// <summary>
    /// A gzip body that cannot be inflated stays as sent, is dispatched unchanged, and adds no buffer.
    /// </summary>
    [Test]
    public async Task CorruptGzipBodyStaysUndecodedAndAddsNoBuffer()
    {
        // Bytes without the gzip magic: inflating fails, the body stays as sent and is dispatched as the packet slice it is.
        byte[] corrupt = [1, 2, 3, 4, 5, 6];
        byte[] frame = _ResponseFrame(
            _ProbeType + "Content-Encoding: gzip\r\nContent-Length: " + corrupt.Length.ToString(CultureInfo.InvariantCulture) + "\r\n",
            corrupt);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(probe.Observations.Single().Bytes.AsSpan().SequenceEqual(corrupt)).IsTrue();
        }
    }

    /// <summary>
    /// An empty decoded body has nothing to parse: nothing is bound, nothing is dispatched, and no error is reported.
    /// </summary>
    [Test]
    public async Task ChunkedBodyOfOnlyTheTerminalChunkDispatchesNothingAndAddsNoBuffer()
    {
        byte[] frame = _ResponseFrame(_ProbeType + "Transfer-Encoding: chunked\r\n", _Chunked());

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _Parse(frame);
        using (stack)
        {
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await Assert.That(probe.Observations.Count).IsEqualTo(0).Because("an empty decoded body has nothing to parse");
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    #endregion

    #region Total size of the bound bodies

    /// <summary>The setting that bounds the total of the decoded bodies that HTTP binds to one packet.</summary>
    private const string _TotalSetting = "http.max_derived_bytes_per_packet";

    /// <summary>The smallest value the settings accept (1 KiB); small bodies then reach the limits without large allocations.</summary>
    private const ulong _SmallLimit = 2048;

    /// <summary>Length of the Ethernet, IPv4, and TCP headers (no options) in front of the HTTP bytes of a test frame.</summary>
    private const int _TransportHeaderLength = 14 + 20 + 20;

    /// <summary>
    /// The total that lets the decoded bodies of <paramref name="frame"/> count up to <see cref="_SmallLimit"/> bytes: the packet's
    /// sum also holds the transport buffers (every HTTP message of the frame is bound by TCP as one buffer before it is parsed),
    /// whichever protocol bound them, so the test adds their length (the HTTP bytes of the frame) to the 1 KiB that it means to
    /// leave for the bodies.
    /// </summary>
    private static ulong _TotalFor(byte[] frame) => _SmallLimit + (ulong)(frame.Length - _TransportHeaderLength);

    /// <summary>A gzip response whose body inflates to <paramref name="inflatedSize"/> zero bytes, routed to the probe protocol.</summary>
    private static byte[] _GzipResponseBytes(int inflatedSize)
    {
        byte[] compressed = _Gzip(new byte[inflatedSize]);
        return _ResponseBytes(
            _ProbeType + "Content-Encoding: gzip\r\nContent-Length: " + compressed.Length.ToString(CultureInfo.InvariantCulture) + "\r\n",
            compressed);
    }

    /// <summary>One segment that completes one gzip response per given size, in order.</summary>
    private static byte[] _SegmentOfGzipResponses(params int[] inflatedSizes)
    {
        List<byte> bytes = [];
        foreach (int size in inflatedSizes)
        {
            bytes.AddRange(_GzipResponseBytes(size));
        }

        return HttpHardeningTests.BuildHttpFrame([.. bytes]);
    }

    /// <summary>
    /// One TCP segment that completes one gzip response per given size, for the tests that treat the three protocols alike
    /// (<c>DerivedBytesLimitConsistencyTests</c>): each response binds one decoded body of that many bytes.
    /// </summary>
    /// <param name="inflatedSizes">The decoded size of each body.</param>
    internal static byte[] FrameWithDerivedBodies(params int[] inflatedSizes) => _SegmentOfGzipResponses(inflatedSizes);

    /// <summary>Parses <paramref name="frame"/> as packet 0 on a stack with the given settings.</summary>
    private static (Stack Stack, Packet Packet, BoundSliceProbeProtocol Probe) _ParseWithSettings(byte[] frame, params (string Name, ulong Value)[] settings)
    {
        BoundSliceProbeProtocol probe = new();
        (string Name, SettingValue Value)[] preloaded = [.. settings.Select(static s => (s.Name, SettingValue.U64(s.Value)))];
        Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, preloaded);
        Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        return (stack, packet, probe);
    }

    /// <summary>How many buffers of the packet are exactly <paramref name="length"/> bytes long (the decoded bodies of these tests).</summary>
    private static int _BuffersOfLength(Packet packet, int length)
    {
        int count = 0;
        for (int index = 1; index < packet.BufferCount; index++)
        {
            if (ProtocolTestHelper.BufferBytes(packet, index).Length == length)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// One segment completes three gzip responses of 1400 inflated bytes each; the per-body limit is 2048, so each body passes
    /// on its own, and the total is 2048: the first body is bound and dispatched, the second would pass the total and gets the
    /// named error, and nothing is dispatched after the error. Exactly one 1400-byte buffer is bound: the refused body left no
    /// partial state behind.
    /// </summary>
    [Test]
    public async Task SeveralInflatedBodiesOfOnePacketAreBoundedByTheTotal()
    {
        byte[] frame = _SegmentOfGzipResponses(1400, 1400, 1400);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithSettings(
            frame, ("http.max_decompressed_bytes", _SmallLimit), (_TotalSetting, _TotalFor(frame)));
        using (stack)
        {
            await Assert.That(probe.Observations.Count).IsEqualTo(1).Because("1400 fits, 2800 passes the total, and nothing follows the error");
            await Assert.That(probe.Observations.Single().Bytes.Length).IsEqualTo(1400);
            await Assert.That(_BuffersOfLength(packet, 1400)).IsEqualTo(1);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", _TotalSetting).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", "total per packet").ConfigureAwait(false);
            string effectiveTotal = _TotalFor(frame).ToString(CultureInfo.InvariantCulture);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", effectiveTotal).ConfigureAwait(false);
        }
    }

    /// <summary>With the total at its relaxed default the same three responses are all bound: the total is the only thing that stopped them.</summary>
    [Test]
    public async Task TheRelaxedDefaultTotalBindsTheSameThreeBodies()
    {
        byte[] frame = _SegmentOfGzipResponses(1400, 1400, 1400);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithSettings(frame, ("http.max_decompressed_bytes", _SmallLimit));
        using (stack)
        {
            await Assert.That(probe.Observations.Count).IsEqualTo(3);
            await Assert.That(_BuffersOfLength(packet, 1400)).IsEqualTo(3);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }
    }

    /// <summary>The sum may equal the total (two bodies of 1024 at 2048); one byte more is refused.</summary>
    /// <param name="first">Inflated size of the first body.</param>
    /// <param name="second">Inflated size of the second body.</param>
    /// <param name="bound">How many bodies are bound and dispatched.</param>
    [Test]
    [Arguments(1024, 1024, 2)]
    [Arguments(1024, 1025, 1)]
    [Arguments(2048, 1, 1)]
    public async Task TheTotalIsInclusive(int first, int second, int bound)
    {
        byte[] frame = _SegmentOfGzipResponses(first, second);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithSettings(frame, (_TotalSetting, _TotalFor(frame)));
        using (stack)
        {
            await Assert.That(probe.Observations.Count).IsEqualTo(bound);
            if (bound == 2)
            {
                await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
            }
            else
            {
                await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", _TotalSetting).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A packet whose second body was refused by the total shows the same error and the same buffers when it is parsed again
    /// (the same packet id on the same stack is a replay). The TCP layer must record the PDUs of a first parse that ended in a
    /// nested error; without them the replay dispatched nothing and the error vanished from every later view of the packet.
    /// </summary>
    [Test]
    public async Task AReparseOfARefusedPacketShowsTheSameErrorAndBuffers()
    {
        byte[] frame = _SegmentOfGzipResponses(1400, 1400, 1400);
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(
            probe, ("http.max_decompressed_bytes", SettingValue.U64(_SmallLimit)), (_TotalSetting, SettingValue.U64(_TotalFor(frame))));

        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        Packet reparse = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(stack, first, "packet.error", _TotalSetting).ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, reparse, "packet.error", _TotalSetting).ConfigureAwait(false);
        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
        await Assert.That(_BuffersOfLength(reparse, 1400)).IsEqualTo(1);
        await PacketFieldComparer.AssertFieldIdentical(stack, first, reparse).ConfigureAwait(false);
    }

    /// <summary>
    /// The total is independent of the per-body limit: with the per-body limit at its default, one body that inflates above the
    /// total is refused with the total's error, and the packet keeps no buffer for it.
    /// </summary>
    [Test]
    public async Task ABodyAboveTheTotalIsRefusedEvenWhenThePerBodyLimitAllowsIt()
    {
        byte[] frame = _SegmentOfGzipResponses(4000);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithSettings(frame, (_TotalSetting, _TotalFor(frame)));
        using (stack)
        {
            await Assert.That(probe.Observations.Count).IsEqualTo(0);
            await Assert.That(_BuffersOfLength(packet, 4000)).IsEqualTo(0);
            await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "packet.error", _TotalSetting).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The per-body limit is unchanged by the total: a body that inflates above it stays undecoded and is flagged by its own
    /// field, and no total error is reported (nothing was bound for it).
    /// </summary>
    [Test]
    public async Task ABodyAboveThePerBodyLimitStaysUndecodedWithoutATotalError()
    {
        byte[] frame = _SegmentOfGzipResponses(4000);

        (Stack stack, Packet packet, BoundSliceProbeProtocol probe) = _ParseWithSettings(
            frame, ("http.max_decompressed_bytes", _SmallLimit), (_TotalSetting, _TotalFor(frame)));
        using (stack)
        {
            await Assert.That(probe.Observations.Count).IsEqualTo(1).Because("the undecoded body is dispatched as the frame slice it is");
            await Assert.That(packet.BufferCount).IsEqualTo(_TransportBufferCount);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.error.decompress_limit").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The count belongs to the packet: a packet that fits (two bodies of 800 bytes at a total of 2048) binds the same buffers
    /// again when it is reparsed, because the reparse starts at zero and not at the 1600 bytes of the first parse; and a packet
    /// that used up its total on another stack does not influence it. The refused packet is parsed on a second stack: HTTP
    /// tracks the TCP connection, and the same sequence number on one stack would be taken for a retransmission.
    /// </summary>
    [Test]
    public async Task TheTotalIsCountedPerPacketAndAReparseStartsAtZero()
    {
        byte[] failing = _SegmentOfGzipResponses(1400, 1400);
        byte[] fitting = _SegmentOfGzipResponses(800, 800);
        BoundSliceProbeProtocol probe = new();
        BoundSliceProbeProtocol otherProbe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, (_TotalSetting, SettingValue.U64(_TotalFor(fitting))));
        using Stack other = BoundSliceProbeProtocol.BuildStackWithSettings(otherProbe, (_TotalSetting, SettingValue.U64(_TotalFor(failing))));

        Packet refused = ProtocolTestHelper.ParseFrame(other, failing, 0, Timestamp.FromMillis(0));
        Packet first = ProtocolTestHelper.ParseFrame(stack, fitting, 0, Timestamp.FromMillis(0));
        Packet reparse = ProtocolTestHelper.ParseFrame(stack, fitting, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringFieldContains(other, refused, "packet.error", _TotalSetting).ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, first, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await Assert.That(_BuffersOfLength(reparse, 800)).IsEqualTo(2);
        await Assert.That(reparse.BufferCount).IsEqualTo(first.BufferCount);
    }

    /// <summary>
    /// Several threads reparse a segment whose bodies (800 and 800 of 2048 bytes) fit the total at the same time. The count lives
    /// on each packet; if it were shared between threads or parses, a later reparse would pass the total and be refused.
    /// </summary>
    [Test]
    public async Task ConcurrentReparsesEachKeepTheirOwnTotal()
    {
        const int threads = 8;
        byte[] frame = _SegmentOfGzipResponses(800, 800);
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStackWithSettings(probe, (_TotalSetting, SettingValue.U64(_TotalFor(frame))));
        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        int expectedCount = first.BufferCount;

        await Parallel.ForAsync(0, threads, async (_, _) =>
        {
            Packet packet = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

            await Assert.That(packet.BufferCount).IsEqualTo(expectedCount);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    #endregion

    #region Reparse

    /// <summary>
    /// Parsing the same frame again binds the same bytes once on the new packet and gives an equal field tree and no error.
    /// </summary>
    /// <param name="gzip"><see langword="true"/> for a gzip body inside a chunked body, <see langword="false"/> for a plain chunked body.</param>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReparseBindsTheSameBufferAndGivesTheSameTree(bool gzip)
    {
        byte[] frame = gzip
            ? _ResponseFrame(_ProbeType + "Transfer-Encoding: chunked\r\nContent-Encoding: gzip\r\n", _Chunked(_Gzip(_Decoded)))
            : _ResponseFrame(_ProbeType + "Transfer-Encoding: chunked\r\n", _Chunked(_Decoded[..9], _Decoded[9..]));
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

        await Assert.That(ProtocolTestHelper.BufferBytes(reparse, first.BufferCount - 1).AsSpan().SequenceEqual(_Decoded)).IsTrue();
        await ProtocolTestHelper.AssertFieldNotPresent(stack, reparse, "packet.error").ConfigureAwait(false);
        await PacketFieldComparer.AssertFieldIdentical(stack, first, reparse).ConfigureAwait(false);
    }

    /// <summary>
    /// Several threads reparse the same message at once, each into its own packet. The new bind sits on the packet and the
    /// decoded bytes are built per call, so nothing may be shared between the threads.
    /// </summary>
    [Test]
    public async Task ConcurrentReparsesEachBindTheirOwnBuffer()
    {
        const int threads = 8;
        byte[] frame = _ResponseFrame(_ProbeType + "Transfer-Encoding: chunked\r\n", _Chunked(_Decoded[..9], _Decoded[9..]));
        BoundSliceProbeProtocol probe = new();
        using Stack stack = BoundSliceProbeProtocol.BuildStack(probe);
        Packet first = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));
        int expectedCount = first.BufferCount;

        await Parallel.ForAsync(0, threads, async (_, _) =>
        {
            Packet reparse = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

            await Assert.That(reparse.BufferCount).IsEqualTo(expectedCount);
            await Assert.That(ProtocolTestHelper.BufferBytes(reparse, expectedCount - 1).AsSpan().SequenceEqual(_Decoded)).IsTrue();
        }).ConfigureAwait(false);
    }

    #endregion
}
