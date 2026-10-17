// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc.Format;

/// <summary>
/// Parses ASC <c>ErrorFrame</c> and <c>OverloadFrame</c> lines into a 16-byte SocketCAN error frame.
/// The line carries no error-class bits, so the payload stays zero and only <c>CAN_ERR_FLAG</c> is set.
/// Stateless; safe to call from multiple threads.
/// </summary>
internal static class AscCanErrorParser
{
    private const int _FrameLength = 16;

    /// <summary>SocketCAN <c>CAN_ERR_FLAG</c> (bit 29).</summary>
    private const uint _CanErrFlag = 0x20000000u;

    /// <summary>
    /// Parses <c>&lt;time&gt; &lt;channel&gt; ErrorFrame</c> or <c>OverloadFrame</c>.
    /// </summary>
    /// <param name="line">Trimmed ASC line.</param>
    /// <param name="timestamp">Line timestamp. Not the file clock.</param>
    /// <param name="channel">Decimal channel token after the timestamp.</param>
    /// <param name="frame">16-byte SocketCAN buffer. Empty when this method returns <c>false</c>.</param>
    /// <returns><c>false</c> when the channel or keyword is missing.</returns>
    internal static bool TryParse(
        ReadOnlySpan<byte> line,
        out AscTimestamp timestamp,
        out int channel,
        out byte[] frame)
        => FrameSlabAllocator.ToArray(TryParse(line, null, out timestamp, out channel, out ReadOnlyMemory<byte> memory), memory, out frame);

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, out AscTimestamp, out int, out byte[])"/>
    /// <param name="line">Trimmed ASC line.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null for a new array.</param>
    /// <param name="timestamp">Line timestamp. Not the file clock.</param>
    /// <param name="channel">Decimal channel token after the timestamp.</param>
    /// <param name="frame">16-byte SocketCAN buffer. Empty when this method returns <c>false</c>.</param>
    internal static bool TryParse(
        ReadOnlySpan<byte> line,
        FrameSlabAllocator? slabs,
        out AscTimestamp timestamp,
        out int channel,
        out ReadOnlyMemory<byte> frame)
    {
        timestamp = default;
        channel = 0;
        frame = default;

        AscTokenizerBytes tokenizer = new(line);
        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> timeToken)
            || !AscTimestamp.TryParse(timeToken, out timestamp))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> channelToken)
            || !AscCanParser.TryParseChannel(channelToken, out channel))
        {
            return false;
        }

        if (!tokenizer.TryNextToken(out ReadOnlySpan<byte> keyword)
            || !_IsErrorKeyword(keyword))
        {
            return false;
        }

        frame = _BuildErrorFrame(slabs);
        return true;
    }

    private static ReadOnlyMemory<byte> _BuildErrorFrame(FrameSlabAllocator? slabs)
    {
        Memory<byte> frame = FrameSlabAllocator.RentZeroed(slabs, _FrameLength);
        BinaryPrimitives.WriteUInt32BigEndian(frame.Span, _CanErrFlag);
        return frame;
    }

    private static bool _IsErrorKeyword(ReadOnlySpan<byte> keyword) =>
        _Equals(keyword, "ErrorFrame"u8) || _Equals(keyword, "OverloadFrame"u8);

    private static bool _Equals(ReadOnlySpan<byte> token, ReadOnlySpan<byte> ascii) =>
        token.Length == ascii.Length
        && AscLineClassifier.StartsWithAsciiIgnoreCase(token, ascii);
}
