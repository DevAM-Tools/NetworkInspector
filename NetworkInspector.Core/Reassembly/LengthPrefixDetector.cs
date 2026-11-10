// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Length-prefix based PDU boundary detection.
/// Reads a fixed-size length field at a specified offset.
/// <para>
/// <b>Header size semantics:</b> When <c>lengthIncludesHeader == false</c>, the value read
/// from the length field describes the payload only — the detector adds the header size to
/// obtain the total PDU length. The effective header size is:
/// <list type="bullet">
///   <item><c>headerSize</c> if <c>headerSize &gt; 0</c> (explicitly provided),</item>
///   <item>otherwise <c>lengthOffset + lengthSize</c> (assumes the length field sits at the
///         end of the header — common for simple wire formats).</item>
/// </list>
/// To avoid ambiguity, callers SHOULD pass an explicit <c>headerSize</c> whenever the
/// header contains bytes after the length field.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Immutable after construction; instances are safe to share across threads.
/// </para>
/// </summary>
public sealed class LengthPrefixDetector : IPduBoundaryDetector
{
    #region Configuration and state

    /// <summary>Byte offset of the length field within the PDU header; not negative, and <c>_LengthOffset + _LengthSize</c> fits an int.</summary>
    private readonly int _LengthOffset;

    /// <summary>Width of the length field in bytes: 1, 2, or 4.</summary>
    private readonly int _LengthSize;

    /// <summary>Byte order of the length field: <see langword="true"/> for big endian.</summary>
    private readonly bool _BigEndian;

    /// <summary>Whether the announced length already covers the header; when false, <see cref="_EffectiveHeaderSize"/> is added.</summary>
    private readonly bool _LengthIncludesHeader;

    /// <summary>
    /// Header size that is added to an announced payload length: the explicit header size, or the end of the length field
    /// when none was given. Resolved once so <see cref="Detect"/> does no recomputation per call.
    /// </summary>
    private readonly int _EffectiveHeaderSize;

    /// <summary>Shortest total length that is a valid PDU: the header and at least the bytes up to the end of the length field.</summary>
    private readonly int _MinPduLength;

    /// <summary>
    /// Longest total PDU length that this detector accepts; a longer announced PDU is reported as invalid. Positive;
    /// <see cref="int.MaxValue"/> when the detector has no limit of its own.
    /// </summary>
    private readonly int _MaxPduSize;

    /// <summary>Creates a length-prefix based PDU boundary detector.</summary>
    /// <param name="lengthOffset">Byte offset of the length field within the PDU header.</param>
    /// <param name="lengthSize">Width of the length field in bytes. Must be 1, 2, or 4.</param>
    /// <param name="bigEndian">Endianness of the length field.</param>
    /// <param name="lengthIncludesHeader">
    /// If <c>true</c>, the length value already covers the header bytes; if <c>false</c>, the
    /// detector adds the effective header size (see <paramref name="headerSize"/>).
    /// </param>
    /// <param name="headerSize">
    /// Total header size in bytes. When <paramref name="lengthIncludesHeader"/> is <c>false</c> it is added to the
    /// announced length; when it is <c>true</c> it is the shortest total length that is accepted (a shorter value is
    /// <see cref="PduBoundaryResult.Invalid"/>). A value of <c>0</c> defaults to <c>lengthOffset + lengthSize</c>.
    /// </param>
    /// <param name="maxPduSize">
    /// Maximum allowed PDU length in bytes for this detector, or <see langword="null"/> for no limit of its own. Length values
    /// that exceed it or overflow when combined with the header size yield <see cref="PduBoundaryResult.Invalid"/>. With
    /// <see langword="null"/> the bound is the PDU limit of the stream (<see cref="StreamReassemblyConfig.MaxPduSize"/>, limit
    /// <see cref="CoreLimits.MaxStreamPduSize"/>), which the stream checks for every reported PDU length.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="lengthSize"/> is not 1, 2, or 4, when
    /// <paramref name="lengthOffset"/> or <paramref name="headerSize"/> is negative, when
    /// <paramref name="maxPduSize"/> is not positive, or when the resolved effective header size
    /// is smaller than <c>lengthOffset + lengthSize</c> (the length field would lie outside
    /// the header), or when <c>lengthOffset + lengthSize</c> exceeds <see cref="int.MaxValue"/>.
    /// </exception>
    public LengthPrefixDetector(int lengthOffset, int lengthSize, bool bigEndian = true,
        bool lengthIncludesHeader = false, int headerSize = 0, int? maxPduSize = null)
    {
        if (lengthSize is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize), lengthSize,
                "Length size must be 1, 2, or 4 bytes.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(lengthOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(headerSize);
        if (maxPduSize is { } explicitMax)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(explicitMax, nameof(maxPduSize));
        }

