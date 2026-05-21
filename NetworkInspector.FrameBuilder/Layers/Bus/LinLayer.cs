// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// LIN bus frame layer for the <see cref="FrameStack"/> API.
/// Uses LINKTYPE_LIN (DLT 212) as the capture link type.
/// </summary>
/// <remarks>
/// <para>
/// The LIN capture format (per Wireshark packet-lin.h) has an 8-byte fixed
/// header followed by the data payload:
/// </para>
/// <code>
/// Byte 0:     Message Format Revision = 1
/// Bytes 1-3:  Reserved (0x00 0x00 0x00)
/// Byte 4:     (payloadLength &lt;&lt; 4) | (msgType &lt;&lt; 2) | checksumType
///             payloadLength = number of data bytes (0..8)
///             msgType = 0 = Frame (only supported value)
///             checksumType = 1 = Classic, 2 = Enhanced (ISO 17987)
/// Byte 5:     Protected ID = parity[7:6] | frameId[5:0]
/// Byte 6:     Checksum (computed over data bytes, optionally including PID)
/// Byte 7:     Error Flags (0 = no errors)
/// Bytes 8+:   Data payload (0..8 bytes)
/// </code>
/// <para>
/// <b>Parity computation (ISO 17987):</b> P0 = ID0 ⊕ ID1 ⊕ ID2 ⊕ ID4;
/// P1 = ¬(ID1 ⊕ ID3 ⊕ ID4 ⊕ ID5).
/// </para>
/// <para>
/// <b>Checksum:</b> Classic (type 1) sums data bytes only.
/// Enhanced (type 2) includes the PID byte in the sum (ISO 17987).
/// Both use carry-add mod 255 followed by bit-inversion.
/// </para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — terminal frame; nothing chains beneath it.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct LinLayer : IStatelessLayer, IRootLayer
{
    /// <summary>Maximum LIN data length (LIN 2.x specification).</summary>
    public const int MaxDataLength = 8;

    private readonly byte _FrameId;
    private readonly byte _ChecksumType;
    private readonly byte _ErrorFlags;
    private readonly int _DataLen;

    // Up to 8 data bytes stored inline (LIN specification limit).
    private readonly byte _D0;
    private readonly byte _D1;
    private readonly byte _D2;
    private readonly byte _D3;
    private readonly byte _D4;
    private readonly byte _D5;
    private readonly byte _D6;
    private readonly byte _D7;

    /// <summary>Creates a LIN frame layer.</summary>
    /// <param name="frameId">6-bit frame identifier (0..63).</param>
    /// <param name="data">Payload bytes (0..8). Excess bytes are silently dropped.</param>
    /// <param name="checksumType">
    /// 1 = Classic (sums data bytes only);
    /// 2 = Enhanced (includes PID, per ISO 17987); default 2.
    /// </param>
    /// <param name="errorFlags">Error flags byte (default 0 = no errors).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LinLayer(
        byte frameId,
        ReadOnlySpan<byte> data = default,
        byte checksumType = 2,
        byte errorFlags = 0)
    {
        if (frameId > 0x3F)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }
        _FrameId = frameId;
        _ChecksumType = checksumType;
        _ErrorFlags = errorFlags;

        int len = data.Length > MaxDataLength ? MaxDataLength : data.Length;
        _DataLen = len;
        _D0 = len > 0 ? data[0] : (byte)0;
        _D1 = len > 1 ? data[1] : (byte)0;
        _D2 = len > 2 ? data[2] : (byte)0;
        _D3 = len > 3 ? data[3] : (byte)0;
        _D4 = len > 4 ? data[4] : (byte)0;
        _D5 = len > 5 ? data[5] : (byte)0;
        _D6 = len > 6 ? data[6] : (byte)0;
        _D7 = len > 7 ? data[7] : (byte)0;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 8 + _DataLen; // 8-byte fixed header + data bytes
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        byte pid = WithParity(_FrameId);

        // Byte 4: payloadLength[7:4] | msgType[3:2] | checksumType[1:0]
        // msgType = 0 (Frame), so bits 3-2 are 00.
        byte byte4 = (byte)((_DataLen << 4) | (_ChecksumType & 0x03));

        // Collect data bytes into a temporary local span for checksum computation.
        Span<byte> dataSlice = dst.Length >= 8 + _DataLen ? dst.Slice(8, _DataLen) : stackalloc byte[_DataLen];

        if (_DataLen > 0)
        {
            dataSlice[0] = _D0;
        }
        if (_DataLen > 1)
        {
            dataSlice[1] = _D1;
        }
        if (_DataLen > 2)
        {
            dataSlice[2] = _D2;
        }
        if (_DataLen > 3)
        {
            dataSlice[3] = _D3;
        }
        if (_DataLen > 4)
        {
            dataSlice[4] = _D4;
        }
        if (_DataLen > 5)
        {
            dataSlice[5] = _D5;
        }
        if (_DataLen > 6)
        {
            dataSlice[6] = _D6;
        }
        if (_DataLen > 7)
        {
            dataSlice[7] = _D7;
        }

        byte checksum = ComputeChecksum(dataSlice[.._DataLen], pid, _ChecksumType);

        dst[0] = 1;           // msg_format_rev = 1
        dst[1] = 0;           // reserved
        dst[2] = 0;           // reserved
        dst[3] = 0;           // reserved
        dst[4] = byte4;
        dst[5] = pid;
        dst[6] = checksum;
        dst[7] = _ErrorFlags;

        // Data bytes are already written to dataSlice (which points into dst) above;
        // if they were on the stack (fallback path), copy them now.
        if (dataSlice.Overlaps(dst))
        {
            return;
        }
        dataSlice.CopyTo(dst[8..]);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed: the entire frame is written in WriteHeader.
    }

    /// <summary>Computes the 8-bit Protected ID (frame id + 2 parity bits per ISO 17987).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte WithParity(byte frameId)
    {
        int id0 = (frameId >> 0) & 1;
        int id1 = (frameId >> 1) & 1;
        int id2 = (frameId >> 2) & 1;
        int id3 = (frameId >> 3) & 1;
        int id4 = (frameId >> 4) & 1;
        int id5 = (frameId >> 5) & 1;
        int p0 = id0 ^ id1 ^ id2 ^ id4;
        int p1 = (id1 ^ id3 ^ id4 ^ id5) ^ 1; // NOT of XOR
        return (byte)(frameId | (p0 << 6) | (p1 << 7));
    }

    /// <summary>
    /// Computes the LIN checksum using carry-add (mod 255) then bit-inversion.
    /// Classic (type 1): sums data bytes only.
    /// Enhanced (type 2): includes the PID byte in the sum (ISO 17987).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ComputeChecksum(ReadOnlySpan<byte> data, byte pid, byte type)
    {
        uint sum = type == 2 ? pid : 0u;
        foreach (byte b in data)
        {
            sum += b;
            if (sum > 0xFF)
            {
                sum = (sum & 0xFF) + 1;
            }
        }
        return (byte)(~sum & 0xFF);
    }
}
