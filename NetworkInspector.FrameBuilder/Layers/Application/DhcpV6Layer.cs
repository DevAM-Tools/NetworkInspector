// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// DHCPv6 application-layer (RFC 8415) for the <see cref="FrameStack"/> API.
/// Produces a 4-byte client/server header followed by a TLV option block.
/// </summary>
/// <remarks>
/// <para>DHCPv6 message format (RFC 8415 §8):</para>
/// <code>
/// Byte  0:    msg-type
/// Bytes 1-3:  transaction-id (24 bits, big-endian)
/// Bytes 4+:   options (2-byte code, 2-byte length, value bytes)
/// </code>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier, no length auto-patching.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct DhcpV6Layer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    private readonly ReadOnlyMemory<byte> _Message;

    /// <summary>Creates a DHCPv6 layer.</summary>
    /// <param name="msgType">DHCPv6 message type (e.g., 1=SOLICIT, 2=ADVERTISE, 3=REQUEST).</param>
    /// <param name="xid24">24-bit transaction identifier; upper 8 bits must be zero.</param>
    /// <param name="options">DHCPv6 options; must not be <c>null</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="xid24"/> exceeds 24 bits.</exception>
    public DhcpV6Layer(byte msgType, uint xid24, IList<DhcpV6Option> options)
    {
        if ((xid24 & 0xFF000000u) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(xid24));
        }
        ArgumentNullException.ThrowIfNull(options);

        int optionsLen = 0;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Data.Length > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options[i].Data.Length,
                    $"DHCPv6 option {options[i].Code} data must not exceed 65535 bytes.");
            }
            optionsLen += 4 + options[i].Data.Length;
        }

        byte[] buf = new byte[4 + optionsLen];
        buf[0] = msgType;
        buf[1] = (byte)((xid24 >> 16) & 0xFF);
        buf[2] = (byte)((xid24 >> 8) & 0xFF);
        buf[3] = (byte)(xid24 & 0xFF);

        int idx = 4;
        for (int i = 0; i < options.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(idx, 2), options[i].Code);
            idx += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(idx, 2), (ushort)options[i].Data.Length);
            idx += 2;
            options[i].Data.Span.CopyTo(buf.AsSpan(idx));
            idx += options[i].Data.Length;
        }
        _Message = buf;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Message.Length;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
        => _Message.Span.CopyTo(dst);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed.
    }
}