        // The length field must end inside the int range; otherwise lengthOffset + lengthSize wraps and Detect would
        // fail on every input.
        if (lengthOffset > int.MaxValue - lengthSize)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthOffset), lengthOffset,
                "lengthOffset + lengthSize must not exceed int.MaxValue.");
        }

        _LengthOffset = lengthOffset;
        _LengthSize = lengthSize;
        _BigEndian = bigEndian;
        _LengthIncludesHeader = lengthIncludesHeader;
        _MaxPduSize = maxPduSize ?? int.MaxValue;

        // Resolve the effective header size once at construction so Detect does not recompute it per call.
        // When the caller did not provide a header size, fall back to the minimum data length
        // required to read the length field. Any other choice would be guesswork.
        int minRequired = lengthOffset + lengthSize;
        _EffectiveHeaderSize = headerSize > 0
            ? headerSize
            : minRequired;

        // The shortest valid total PDU length: the header, and never less than the bytes up to the end of the length field
        // (a PDU is at least as long as the field it was announced by). Always at least 1.
        _MinPduLength = Math.Max(_EffectiveHeaderSize, minRequired);

        // If the caller specified a headerSize that is smaller than the bytes the length field
        // itself occupies, the configuration is internally inconsistent (the length field would
        // be outside the header). Reject it instead of silently producing wrong PDU sizes.
        if (!lengthIncludesHeader && headerSize > 0 && headerSize < minRequired)
        {
            throw new ArgumentOutOfRangeException(nameof(headerSize), headerSize,
                Invariant($"headerSize ({headerSize}) must be >= lengthOffset + lengthSize ({minRequired}) when lengthIncludesHeader is false."));
        }
    }

    #endregion

    #region Boundary detection

    /// <inheritdoc/>
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        // Wait until the whole length field is buffered; the sum cannot overflow (the constructor bounds it).
        int minRequired = _LengthOffset + _LengthSize;
        if (data.Length < minRequired)
        {
            return PduBoundaryResult.Incomplete;
        }

        // A length field that does not fit a non-negative int cannot be a real PDU size: resync instead of waiting.
        int pduLength = _ReadLength(data.Slice(_LengthOffset, _LengthSize));
        if (pduLength < 0)
        {
            return PduBoundaryResult.Invalid;
        }

        // Turn a payload length into a total PDU length. The sum is checked against int.MaxValue first so the addition
        // cannot wrap into a small or negative value that would pass the cap check below.
        if (!_LengthIncludesHeader)
        {
            // Effective header size is precomputed in the constructor.
            // Overflow here would exceed allocatable buffer bounds (Array.MaxLength).
            if (pduLength > int.MaxValue - _EffectiveHeaderSize)
            {
                return PduBoundaryResult.Invalid;
            }
            pduLength += _EffectiveHeaderSize;
        }

        // A PDU shorter than its own header is invalid: with a total length that includes the header, a value below the
        // header size (at least the length field itself) would describe a PDU that cannot contain the field it was read
        // from, and a zero-length PDU would never advance the stream. A length above the cap is rejected here, before the
        // owner waits for that many bytes.
        if (pduLength < _MinPduLength || pduLength > _MaxPduSize)
        {
            return PduBoundaryResult.Invalid;
        }

        return data.Length >= pduLength
            ? PduBoundaryResult.Complete(pduLength)
            : PduBoundaryResult.Incomplete;
    }

    #endregion

    #region Length field reading

    /// <summary>
    /// Reads the wire length field as a non-negative <see cref="int"/>, or -1 when invalid.
    /// Values above <see cref="int.MaxValue"/> are rejected because PDU buffers are arrays.
    /// </summary>
    /// <param name="data">Exactly the <see cref="_LengthSize"/> bytes of the length field.</param>
    /// <returns>The announced length, or -1 when it does not fit a non-negative int.</returns>
    private int _ReadLength(ReadOnlySpan<byte> data)
    {
        if (_LengthSize == 4)
        {
            uint raw = _ReadLengthU32(data);
            if (raw > int.MaxValue)
            {
                return -1;
            }
            return (int)raw;
        }

        return _LengthSize switch
        {
            1 => data[0],
            2 => _BigEndian
                ? (data[0] << 8) | data[1]
                : data[0] | (data[1] << 8),
            // Unreachable: constructor validates _LengthSize to 1, 2, or 4
            _ => -1,
        };
    }

    /// <summary>
    /// Reads four length bytes in the configured byte order as an unsigned value. The shifts run in <see cref="int"/>
    /// and a high first byte makes the intermediate negative; the cast to <see cref="uint"/> reinterprets those bits
    /// (unchecked context), which is why the caller compares against <see cref="int.MaxValue"/> afterwards.
    /// </summary>
    /// <param name="data">The four bytes of the length field.</param>
    /// <returns>The four bytes as an unsigned number in the configured byte order.</returns>
    private uint _ReadLengthU32(ReadOnlySpan<byte> data)
        => _BigEndian
            ? (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3])
            : (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

    #endregion
}
