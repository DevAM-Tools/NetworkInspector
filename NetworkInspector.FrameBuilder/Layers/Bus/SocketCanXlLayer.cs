// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Linux SocketCAN XL frame layer for the new <see cref="FrameStack"/> API.
/// Wire format for LINKTYPE_CAN_SOCKETCAN (DLT 227):
/// Prio/VCID(4 BE) + Flags(1) + Sdt(1) + Len(2 LE) + Af(4 LE) + Data(2048, zero-padded).
/// Total fixed frame size = 12 + 2048 = 2060 bytes.
/// The priority/VCID word is stored in big-endian byte order in pcap captures,
/// while all other multi-byte CAN-XL fields (Len, Af) are little-endian.
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — terminal frame.</item>
/// </list>
/// <para>The frame always writes a fixed 2048-byte data area (zero-padded beyond the
/// actual data length) to match the in-kernel <c>struct canxl_frame</c> layout. The data
/// payload is copied at construction time, so callers may mutate or release their buffer
/// immediately after constructing the layer.</para>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct SocketCanXlLayer : IStatelessLayer, IRootLayer
{
    /// <summary>Fixed CAN-XL header bytes (4 + 1 + 1 + 2 + 4), excluding the data area.</summary>
    public const int HeaderBytes = 12;

    /// <summary>Maximum data length per the CAN-XL specification.</summary>
    public const int MaxXlData = 2048;

    /// <summary>Total fixed frame size in bytes (header + max data).</summary>
    public const int FrameSize = HeaderBytes + MaxXlData;

    /// <summary>XLF flag in the <c>flags</c> byte (CAN-XL frame indicator).</summary>
    public const byte XlfFlag = 0x80;

    /// <summary>SEC flag in the <c>flags</c> byte (Simple Extended Content).</summary>
    public const byte SecFlag = 0x01;

    private readonly uint _Prio;
    private readonly byte _Flags;
    private readonly byte _Sdt;
    private readonly uint _Af;
    // Actual data stored in-struct using 256 ulongs (2048 bytes) — zero-padded.
    // Fields _D0.._D255 store data in 8-byte chunks; allocated via fixed fields.
    private readonly ReadOnlyMemory<byte> _Data;
    private readonly ushort _DataLen; // actual data length (capped at MaxXlData)

    /// <summary>Creates a SocketCAN-XL frame layer.</summary>
    /// <param name="priority">Priority/VCID composite field (32-bit, big-endian on wire for LINKTYPE_CAN_SOCKETCAN).</param>
    /// <param name="data">Frame data (0..2048 bytes). Excess bytes are dropped.</param>
    /// <param name="sdt">Service Data Unit Type byte; default 0.</param>
    /// <param name="af">Acceptance field (32-bit); default 0.</param>
    /// <param name="sec">SEC flag (Simple Extended Content); default false.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SocketCanXlLayer(
        uint priority,
        ReadOnlyMemory<byte> data,
        byte sdt = 0,
        uint af = 0,
        bool sec = false)
    {
        _Prio = priority;
        _Sdt = sdt;
        _Af = af;
        _DataLen = (ushort)(data.Length > MaxXlData ? MaxXlData : data.Length);
        _Data = data.Length > MaxXlData ? data[..MaxXlData] : data;

        byte flags = XlfFlag; // XLF always set
        if (sec)
        {
            flags |= SecFlag;
        }
        _Flags = flags;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => FrameSize; // always fixed 2060
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // Prio/VCID: 4 bytes big-endian in LINKTYPE_CAN_SOCKETCAN captures.
        // Wireshark: "The priority/VCID field is big-endian in LINKTYPE_CAN_SOCKETCAN
        // captures, for historical reasons."
        BinaryPrimitives.WriteUInt32BigEndian(dst[..4], _Prio);
        dst[4] = _Flags;
        dst[5] = _Sdt;
        // Len: 2 bytes little-endian, actual data length
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(6, 2), _DataLen);
        // AF: 4 bytes little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(8, 4), _Af);
        // Data area: copy actual data, then explicitly zero the tail. We must NOT rely on
        // the destination being pre-zeroed: FrameSequence/StatefulFrameSequence allocate
        // pooled buffers that may carry residual bytes from a previous frame. Leaving the
        // tail unzeroed would emit caller-buffer or scratch contents on the wire (review B1).
        ReadOnlySpan<byte> src = _Data.Span[.._DataLen];
        Span<byte> dataArea = dst.Slice(HeaderBytes, MaxXlData);
        src.CopyTo(dataArea);
        dataArea[_DataLen..].Clear();
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // CAN-XL frames are self-contained and need no post-fix.
    }
}
