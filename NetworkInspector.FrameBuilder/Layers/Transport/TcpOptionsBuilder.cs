// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Accumulates TCP option bytes and produces a <see cref="TcpOptions"/> instance.
/// </summary>
/// <remarks>
/// Create with <c>new TcpOptionsBuilder()</c>, call option methods, then call
/// <see cref="Build"/> to obtain the immutable <see cref="TcpOptions"/> value.
/// <para>Thread safety: not thread-safe; each caller must use its own instance.</para>
/// </remarks>
public struct TcpOptionsBuilder
{
    /// <summary>Maximum TCP option bytes (TCP header max 60 bytes minus 20-byte base).</summary>
    private const int _MaxOptionBytes = 40;

    /// <summary>Largest legal window scale shift (RFC 7323 section 2.3); a format fact, not configurable.</summary>
    private const byte _MaxWindowScaleShift = 14;

    /// <summary>Most SACK blocks the 40-byte option area can carry: (40 - 2) / 8 = 4 (RFC 2018 section 3).</summary>
    private const int _MaxSackBlocks = 4;

    private byte[]? _Buffer;
    private int _Length;

    /// <summary>Appends a Maximum Segment Size (MSS) option (4 bytes).</summary>
    /// <param name="mss">MSS value in bytes; typically 1460.</param>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Mss(ushort mss)
    {
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, mss);
        _Append(0x02, data);
    }

    /// <summary>Appends a single No-Operation byte (kind 1), used to align the options that follow it (RFC 9293 section 3.2).</summary>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Nop() => _AppendSingleByte(0x01);

    /// <summary>
    /// Appends an End-Of-Option-List byte (kind 0). Options appended after it are still written; the receiver ignores them,
    /// which is what a test of malformed option areas needs.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Eol() => _AppendSingleByte(0x00);

    /// <summary>Appends a SACK Permitted option (kind 4, 2 bytes; RFC 2018 section 2).</summary>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void SackPermitted() => _Append(0x04, []);

    /// <summary>Appends a Window Scale option (kind 3, 3 bytes; RFC 7323 section 2).</summary>
    /// <param name="shift">Shift count, 0 to 14. RFC 7323 section 2.3 forbids larger values, so they are rejected here.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="shift"/> is above 14.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void WindowScale(byte shift)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(shift, _MaxWindowScaleShift);
        _Append(0x03, [shift]);
    }

    /// <summary>Appends a Timestamps option (kind 8, 10 bytes; RFC 7323 section 3).</summary>
    /// <param name="timestampValue">TSval, the sender's timestamp clock.</param>
    /// <param name="timestampEcho">TSecr, the echoed peer timestamp; 0 in a SYN.</param>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Timestamps(uint timestampValue, uint timestampEcho)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(data, timestampValue);
        BinaryPrimitives.WriteUInt32BigEndian(data[4..], timestampEcho);
        _Append(0x08, data);
    }

    /// <summary>Appends a SACK option (kind 5; RFC 2018 section 3) with one to four blocks.</summary>
    /// <param name="blocks">The (left edge, right edge) sequence numbers of the selectively acknowledged ranges.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when there are no blocks or more than four (the option area holds at most four).</exception>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Sack(params ReadOnlySpan<(uint Left, uint Right)> blocks)
    {
        ArgumentOutOfRangeException.ThrowIfZero(blocks.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blocks.Length, _MaxSackBlocks);

        Span<byte> data = stackalloc byte[_MaxSackBlocks * 8];
        for (int i = 0; i < blocks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data[(i * 8)..], blocks[i].Left);
            BinaryPrimitives.WriteUInt32BigEndian(data[((i * 8) + 4)..], blocks[i].Right);
        }

        _Append(0x05, data[..(blocks.Length * 8)]);
    }

    /// <summary>Appends an option of any kind with the given payload; the length byte is derived from the payload.</summary>
    /// <param name="kind">Option kind byte.</param>
    /// <param name="data">Option payload without kind and length bytes; at most 38 bytes.</param>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Unknown(byte kind, ReadOnlySpan<byte> data) => _Append(kind, data);

    /// <summary>
    /// Appends the standard SYN option set (22 bytes, padded to 24) used in modern TCP SYN handshakes:
    /// MSS(4) + SACKPermitted(2) + NOP+NOP+Timestamps(10) + NOP+WindowScale(3).
    /// </summary>
    /// <remarks>
    /// Built from the single-option methods on purpose: the bundle and the building blocks cannot drift apart, and a test
    /// that needs a different shift or timestamp calls the blocks directly instead of hand-encoding bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void SynOptions()
    {
        Mss(1460);
        SackPermitted();
        Nop();
        Nop();
        Timestamps(0, 0);
        Nop();
        WindowScale(7);

        // Total: 4 + 2 + 2 + 10 + 1 + 3 = 22 bytes; padded to 24 in Build()
    }

    /// <summary>
    /// Produces a <see cref="TcpOptions"/> with the accumulated bytes padded to a
    /// 4-byte boundary.  Returns <see cref="TcpOptions.Empty"/> when nothing was added.
    /// </summary>
    public TcpOptions Build()
    {
        if (_Length == 0)
        {
            return TcpOptions.Empty;
        }

        // Pad to 4-byte boundary with zero (End-Of-Options).
        int padded = (_Length + 3) & ~3;
        byte[] result = new byte[padded];
        _Buffer.AsSpan(0, _Length).CopyTo(result);
        return new TcpOptions(result);
    }

    /// <summary>
    /// Writes one kind-length-value option. The length byte counts itself and the kind byte (RFC 9293 section 3.2), so it is
    /// derived from the payload here and no caller can write an inconsistent length.
    /// </summary>
    private void _Append(byte kind, ReadOnlySpan<byte> data)
    {
        _EnsureBuffer();
        _EnsureCapacity(2 + data.Length);
        _Buffer![_Length++] = kind;
        _Buffer![_Length++] = (byte)(2 + data.Length);
        data.CopyTo(_Buffer.AsSpan(_Length));
        _Length += data.Length;
    }

    /// <summary>Writes a one-byte option (EOL or NOP), which has neither a length byte nor a payload.</summary>
    private void _AppendSingleByte(byte kind)
    {
        _EnsureBuffer();
        _EnsureCapacity(1);
        _Buffer![_Length++] = kind;
    }

    /// <summary>Allocates the backing buffer on first use.</summary>
    private void _EnsureBuffer()
    {
        if (_Buffer is null)
        {
            _Buffer = new byte[_MaxOptionBytes];
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when appending
    /// <paramref name="additional"/> bytes would exceed the 40-byte TCP
    /// option-area limit (RFC 9293 §3.1).  Called at the start of every
    /// option-emitting method so callers see a clear error instead of an
    /// opaque <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    private readonly void _EnsureCapacity(int additional)
    {
        if (_Length + additional > _MaxOptionBytes)
        {
            throw new InvalidOperationException(
                Invariant($"TCP options would exceed the maximum of {_MaxOptionBytes} bytes (current {_Length}, adding {additional})."));
        }
    }
}
