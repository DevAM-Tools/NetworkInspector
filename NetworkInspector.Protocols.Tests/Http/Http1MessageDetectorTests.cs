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
        string message = "HTTP/1.1 20\r\n\r\n";

        PduBoundaryResult result = _Detect(message);

        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Length).IsEqualTo(message.Length);
    }

    [Test]
    public async Task DetectStatusLineEndingAfterSpaceDoesNotThrow()
    {
        string message = "HTTP/1.1 \r\n\r\n";

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
}
