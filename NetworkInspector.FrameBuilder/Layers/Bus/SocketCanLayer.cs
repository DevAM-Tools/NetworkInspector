// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Linux SocketCAN classic frame layer (16 bytes fixed) for the new
/// <see cref="FrameStack"/> API.  PCAP linktype 227 (LINKTYPE_CAN_SOCKETCAN).
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — terminal frame; nothing chains beneath it.</item>
/// </list>
/// <para>The CAN frame is self-contained: data bytes are part of the layer
/// (passed in the constructor), so the <c>payload</c> argument to
/// <see cref="CreatedStack{TStack,TTrailer,TInterceptor}.Build(System.ReadOnlySpan{byte})"/>
/// must be empty.</para>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct SocketCanLayer : IStatelessLayer, IRootLayer
{
    /// <summary>Maximum payload size for classic CAN.</summary>
    private const int MaxClassicCanData = 8;

    private readonly uint _CanIdWithFlags;
    private readonly byte _Dlc;
    private readonly byte _Data0;
    private readonly byte _Data1;
    private readonly byte _Data2;
    private readonly byte _Data3;
    private readonly byte _Data4;
    private readonly byte _Data5;
    private readonly byte _Data6;
    private readonly byte _Data7;

    /// <summary>Creates a SocketCAN classic frame layer.</summary>
    /// <param name="canId">CAN identifier (11 or 29 bits).</param>
    /// <param name="data">Frame data (0..8 bytes).  Excess bytes are dropped.</param>
    /// <param name="extended">Use 29-bit extended identifier (EFF).</param>
    /// <param name="remoteTransmissionRequest">RTR flag.</param>
    /// <param name="errorFrame">ERR flag (error message frame).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SocketCanLayer(
        uint canId,
        ReadOnlySpan<byte> data = default,
        bool extended = false,
        bool remoteTransmissionRequest = false,
        bool errorFrame = false)
    {
        uint id = canId;
        if (extended)
        {
            id |= SocketCanHeader.EffFlag;
        }
        if (remoteTransmissionRequest)
        {
            id |= SocketCanHeader.RtrFlag;
        }
        if (errorFrame)
        {
            id |= SocketCanHeader.ErrFlag;
        }
        _CanIdWithFlags = id;

        int len = data.Length > MaxClassicCanData ? MaxClassicCanData : data.Length;
        _Dlc = (byte)len;
        _Data0 = len > 0 ? data[0] : (byte)0;
        _Data1 = len > 1 ? data[1] : (byte)0;
        _Data2 = len > 2 ? data[2] : (byte)0;
        _Data3 = len > 3 ? data[3] : (byte)0;
        _Data4 = len > 4 ? data[4] : (byte)0;
        _Data5 = len > 5 ? data[5] : (byte)0;
        _Data6 = len > 6 ? data[6] : (byte)0;
        _Data7 = len > 7 ? data[7] : (byte)0;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => SocketCanHeader.Size;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        SocketCanHeader hdr = new()
        {
            CanId = _CanIdWithFlags,
            Dlc = _Dlc,
            Pad = 0,
            Res0 = 0,
            Res1 = 0,
            Data0 = _Data0,
            Data1 = _Data1,
            Data2 = _Data2,
            Data3 = _Data3,
            Data4 = _Data4,
            Data5 = _Data5,
            Data6 = _Data6,
            Data7 = _Data7,
        };
        _ = ((IBinarySerializable)hdr).TryWrite(dst, out _);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // CAN frames are self-contained and need no post-fix.
    }
}
