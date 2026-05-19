// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Signal-PDU application layer for the new <see cref="FrameStack"/> API.
/// Carries either a structured bitfield layout (preferred) or a raw payload
/// blob (legacy, behind the explicit <see cref="FromRawBytes"/> factory).
/// </summary>
/// <remarks>
/// <para>Capabilities:</para>
/// <list type="bullet">
///   <item><see cref="IPayloadLayer"/> — pure terminal payload carrier; no
///   layer can chain underneath it.</item>
///   <item><see cref="IStatelessLayer"/> — no per-frame mutable state.</item>
///   <item><see cref="IPseudoHeaderIndependent"/> — needs no transport pseudo
///   header.</item>
/// </list>
/// <para>
/// In the structured mode the layer renders each <see cref="SignalSpec"/>
/// according to its bit position, byte order, type and linear scaling. The
/// algorithm is the inverse of the parser's <c>SignalDecoder</c>; the same
/// in-memory <see cref="SignalPduLayout"/> drives both sides via the test
/// bridge so a wire-format drift is structurally impossible.
/// </para>
/// <para>
/// In the raw-bytes mode the layer just emits a caller-supplied byte blob,
/// preserving compatibility with tests that pre-compute the wire image
/// elsewhere.
/// </para>
/// <para>Thread safety: immutable struct, safe for concurrent use.</para>
/// </remarks>
public readonly struct SignalPduLayer : IStatelessLayer, IPayloadLayer, IPseudoHeaderIndependent
{
    private readonly SignalPduLayout? _Layout;
    private readonly SignalValueSet? _Values;
    private readonly ReadOnlyMemory<byte> _RawBytes;

    /// <summary>
    /// Creates a Signal-PDU layer that renders the given <paramref name="layout"/>
    /// using the values in <paramref name="values"/>.
    /// </summary>
    /// <param name="layout">PDU layout (bit-fields, mux groups, scaling).</param>
    /// <param name="values">Concrete signal values to encode; must be bound to <paramref name="layout"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="values"/> was built for a different layout
    /// instance (catches the most common test-wiring mistake at construction
    /// time rather than producing silent garbage on the wire).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SignalPduLayer(SignalPduLayout layout, SignalValueSet values)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(values);
        if (!ReferenceEquals(values.Layout, layout))
        {
            throw new ArgumentException("SignalValueSet was built for a different SignalPduLayout instance.", nameof(values));
        }
        _Layout = layout;
        _Values = values;
        _RawBytes = default;
    }

    private SignalPduLayer(ReadOnlyMemory<byte> rawBytes)
    {
        _Layout = null;
        _Values = null;
        _RawBytes = rawBytes;
    }

    /// <summary>
    /// Builds a Signal-PDU layer that emits <paramref name="bytes"/> verbatim
    /// without any bitfield encoding, for cases where the wire image is computed
    /// externally.  Prefer the
    /// <see cref="SignalPduLayer(SignalPduLayout, SignalValueSet)"/> constructor
    /// for production code.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SignalPduLayer FromRawBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <inheritdoc />
    public int HeaderSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Layout is null ? _RawBytes.Length : _Layout.ByteLength;
    }

    /// <inheritdoc />
    public void WriteHeader(scoped Span<byte> dst)
    {
        if (_Layout is null)
        {
            _RawBytes.Span.CopyTo(dst);
            return;
        }

        // Zero the destination first so unwritten bit positions are deterministic.
        dst.Clear();

        SignalPduLayout layout = _Layout;
        SignalValueSet values = _Values!;

        // Static signals are always rendered.
        foreach (SignalSpec sig in layout.Signals)
        {
            EncodeSignal(dst, sig, values);
        }

        // Mux: write the selector, then render only the matching group.
        if (layout.Mux is { } mux)
        {
            ulong muxRaw = ResolveMuxRaw(mux, values);

            // Emit selector itself as a virtual unsigned signal.
            WriteRawBits(dst, mux.StartBit, mux.BitLength, muxRaw, mux.Endian);

            bool matched = false;
            foreach (MuxGroupSpec group in layout.MuxGroups)
            {
                if (group.MuxValue == muxRaw)
                {
                    foreach (SignalSpec sig in group.Signals)
                    {
                        EncodeSignal(dst, sig, values);
                    }
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                throw new InvalidOperationException(
                    $"Multiplexer '{mux.Name}' value {muxRaw} does not match any configured MuxGroup.");
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>No-op: payload bytes carry no length / checksum that needs patching.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyPostFix(FixPhase phase, scoped Span<byte> frame, int myOffset, int myLength, scoped ref PostFixContext ctx)
    {
    }

    #region Encoder primitives

    /// <summary>
    /// Resolves the raw selector value for a mux signal: prefers an explicit
    /// raw value via <see cref="SignalValueSet.SetRaw"/>, falls back to a
    /// physical value via <see cref="SignalValueSet.Set"/> (no scaling for
    /// the selector itself), and otherwise throws so a missing mux value
    /// fails loudly instead of silently rendering group 0.
    /// </summary>
    private static ulong ResolveMuxRaw(MuxSpec mux, SignalValueSet values)
    {
        if (values.TryGetRaw(mux.Name, out ulong raw))
        {
            return raw;
        }
        if (values.TryGetPhysical(mux.Name, out double physical))
        {
            return (ulong)Math.Round(physical, MidpointRounding.AwayFromZero);
        }
        throw new InvalidOperationException(
            $"Multiplexer selector '{mux.Name}' has no value in the SignalValueSet.");
    }

    /// <summary>
    /// Encodes a single static or mux-group signal into <paramref name="dst"/>:
    /// computes the raw bits from the value set, masks to <see cref="SignalSpec.BitLength"/>
    /// and writes them at <see cref="SignalSpec.StartBit"/> with the requested endianness.
    /// </summary>
    private static void EncodeSignal(scoped Span<byte> dst, SignalSpec sig, SignalValueSet values)
    {
        ulong raw;

        // Raw values bypass scaling and type interpretation entirely.
        if (values.TryGetRaw(sig.Name, out ulong explicitRaw))
        {
            raw = explicitRaw;
        }
        else if (values.TryGetPhysical(sig.Name, out double physical))
        {
            raw = ToRawBits(sig, physical);
        }
        else
        {
            // Unset signal => zero bits. Matches the WriteHeader pre-clear and
            // is the convention parsers expect for absent values in static slots.
            raw = 0;
        }

        WriteRawBits(dst, sig.StartBit, sig.BitLength, raw, sig.Endian);
    }

    /// <summary>
    /// Converts a physical (post-scaled) signal value into the raw bit
    /// representation per the signal type, then masks to
    /// <see cref="SignalSpec.BitLength"/>.
    /// </summary>
    private static ulong ToRawBits(SignalSpec sig, double physical)
    {
        ulong mask = sig.BitLength == 64 ? ulong.MaxValue : (1UL << sig.BitLength) - 1;

        switch (sig.Type)
        {
            case SignalType.F32:
                if (sig.BitLength != 32)
                {
                    throw new InvalidOperationException(
                        $"Signal '{sig.Name}' of type F32 must have BitLength=32 (got {sig.BitLength}).");
                }
                // No scaling: F32 uses its IEEE-754 value verbatim.
                return BitConverter.SingleToUInt32Bits((float)physical);

            case SignalType.F64:
                if (sig.BitLength != 64)
                {
                    throw new InvalidOperationException(
                        $"Signal '{sig.Name}' of type F64 must have BitLength=64 (got {sig.BitLength}).");
                }
                return BitConverter.DoubleToUInt64Bits(physical);

            case SignalType.Signed:
                {
                    // Inverse linear scaling: raw = (physical - offset) / factor.
                    double factor = sig.Factor == 0.0 ? 1.0 : sig.Factor;
                    double scaled = (physical - sig.Offset) / factor;
                    long signedRaw = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
                    // Two's-complement truncation into the configured bit width.
                    return (ulong)signedRaw & mask;
                }

            case SignalType.Unsigned:
            default:
                {
                    double factor = sig.Factor == 0.0 ? 1.0 : sig.Factor;
                    double scaled = (physical - sig.Offset) / factor;
                    long signedRaw = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
                    return (ulong)signedRaw & mask;
                }
        }
    }

    /// <summary>
    /// Writes the lowest <paramref name="bitLength"/> bits of <paramref name="raw"/>
    /// at <paramref name="startBit"/> in <paramref name="dst"/> with the given
    /// endianness. The big-endian path is the inverse of the parser's
    /// <c>SignalDecoder.ExtractBigEndian</c>; the little-endian path is the
    /// inverse of <c>SignalDecoder.ExtractLittleEndian</c>.
    /// </summary>
    private static void WriteRawBits(scoped Span<byte> dst, int startBit, int bitLength, ulong raw, SignalEndian endian)
    {
        if (bitLength <= 0 || bitLength > 64)
        {
            return;
        }

        if (endian == SignalEndian.Big)
        {
            WriteBigEndianBits(dst, startBit, bitLength, raw);
        }
        else
        {
            WriteLittleEndianBits(dst, startBit, bitLength, raw);
        }
    }

    /// <summary>
    /// Big-endian / Motorola writeback: <paramref name="startBit"/> is the
    /// MSB position of the field; bits flow from MSB to LSB across byte
    /// boundaries with each new byte starting at bit position 7.
    /// </summary>
    private static void WriteBigEndianBits(scoped Span<byte> dst, int startBit, int bitLength, ulong raw)
    {
        int bytePos = startBit / 8;
        int bitPos = startBit % 8;
        int bitsRemaining = bitLength;

        while (bitsRemaining > 0 && (uint)bytePos < (uint)dst.Length)
        {
            int available = bitPos + 1;
            int bitsToWrite = Math.Min(available, bitsRemaining);
            int shift = bitPos - bitsToWrite + 1;
            byte mask = (byte)((1 << bitsToWrite) - 1);

            // Top `bitsToWrite` bits of the as-yet-unconsumed portion of raw.
            int chunkStartBit = bitsRemaining - bitsToWrite;
            byte chunk = (byte)((raw >> chunkStartBit) & mask);

            byte clearedByte = (byte)(dst[bytePos] & ~(mask << shift));
            dst[bytePos] = (byte)(clearedByte | (chunk << shift));

            bitsRemaining -= bitsToWrite;
            bytePos++;
            bitPos = 7;
        }
    }

    /// <summary>
    /// Little-endian / Intel writeback: <paramref name="startBit"/> is the
    /// LSB position of the field; bit i of <paramref name="raw"/> ends up at
    /// (byteIndex = (startBit + i) / 8, bitIndex = (startBit + i) % 8).
    /// </summary>
    private static void WriteLittleEndianBits(scoped Span<byte> dst, int startBit, int bitLength, ulong raw)
    {
        int currentBit = startBit;
        for (int i = 0; i < bitLength; i++)
        {
            int byteIndex = currentBit / 8;
            int bitIndex = currentBit % 8;

            if ((uint)byteIndex < (uint)dst.Length)
            {
                if ((raw & (1UL << i)) != 0)
                {
                    dst[byteIndex] |= (byte)(1 << bitIndex);
                }
                else
                {
                    dst[byteIndex] &= (byte)~(1 << bitIndex);
                }
            }

            currentBit++;
        }
    }

    #endregion
}
