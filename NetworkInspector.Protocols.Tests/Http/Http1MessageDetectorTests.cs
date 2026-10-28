// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using Http1MessageDetector = NetworkInspector.Protocols.Http.Http1MessageDetector;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for <see cref="Http1MessageDetector"/>: message framing of crafted and truncated HTTP/1 byte streams.
/// </summary>
internal sealed class Http1MessageDetectorTests
{
    private static PduBoundaryResult _Detect(string text) => _Detect(Encoding.ASCII.GetBytes(text));

    private static PduBoundaryResult _Detect(byte[] data) => new Http1MessageDetector().Detect(data);

    [Test]
    public async Task DetectStatusLineWithTwoDigitCodeDoesNotThrow()
    {
        // Content-Length: 0 makes the framing explicit: a response without any length would (correctly) run until the
        // connection closes, and this test is about the short status line, not about framing.
        string message = "HTTP/1.1 20\r\nContent-Length: 0\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectStatusLineEndingAfterSpaceDoesNotThrow()
    {
        string message = "HTTP/1.1 \r\nContent-Length: 0\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectStatus204HasNoBody()
    {
        string header = "HTTP/1.1 204 No Content\r\nContent-Length: 5\r\n\r\n";

        PduBoundaryResult result = _Detect(header + "Hello");

        await Assert.That(result.Length).IsEqualTo(header.Length);
    }

    [Test]
    public async Task DetectChunkSizeNearIntMaxIsIncompleteNotException()
    {
        // The chunk end (data offset + 0x7FFFFFFF) does not fit an int; it must be treated as "not buffered yet".
        string message = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n7FFFFFFF\r\nabc";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectChunkedBodyCompletes()
    {
        string message = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r\n0\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectChunkedBodyWithTrailerCompletes()
    {
        string message = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nX-A: 1\r\nX-B: 2\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectChunkedBodyWithUnterminatedTrailerIsIncomplete()
    {
        string message = "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nX-A: 1\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.IsIncomplete).IsTrue();
    }

    [Test]
    public async Task DetectChunkedBodyWithHugeTrailerSectionDoesNotOverflowStack()
    {
        // Many tiny trailer lines must be walked without recursion. The walk runs on a thread with a small stack so a
        // recursive implementation fails here instead of needing millions of lines.
        const int lineCount = 400_000;
        byte[] prefix = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n");
        byte[] data = new byte[prefix.Length + (lineCount * 3) + 2];
        prefix.CopyTo(data, 0);
        int position = prefix.Length;
        for (int index = 0; index < lineCount; index++)
        {
            data[position++] = (byte)'a';
            data[position++] = (byte)'\r';
            data[position++] = (byte)'\n';
        }

        data[position++] = (byte)'\r';
        data[position] = (byte)'\n';

        PduBoundaryResult result = default;
        Thread worker = new(() => result = _Detect(data), 256 * 1024);
        worker.Start();
        worker.Join();

        await Assert.That(result.Length).IsEqualTo(data.Length);
    }

    [Test]
    public async Task DetectBareLfHeaderBlockStopsAtEarliestTerminator()
    {
        // The header block ends with a bare LF LF. The body that follows happens to contain CRLF CRLF; the header end
        // must be the first terminator, so the 4 body bytes (Content-Length) complete the message exactly.
        string message = "POST / HTTP/1.1\nContent-Length: 4\n\n\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectCrlfHeaderBlockStillWins()
    {
        string message = "POST / HTTP/1.1\r\nContent-Length: 2\r\n\r\nab";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    #region Stream-aware framing

    private static StreamDetectionContext _Forward(bool closed = false) => new() { StreamId = 1, IsForward = true, StreamClosed = closed };

    private static StreamDetectionContext _Reverse(bool closed = false) => new() { StreamId = 1, IsForward = false, StreamClosed = closed };

    [Test]
    public async Task HeadResponseWithContentLengthHasNoBody()
    {
        // RFC 9112 section 6.3: a response to HEAD carries the headers of the GET response but never its body. Without the
        // request, the Content-Length of 500 would make the detector wait for 500 bytes that never come.
        Http1MessageDetector detector = new();
        PduBoundaryResult request = detector.Detect("HEAD /a HTTP/1.1\r\nHost: h\r\n\r\n"u8, _Forward());
        string response = "HTTP/1.1 200 OK\r\nContent-Length: 500\r\n\r\n";

        PduBoundaryResult result = detector.Detect(Encoding.ASCII.GetBytes(response), _Reverse());

        await Assert.That(request.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(response.Length);
    }

    [Test]
    public async Task ResponseAfterHeadDoesNotInheritTheHeadFraming()
    {
        // The queue pairs responses with requests in order: HEAD /a, GET /b -> the first response has no body, the second does.
        Http1MessageDetector detector = new();
        _ = detector.Detect("HEAD /a HTTP/1.1\r\n\r\n"u8, _Forward());
        PduBoundaryResult headResponse = detector.Detect("HTTP/1.1 200 OK\r\nContent-Length: 500\r\n\r\n"u8, _Reverse());
        _ = detector.Detect("POST /b HTTP/1.1\r\nContent-Length: 0\r\n\r\n"u8, _Forward());
        byte[] second = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");

        PduBoundaryResult postResponse = detector.Detect(second, _Reverse());

        await Assert.That(headResponse.IsComplete).IsTrue();
        await Assert.That(postResponse.Length).IsEqualTo(second.Length);
    }

    [Test]
    public async Task ConnectAcceptedEndsFraming()
    {
        // A 2xx answer to CONNECT turns the connection into a tunnel: the bytes after the header are not HTTP any more.
        Http1MessageDetector detector = new();
        _ = detector.Detect("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"u8, _Forward());
        string response = "HTTP/1.1 200 Connection established\r\n\r\n";

        PduBoundaryResult accepted = detector.Detect(Encoding.ASCII.GetBytes(response), _Reverse());
        PduBoundaryResult tunneled = detector.Detect([0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00], _Forward());

        await Assert.That(accepted.Length).IsEqualTo(response.Length);
        await Assert.That(tunneled.IsInvalid).IsTrue();
    }

    [Test]
    public async Task InterimResponseDoesNotConsumeTheRequest()
    {
        // 100 Continue is not the answer to the request; the final response still pairs with the HEAD queue entry.
        Http1MessageDetector detector = new();
        _ = detector.Detect("HEAD /a HTTP/1.1\r\n\r\n"u8, _Forward());
        _ = detector.Detect("HTTP/1.1 100 Continue\r\n\r\n"u8, _Reverse());

        PduBoundaryResult finalResponse = detector.Detect("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\n"u8, _Reverse());

        await Assert.That(finalResponse.IsComplete).IsTrue();
    }

    [Test]
    public async Task ResponseWithoutLengthIsIncompleteUntilTheStreamCloses()
    {
        Http1MessageDetector detector = new();
        byte[] data = Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\n\r\nstreamed body");

        PduBoundaryResult open = detector.Detect(data, _Reverse(closed: false));
        PduBoundaryResult closed = detector.Detect(data, _Reverse(closed: true));

        await Assert.That(open.IsIncomplete).IsTrue();
        await Assert.That(closed.Length).IsEqualTo(data.Length);
    }

    [Test]
    public async Task RequestWithoutLengthEndsAtTheHeaderBlock()
    {
        // A request has no body unless it announces one, so it must not wait for the connection to close.
        string request = "POST /submit HTTP/1.1\r\nHost: h\r\n\r\n";

        PduBoundaryResult result = new Http1MessageDetector().Detect(Encoding.ASCII.GetBytes(request), _Forward());

        await Assert.That(result.Length).IsEqualTo(request.Length);
    }

    [Test]
    public async Task GrowthInSmallStepsGivesTheSameResultAsOneCall()
    {
        // The detector keeps a cursor and examines only new bytes. Feeding the message in small steps (as the stream buffer
        // does while segments arrive) must reach exactly the verdict of a single call on the whole message.
        string message = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=1\r\nHello\r\n0\r\nX-T: 1\r\n\r\n";
        byte[] data = Encoding.ASCII.GetBytes(message);
        Http1MessageDetector detector = new();

        PduBoundaryResult last = default;
        bool everyPrefixWaited = true;
        for (int length = 1; length < data.Length; length += 7)
        {
            last = detector.Detect(data.AsSpan(0, length), _Reverse());
            everyPrefixWaited &= last.IsIncomplete;
        }

        PduBoundaryResult whole = detector.Detect(data, _Reverse());

        await Assert.That(everyPrefixWaited).IsTrue();
        await Assert.That(whole.Length).IsEqualTo(data.Length);
    }

    [Test]
    public async Task LoneCrAtTheEndOfAChunkWaitsForItsLf()
    {
        // The chunk is committed only when its line ending is complete: a CR at the end of the buffer must not make the LF
        // that arrives next look like an empty size line (which would be an invalid chunk).
        Http1MessageDetector detector = new();
        byte[] withCr = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r");
        byte[] complete = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r\n0\r\n\r\n");

        PduBoundaryResult waiting = detector.Detect(withCr, _Reverse());
        PduBoundaryResult done = detector.Detect(complete, _Reverse());

        await Assert.That(waiting.IsIncomplete).IsTrue();
        await Assert.That(done.Length).IsEqualTo(complete.Length);
    }

    [Test]
    public async Task JunkAtAMessageStartEndsAtTheNextStartLine()
    {
        // The tail of a dropped message is junk; the next request starts at a line start. The junk is one unit (so it stays
        // visible as "not HTTP") and the request after it is framed normally.
        byte[] data = [0x00, 0x01, 0x02, (byte)'\n', .. "GET /next HTTP/1.1\r\n\r\n"u8];

        PduBoundaryResult junk = new Http1MessageDetector().Detect(data, _Forward());

        await Assert.That(junk.Length).IsEqualTo(4);
    }

    [Test]
    public async Task JunkWithoutALaterStartLineIsOneUnit()
    {
        PduBoundaryResult junk = new Http1MessageDetector().Detect([0x00, 0x01, 0x02], _Forward());

        await Assert.That(junk.Length).IsEqualTo(3);
    }

    [Test]
    public async Task PartialMethodAtTheEndOfJunkWaitsForMoreBytes()
    {
        // "GE" after a line break may become "GET ": the junk ends in front of it and the partial line waits.
        byte[] data = [0x00, (byte)'\n', (byte)'G', (byte)'E'];

        PduBoundaryResult junk = new Http1MessageDetector().Detect(data, _Forward());

        await Assert.That(junk.Length).IsEqualTo(2);
    }

    [Test]
    public async Task ResetStreamForgetsThePendingRequests()
    {
        Http1MessageDetector detector = new();
        _ = detector.Detect("HEAD /a HTTP/1.1\r\n\r\n"u8, _Forward());
        detector.ResetStream(1);
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nbody");

        // After the reset there is no HEAD to pair with, so the Content-Length applies again.
        PduBoundaryResult result = detector.Detect(response, _Reverse());

        await Assert.That(result.Length).IsEqualTo(response.Length);
    }

    #endregion
}
