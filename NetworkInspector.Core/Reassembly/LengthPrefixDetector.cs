// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
/// </summary>
public sealed class LengthPrefixDetector : IPduBoundaryDetector
{
    #region Fields

    private readonly int _LengthOffset;
    private readonly int _LengthSize; // bytes: 1, 2, or 4
    private readonly bool _BigEndian;
    private readonly bool _LengthIncludesHeader;
    private readonly int _HeaderSize;
    private readonly int _EffectiveHeaderSize;

    #endregion

    #region Constructors

    /// <summary>Creates a length-prefix based PDU boundary detector.</summary>
    /// <param name="lengthOffset">Byte offset of the length field within the PDU header.</param>
    /// <param name="lengthSize">Width of the length field in bytes. Must be 1, 2, or 4.</param>
    /// <param name="bigEndian">Endianness of the length field.</param>
    /// <param name="lengthIncludesHeader">
    /// If <c>true</c>, the length value already covers the header bytes; if <c>false</c>, the
    /// detector adds the effective header size (see <paramref name="headerSize"/>).
    /// </param>
    /// <param name="headerSize">
    /// Total header size in bytes. Only consulted when <paramref name="lengthIncludesHeader"/>
    /// is <c>false</c>. A value of <c>0</c> defaults to <c>lengthOffset + lengthSize</c>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="lengthSize"/> is not 1, 2, or 4, when
    /// <paramref name="lengthOffset"/> or <paramref name="headerSize"/> is negative, or when
    /// the resolved effective header size is smaller than <c>lengthOffset + lengthSize</c>
    /// (the length field would lie outside the header).
    /// </exception>
    public LengthPrefixDetector(int lengthOffset, int lengthSize, bool bigEndian = true,
        bool lengthIncludesHeader = false, int headerSize = 0)
    {
        if (lengthSize is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(lengthSize), lengthSize,
                "Length size must be 1, 2, or 4 bytes.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(lengthOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(headerSize);

        _LengthOffset = lengthOffset;
        _LengthSize = lengthSize;
        _BigEndian = bigEndian;
        _LengthIncludesHeader = lengthIncludesHeader;
        _HeaderSize = headerSize;

        // Resolve the effective header size once at construction so Detect is branch-free.
        // When the caller did not provide a header size, fall back to the minimum data length
        // required to read the length field. Any other choice would be guesswork.
        int minRequired = lengthOffset + lengthSize;
        _EffectiveHeaderSize = headerSize > 0 ? headerSize : minRequired;

        // If the caller specified a headerSize that is smaller than the bytes the length field
        // itself occupies, the configuration is internally inconsistent (the length field would
        // be outside the header). Reject it instead of silently producing wrong PDU sizes.
        if (!lengthIncludesHeader && headerSize > 0 && headerSize < minRequired)
        {
            throw new ArgumentOutOfRangeException(nameof(headerSize), headerSize,
                $"headerSize ({headerSize}) must be >= lengthOffset + lengthSize ({minRequired}) when lengthIncludesHeader is false.");
        }
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public PduBoundaryResult Detect(ReadOnlySpan<byte> data)
    {
        int minRequired = _LengthOffset + _LengthSize;
        if (data.Length < minRequired)
        {
            return PduBoundaryResult.Incomplete;
        }

        int pduLength = ReadLength(data.Slice(_LengthOffset, _LengthSize));
        if (!_LengthIncludesHeader)
        {
            // Effective header size is precomputed in the constructor — keeps Detect branch-free.
            pduLength += _EffectiveHeaderSize;
        }

        return data.Length >= pduLength
            ? PduBoundaryResult.Complete(pduLength)
            : PduBoundaryResult.Incomplete;
    }

    #endregion

    #region Private Helpers

    private int ReadLength(ReadOnlySpan<byte> data) => _LengthSize switch
    {
        1 => data[0],
        2 => _BigEndian
            ? (data[0] << 8) | data[1]
            : data[0] | (data[1] << 8),
        4 => _BigEndian
            ? (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]
            : data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24),
        // Unreachable: constructor validates _LengthSize to 1, 2, or 4
        _ => throw new InvalidOperationException($"Unsupported length size: {_LengthSize}"),
    };

    #endregion
}