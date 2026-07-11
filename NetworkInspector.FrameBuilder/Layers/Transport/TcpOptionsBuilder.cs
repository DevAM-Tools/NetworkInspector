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

    private byte[]? _Buffer;
    private int _Length;

    /// <summary>Appends a Maximum Segment Size (MSS) option (4 bytes).</summary>
    /// <param name="mss">MSS value in bytes; typically 1460.</param>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void Mss(ushort mss)
    {
        _EnsureBuffer();
        _EnsureCapacity(4);
        _Buffer![_Length++] = 0x02; // option kind = MSS
        _Buffer![_Length++] = 0x04; // length = 4
        BinaryPrimitives.WriteUInt16BigEndian(_Buffer.AsSpan(_Length, 2), mss);
        _Length += 2;
    }

    /// <summary>
    /// Appends the standard SYN option set (22 bytes, padded to 24) used in
    /// modern TCP SYN handshakes:
    /// MSS(4) + SACKPermitted(2) + NOP+NOP+Timestamps(12) + NOP+WindowScale(3).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the option area would exceed 40 bytes.</exception>
    public void SynOptions()
    {
        _EnsureBuffer();
        _EnsureCapacity(22);
        // MSS: kind=2, len=4, value=1460
        Mss(1460);
        // SACKPermitted: kind=4, len=2
        _Buffer![_Length++] = 0x04;
        _Buffer![_Length++] = 0x02;
        // NOP, NOP (align Timestamps)
        _Buffer![_Length++] = 0x01;
        _Buffer![_Length++] = 0x01;
        // Timestamps: kind=8, len=10, TSval=0, TSecr=0
        _Buffer![_Length++] = 0x08;
        _Buffer![_Length++] = 0x0A;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        _Buffer![_Length++] = 0x00;
        // NOP (align WindowScale)
        _Buffer![_Length++] = 0x01;
        // WindowScale: kind=3, len=3, shift=7
        _Buffer![_Length++] = 0x03;
        _Buffer![_Length++] = 0x03;
        _Buffer![_Length++] = 0x07;
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
                $"TCP options would exceed the maximum of {_MaxOptionBytes} bytes (current {_Length}, adding {additional}).");
        }
    }
}
