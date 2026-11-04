// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Text;

/// <summary>
/// The hex dump the Text exporter writes for the frame bytes: the title line with the length, one line per 16 bytes with a
/// four-digit upper-case offset, two spaces, and the bytes as upper-case hex pairs, no ASCII column, no cut by the text limit.
/// </summary>
internal sealed class TextHexDumpTests
{
    #region Frames, export, and dump extraction

    /// <summary>The frame of a test: <paramref name="length"/> bytes counting up from <paramref name="first"/>, so a dump is easy to read.</summary>
    /// <param name="length">The number of bytes.</param>
    /// <param name="first">The value of the first byte; each later byte is one more, and the count wraps around after 255.</param>
    /// <returns>The bytes.</returns>
    private static byte[] _Bytes(int length, int first = 0)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // The sequence wraps around on purpose: a frame longer than 256 bytes only needs distinct line starts, not distinct bytes.
            bytes[i] = unchecked((byte)(first + i));
        }

        return bytes;
    }

    /// <summary>Exports one packet per frame to Text (the field tree at Standard level) with the frame bytes on and returns the text.</summary>
    /// <param name="maxTextLength">The text limit in characters for the field lines.</param>
    /// <param name="frameBytes">Whether the frame bytes member is on.</param>
    /// <param name="frames">The frame of each packet.</param>
    /// <returns>The exported text.</returns>
    private static string _Export(int maxTextLength, bool frameBytes, params byte[][] frames)
    {
        using MemoryStream stream = new();
        TextExporter.Builder builder = TextExporter.CreateBuilder().ToStream(stream).WithMaxTextLength(maxTextLength)
            .WithContent(TextExporter.DefaultContent with { FrameBytes = frameBytes });
        using (TextExporter exporter = builder.Build())
        {
            List<Frame> list = [];
            for (int i = 0; i < frames.Length; i++)
            {
                list.Add(TestHarness.CreateFrame(i, (i + 1) * 1_000_000L, frames[i]));
            }

            _ = TreeFreeFeed.Run(exporter, list);
            exporter.OnFinish();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The lines of the text from the title line of the first dump on, up to the blank separator line.</summary>
    /// <param name="text">The exported text.</param>
    /// <returns>The title line and the dump lines of the first frame.</returns>
    private static string[] _DumpLines(string text)
    {
        string[] lines = text.Split('\n');
        int title = Array.FindIndex(lines, static line => line.StartsWith("Frame bytes (", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, title, static line => line.Length == 0);
        return lines[title..end];
    }

    #endregion

    #region Lines

    /// <summary>Proves that a 20-byte frame is dumped as one full line of 16 bytes and one partial line of 4.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ATwentyByteFrameWritesAFullAndAPartialLine()
    {
        string[] dump = _DumpLines(_Export(256, frameBytes: true, _Bytes(20)));

        await Assert.That(dump.Length).IsEqualTo(3);
        await Assert.That(dump[0]).IsEqualTo("Frame bytes (20):");
        await Assert.That(dump[1]).IsEqualTo("0000  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F");
        await Assert.That(dump[2]).IsEqualTo("0010  10 11 12 13");
    }

    /// <summary>Proves that a 16-byte frame is dumped as exactly one line.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ASixteenByteFrameWritesExactlyOneDumpLine()
    {
        string[] dump = _DumpLines(_Export(256, frameBytes: true, _Bytes(16, first: 0xF0)));

        await Assert.That(dump.Length).IsEqualTo(2);
        await Assert.That(dump[0]).IsEqualTo("Frame bytes (16):");
        await Assert.That(dump[1]).IsEqualTo("0000  F0 F1 F2 F3 F4 F5 F6 F7 F8 F9 FA FB FC FD FE FF");
    }

    /// <summary>Proves that an empty frame writes the title line with length 0 and no dump line.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnEmptyFrameWritesTheTitleLineAndNoDumpLine()
    {
        string[] dump = _DumpLines(_Export(256, frameBytes: true, Array.Empty<byte>()));

        await Assert.That(dump.Length).IsEqualTo(1);
        await Assert.That(dump[0]).IsEqualTo("Frame bytes (0):");
    }

    /// <summary>Proves that a one-byte frame writes one dump line that ends right after the byte, with no trailing space.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AFrameOfOneByteWritesOneLineWithoutATrailingSpace()
    {
        string[] dump = _DumpLines(_Export(256, frameBytes: true, [0xAB]));

        await Assert.That(dump[1]).IsEqualTo("0000  AB");
    }

    /// <summary>Proves that the dump offset grows to five digits for a frame beyond 64 KiB instead of wrapping.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheOffsetGrowsPastFourDigitsForAFrameBeyondSixtyFourKibibytes()
    {
        // 65 552 bytes = 4097 lines: the last line starts at offset 0x10010 (five digits).
        string[] dump = _DumpLines(_Export(256, frameBytes: true, _Bytes(65_552)));

        await Assert.That(dump.Length).IsEqualTo(1 + 4097);
        await Assert.That(dump[1].StartsWith("0000  ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dump[4096].StartsWith("FFF0  ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(dump[4097].StartsWith("10000  ", StringComparison.Ordinal)).IsTrue();
    }

    #endregion

    #region Rules

    /// <summary>Proves that the maximum text length never cuts a dump and that the dump has no ASCII column.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task TheDumpIsNotCutByTheMaximumTextLengthAndHasNoAsciiColumn()
    {
        // A limit of 8 characters cuts the values of the field lines but never the dump of the frame.
        string[] dump = _DumpLines(_Export(8, frameBytes: true, _Bytes(20, first: 0x41)));

        await Assert.That(dump[1]).IsEqualTo("0000  41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F 50");
        await Assert.That(dump[2]).IsEqualTo("0010  51 52 53 54");
        await Assert.That(dump.Any(static line => line.Contains("ABCD", StringComparison.Ordinal) || line.Contains('…', StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>Proves that control bytes of a frame appear in the output only as hexadecimal digits.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AControlByteInTheFrameReachesTheOutputOnlyAsHexDigits()
    {
        string text = _Export(256, frameBytes: true, [0x1B, 0x5B, 0x32, 0x4A, 0x0A, 0x0D, 0x00, 0x7F]);

        await Assert.That(_DumpLines(text)[1]).IsEqualTo("0000  1B 5B 32 4A 0A 0D 00 7F");
        await Assert.That(text.Contains('\u001B', StringComparison.Ordinal)).IsFalse();
        await Assert.That(text.Contains('\r', StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>Proves that each packet's dump comes before the blank separator line, so it belongs to its packet.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task EveryPacketGetsItsDumpBeforeTheBlankSeparatorLine()
    {
        string text = _Export(256, frameBytes: true, _Bytes(3), _Bytes(1, first: 9));

        string[] packets = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        // Two packets, each ending with its own dump: the dump belongs to the packet, not to the gap between packets.
        await Assert.That(packets.Length).IsEqualTo(2);
        await Assert.That(packets[0].TrimEnd().EndsWith("Frame bytes (3):\n0000  00 01 02", StringComparison.Ordinal)).IsTrue();
        await Assert.That(packets[1].TrimEnd().EndsWith("Frame bytes (1):\n0000  09", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Proves that with the frame-bytes member off the text holds no dump.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task WithoutFrameBytesTheTextHasNoDump()
    {
        string text = _Export(256, frameBytes: false, _Bytes(20));

        await Assert.That(text.Contains("Frame bytes", StringComparison.Ordinal)).IsFalse();
    }

    #endregion
}
