// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="BoundedReadStream"/>: the size cap of settings and configuration loads holds for the bytes
/// actually read, whatever length the file system reported before the read.
/// </summary>
internal sealed class SettingsFileAccessTests
{
    /// <summary>An endless stream: every read fills the buffer, the reported length is 0 (like a pipe).</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static int _ReadToEnd(Stream stream)
    {
        byte[] buffer = new byte[4096];
        int total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }

        return total;
    }

    [Test]
    public async Task BoundedReadStreamExactlyTheCapIsAccepted()
    {
        using MemoryStream inner = new(new byte[2048]);
        using BoundedReadStream bounded = new(inner, 2048);

        int delivered = _ReadToEnd(bounded);

        await Assert.That(delivered).IsEqualTo(2048);
        await Assert.That(bounded.Exceeded).IsFalse();
    }

    [Test]
    public async Task BoundedReadStreamMoreBytesThanTheCapStopsOnePastTheCap()
    {
        using MemoryStream inner = new(new byte[5000]);
        using BoundedReadStream bounded = new(inner, 2048);

        int delivered = _ReadToEnd(bounded);

        await Assert.That(delivered).IsEqualTo(2049);
        await Assert.That(bounded.Exceeded).IsTrue();
    }

    [Test]
    public async Task BoundedReadStreamEndlessInputUnderACapIsExceeded()
    {
        using EndlessStream inner = new();
        using BoundedReadStream bounded = new(inner, 100_000);

        int delivered = _ReadToEnd(bounded);

        await Assert.That(delivered).IsEqualTo(100_001);
        await Assert.That(bounded.Exceeded).IsTrue();
    }

    [Test]
    public async Task BoundedReadStreamLongMaxValueCapDoesNotOverflow()
    {
        using MemoryStream inner = new(new byte[10]);
        using BoundedReadStream bounded = new(inner, long.MaxValue);

        int delivered = _ReadToEnd(bounded);

        await Assert.That(delivered).IsEqualTo(10);
        await Assert.That(bounded.Exceeded).IsFalse();
    }

    [Test]
    public async Task BoundedReadStreamCutJsonDocumentFailsToParse()
    {
        // Two bytes over the cap: the parser sees a truncated input (the loads then report the size, not the syntax).
        byte[] json = Encoding.UTF8.GetBytes("[" + string.Concat(Enumerable.Repeat("0, ", 10_000)) + "0]");
        using MemoryStream inner = new(json);
        using BoundedReadStream bounded = new(inner, json.Length - 2);

        await Assert.That(() => JsonNode.Parse(bounded)).Throws<JsonException>();
        await Assert.That(bounded.Exceeded).IsTrue();
    }

    [Test]
    public async Task BoundedReadStreamOneByteOverTheCapParsesButIsExceeded()
    {
        // One byte over the cap: the extra byte completes the document, so the parse succeeds. The loads must
        // therefore check Exceeded after a successful parse too.
        byte[] json = Encoding.UTF8.GetBytes("[1, 2, 3]");
        using MemoryStream inner = new(json);
        using BoundedReadStream bounded = new(inner, json.Length - 1);

        JsonNode? root = JsonNode.Parse(bounded);

        await Assert.That(root).IsNotNull();
        await Assert.That(bounded.Exceeded).IsTrue();
    }

    [Test]
    public async Task BoundedReadStreamNonPositiveCapThrows()
    {
        using MemoryStream inner = new();

        await Assert.That(() => new BoundedReadStream(inner, 0)).Throws<ArgumentOutOfRangeException>();
    }
}
