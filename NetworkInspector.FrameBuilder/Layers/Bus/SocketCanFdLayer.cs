// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Linux SocketCAN FD frame layer (72 bytes fixed) for the new
/// <see cref="FrameStack"/> API.  Wire format per <c>struct canfd_frame</c>:
/// CanId(4 BE) + Len(1) + Flags(1) + Res0(1) + Res1(1) + Data(64).
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — terminal frame.</item>
/// </list>
/// <para>Data is part of the layer (constructor arg); the <c>payload</c>
/// argument to <c>Build</c> must be empty.</para>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct SocketCanFdLayer : IStatelessLayer, IRootLayer
{
    /// <summary>Fixed CAN-FD frame size in bytes (4 + 1 + 1 + 1 + 1 + 64).</summary>
    public const int FrameSize = 72;

    /// <summary>Maximum payload size for CAN-FD.</summary>
    public const int MaxFdData = 64;

    /// <summary>BRS (Bit Rate Switch) flag in the <c>flags</c> byte.</summary>
    public const byte BrsFlag = 0x01;

    /// <summary>ESI (Error State Indicator) flag in the <c>flags</c> byte.</summary>
    public const byte EsiFlag = 0x02;

    /// <summary>FDF (CAN-FD Frame) flag in the <c>flags</c> byte.</summary>
    public const byte FdfFlag = 0x04;

    private readonly uint _CanIdWithFlags;
    private readonly byte _Length;
    private readonly byte _Flags;
    // Data stored as 8 ulong values for compact in-struct storage of up to 64 bytes.
    private readonly ulong _Data0;
    private readonly ulong _Data1;
    private readonly ulong _Data2;
    private readonly ulong _Data3;
    private readonly ulong _Data4;
    private readonly ulong _Data5;
    private readonly ulong _Data6;
    private readonly ulong _Data7;

    /// <summary>Creates a SocketCAN-FD frame layer.</summary>
    /// <param name="canId">CAN identifier (11 or 29 bits).</param>
    /// <param name="data">Frame data (0..64 bytes).  Excess bytes are dropped.</param>
    /// <param name="extended">Use 29-bit extended identifier (EFF).</param>
    /// <param name="brs">BRS flag (bit rate switch for the data phase).</param>
    /// <param name="errorStateIndicator">ESI flag (error state indicator).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SocketCanFdLayer(
        uint canId,
        ReadOnlySpan<byte> data = default,
        bool extended = false,
        bool brs = false,
        bool errorStateIndicator = false)
    {
        uint id = canId;
        if (extended)
        {
            id |= 0x80000000u;
        }
        _CanIdWithFlags = id;

        int len = data.Length > MaxFdData ? MaxFdData : data.Length;
        // SocketCAN's canfd_frame stores the actual payload byte count (0..64)
        // in byte 4 — there is no DLC encoding on the wire (the kernel and the
        // Wireshark dissector both treat byte 4 as a plain length).
        _Length = (byte)len;

        byte flags = FdfFlag; // CAN-FD frames always have the FDF bit set
        if (brs)
        {
            flags |= BrsFlag;
        }
        if (errorStateIndicator)
        {
            flags |= EsiFlag;
        }
        _Flags = flags;

        // Pack data into 8 ulongs using explicit little-endian reads so that the
        // byte order is reproducible on any host endianness when paired with the
        // WriteUInt64LittleEndian calls in WriteHeader.  The zero-padded stackalloc
        // backing ensures unused tail bytes are zero.
        Span<byte> tmp = stackalloc byte[MaxFdData];
        data[..len].CopyTo(tmp);
        _Data0 = BinaryPrimitives.ReadUInt64LittleEndian(tmp[..8]);
        _Data1 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(8, 8));
        _Data2 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(16, 8));
        _Data3 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(24, 8));
        _Data4 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(32, 8));
        _Data5 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(40, 8));
        _Data6 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(48, 8));
        _Data7 = BinaryPrimitives.ReadUInt64LittleEndian(tmp.Slice(56, 8));
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => FrameSize;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        // 4-byte big-endian CAN ID.
        BinaryPrimitives.WriteUInt32BigEndian(dst[..4], _CanIdWithFlags);
        dst[4] = _Length;
        dst[5] = _Flags;
        dst[6] = 0;
        dst[7] = 0;
        // 64-byte data area: explicit little-endian writes pair with the
        // ReadUInt64LittleEndian calls in the constructor for a host-endian-
        // independent byte-preserving round-trip.
        Span<byte> data = dst.Slice(8, MaxFdData);
        BinaryPrimitives.WriteUInt64LittleEndian(data[..8], _Data0);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(8, 8), _Data1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(16, 8), _Data2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(24, 8), _Data3);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(32, 8), _Data4);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(40, 8), _Data5);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(48, 8), _Data6);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(56, 8), _Data7);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // CAN-FD frames are self-contained and need no post-fix.
    }
}
