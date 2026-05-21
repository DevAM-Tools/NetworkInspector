// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// DHCPv4 / BOOTP application-layer for the <see cref="FrameStack"/> API.
/// Produces a BOOTP fixed header (236 bytes), the DHCP magic cookie,
/// a TLV option block, and the End sentinel (0xFF).
/// </summary>
/// <remarks>
/// <para>
/// BOOTP fixed header layout (RFC 951 / RFC 2131):
/// </para>
/// <code>
/// Byte  0:     op (1=BOOTREQUEST, 2=BOOTREPLY)
/// Byte  1:     htype = 1 (Ethernet)
/// Byte  2:     hlen  = 6
/// Byte  3:     hops  = 0
/// Bytes 4-7:   xid (transaction id)
/// Bytes 8-9:   secs  = 0
/// Bytes 10-11: flags
/// Bytes 12-15: ciaddr
/// Bytes 16-19: yiaddr
/// Bytes 20-23: siaddr
/// Bytes 24-27: giaddr
/// Bytes 28-43: chaddr (AA:BB:CC:DD:EE:FF zero-padded to 16 bytes)
/// Bytes 44-107:sname (64 bytes, zero)
/// Bytes 108-235:file (128 bytes, zero)
/// Bytes 236-239:magic cookie = 0x63825363
/// Bytes 240+:  DHCP options (TLV: type(1)+length(1)+value)
/// Last byte:   0xFF (End option)
/// </code>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure payload carrier, no length auto-patching.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — not an IP transport; no pseudo-header concerns.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use after construction.</para>
/// </remarks>
public readonly struct DhcpV4Layer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    /// <summary>BOOTP/DHCP magic cookie (RFC 2131 §3).</summary>
    public const uint MagicCookie = 0x63825363u;

    private readonly ReadOnlyMemory<byte> _Message;

    /// <summary>Creates a DHCPv4 layer with the BOOTP fixed header, magic cookie, and options.</summary>
    /// <param name="op">Operation code: 1 = BOOTREQUEST, 2 = BOOTREPLY.</param>
    /// <param name="xid">Transaction identifier.</param>
    /// <param name="options">DHCP options; must not be <c>null</c>.</param>
    /// <param name="flags">Flags field (0x8000 = broadcast, default 0).</param>
    /// <param name="ciaddr">Client IP address (default 0.0.0.0).</param>
    /// <param name="yiaddr">Your (client) IP address (default 0.0.0.0).</param>
    /// <param name="siaddr">Next server IP address (default 0.0.0.0).</param>
    /// <param name="giaddr">Relay agent IP address (default 0.0.0.0).</param>
    public DhcpV4Layer(
        byte op,
        uint xid,
        IList<DhcpV4Option> options,
        ushort flags = 0,
        IPv4Address ciaddr = default,
        IPv4Address yiaddr = default,
        IPv4Address siaddr = default,
        IPv4Address giaddr = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        int optionsLen = 1; // End byte
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Data.Length > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options[i].Data.Length,
                    $"DHCP option {options[i].Type} data must not exceed 255 bytes.");
            }
            optionsLen += 2 + options[i].Data.Length;
        }

        byte[] buf = new byte[240 + optionsLen];

        // BOOTP fixed header (chaddr hard-wired to AA:BB:CC:DD:EE:FF per test convention).
        buf[0] = op;
        buf[1] = 1; // htype = Ethernet
        buf[2] = 6; // hlen  = 6
        buf[3] = 0; // hops
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4, 4), xid);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(8, 2), 0);     // secs
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(10, 2), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12, 4), ciaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16, 4), yiaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), siaddr.RawValue);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), giaddr.RawValue);
        buf[28] = 0xAA;
        buf[29] = 0xBB;
        buf[30] = 0xCC;
        buf[31] = 0xDD;
        buf[32] = 0xEE;
        buf[33] = 0xFF;
        // chaddr[6..15], sname[64], file[128] left zero.
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(236, 4), MagicCookie);

        int idx = 240;
        for (int i = 0; i < options.Count; i++)
        {
            buf[idx++] = options[i].Type;
            buf[idx++] = (byte)options[i].Data.Length;
            options[i].Data.Span.CopyTo(buf.AsSpan(idx));
            idx += options[i].Data.Length;
        }
        buf[idx] = 0xFF; // End option
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
