// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// FlexRay bus frame layer for the <see cref="FrameStack"/> API.
/// Uses LINKTYPE_FLEXRAY (DLT 210) as the capture link type.
/// </summary>
/// <remarks>
/// <para>
/// LINKTYPE_FLEXRAY capture format (per tcpdump.org specification):
/// </para>
/// <code>
/// Byte 0:     Measurement Header: [7] CH | [6:0] Type Index
///             CH = 0 for channel A, 1 for channel B.
///             Type Index = 1 for a regular FlexRay frame.
/// Byte 1:     Error Flags ([4] FCRCERR | [3] HCRCERR | [2] FESERR | [1] CODERR | [0] TSSVIOL)
/// Bytes 2-6:  FlexRay Frame Header (ISO 17458-2 §8):
///   Byte 2:   [7] Reserved | [6] PPI | [5] NFI | [4] SFI | [3] STFI | [2:0] FID[10:8]
///   Byte 3:   [7:0] FID[7:0]
///   Byte 4:   [7:1] Payload Length in 16-bit words (7 bits) | [0] HCRC[10]
///   Byte 5:   [7:0] HCRC[9:2]
///   Byte 6:   [7:6] HCRC[1:0] | [5:0] Cycle Count (6 bits)
/// Bytes 7+:   Payload data bytes
/// </code>
/// <para>
/// Payload length in bytes must be even (multiples of 2 words). Odd payloads are zero-padded.
/// </para>
/// <para><b>Capabilities:</b></para>
/// <list type="bullet">
///   <item><see cref="IRootLayer"/> — terminal frame; nothing chains beneath it.</item>
/// </list>
/// <para><b>Thread safety:</b> immutable value type; safe for concurrent use.</para>
/// </remarks>
public readonly struct FlexRayLayer : IStatelessLayer, IRootLayer
{
    private readonly ushort _FrameId;
    private readonly byte _CycleCount;
    private readonly bool _ChannelB;
    private readonly byte _TypeIndex;
    private readonly byte _ErrorFlags;
    private readonly bool _Nfi;
    private readonly bool _Sfi;
    private readonly bool _Stfi;
    private readonly bool _Ppi;
    private readonly ushort _Hcrc;
    private readonly ReadOnlyMemory<byte> _Payload;

    /// <summary>Creates a FlexRay frame layer.</summary>
    /// <param name="frameId">FlexRay frame identifier (0..2047, 11 bits).</param>
    /// <param name="cycleCount">Cycle count (0..63, 6 bits).</param>
    /// <param name="payload">Payload bytes (0..254 bytes; length must be even, or one byte is zero-padded).</param>
    /// <param name="channelB">
    /// <c>false</c> for channel A (default), <c>true</c> for channel B.
    /// </param>
    /// <param name="typeIndex">Type index in the measurement header (default 1 = Frame).</param>
    /// <param name="nfi"><c>true</c> if the Null Frame Indicator is set.</param>
    /// <param name="sfi"><c>true</c> if the Sync Frame Indicator is set.</param>
    /// <param name="stfi"><c>true</c> if the Startup Frame Indicator is set.</param>
    /// <param name="ppi"><c>true</c> if the Payload Preamble Indicator is set.</param>
    /// <param name="hcrc">Header CRC (11 bits); default 0 (no verification in FrameBuilder).</param>
    /// <param name="errorFlags">Error flags byte (default 0 = no errors).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FlexRayLayer(
        ushort frameId,
        byte cycleCount,
        ReadOnlyMemory<byte> payload = default,
        bool channelB = false,
        byte typeIndex = 1,
        bool nfi = true,
        bool sfi = false,
        bool stfi = false,
        bool ppi = false,
        ushort hcrc = 0,
        byte errorFlags = 0)
    {
        if (frameId > 0x7FF)
        {
            throw new ArgumentOutOfRangeException(nameof(frameId));
        }
        if (cycleCount > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(cycleCount));
        }
        _FrameId = frameId;
        _CycleCount = cycleCount;
        _ChannelB = channelB;
        _TypeIndex = typeIndex;
        _ErrorFlags = errorFlags;
        _Nfi = nfi;
        _Sfi = sfi;
        _Stfi = stfi;
        _Ppi = ppi;
        _Hcrc = hcrc;
        _Payload = payload;
    }

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int payloadBytes = _Payload.Length;
            // Payload length field counts 16-bit words; round up to even byte count.
            int paddedPayloadBytes = (payloadBytes + 1) & ~1;
            return 7 + paddedPayloadBytes;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteHeader(scoped Span<byte> dst)
    {
        byte measurementHeader = (byte)((_ChannelB ? 0x80 : 0x00) | (_TypeIndex & 0x7F));

        // Byte 2: [7] Reserved | [6] PPI | [5] NFI | [4] SFI | [3] STFI | [2:0] FID[10:8]
        byte byte2 = (byte)((_Ppi ? 0x40 : 0)
                           | (_Nfi ? 0x20 : 0)
                           | (_Sfi ? 0x10 : 0)
                           | (_Stfi ? 0x08 : 0)
                           | ((_FrameId >> 8) & 0x07));
        byte byte3 = (byte)(_FrameId & 0xFF);

        ReadOnlySpan<byte> payloadSpan = _Payload.Span;
        int payloadBytes = payloadSpan.Length;
        // Payload length in 16-bit words; round up.
        int payloadWords = (payloadBytes + 1) / 2;

        // Byte 4: [7:1] Payload Length (7 bits, words) | [0] HCRC[10]
        byte byte4 = (byte)(((payloadWords & 0x7F) << 1) | ((_Hcrc >> 10) & 0x01));
        // Byte 5: HCRC[9:2]
        byte byte5 = (byte)((_Hcrc >> 2) & 0xFF);
        // Byte 6: HCRC[1:0] | Cycle Count[5:0]
        byte byte6 = (byte)(((_Hcrc & 0x03) << 6) | (_CycleCount & 0x3F));

        dst[0] = measurementHeader;
        dst[1] = _ErrorFlags;
        dst[2] = byte2;
        dst[3] = byte3;
        dst[4] = byte4;
        dst[5] = byte5;
        dst[6] = byte6;

        // Copy payload bytes, zero-padding to even length.
        if (payloadBytes > 0)
        {
            payloadSpan.CopyTo(dst[7..]);
        }
        int paddedBytes = (payloadBytes + 1) & ~1;
        if (paddedBytes > payloadBytes)
        {
            dst[7 + payloadBytes] = 0;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
        // No post-fix processing needed: the entire frame is written in WriteHeader.
    }
}
