// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// A single raw option parsed from a PCAPNG option TLV block.
/// </summary>
internal readonly ref struct RawOption
{
    /// <summary>Option code. 0 = end-of-options, 1 = comment, others block-specific.</summary>
    internal ushort Code
    {
        get;
    }

    /// <summary>Raw option value bytes (without padding).</summary>
    internal ReadOnlySpan<byte> Value
    {
        get;
    }

    /// <summary>Creates a new raw option with the given code and value.</summary>
    internal RawOption(ushort code, ReadOnlySpan<byte> value)
    {
        Code = code;
        Value = value;
    }
}

/// <summary>
/// Zero-allocation iterator over PCAPNG options within a block body.
/// Options are stored as TLV (Type-Length-Value) entries, each padded to 4-byte boundaries.
/// Iteration stops upon encountering opt_endofopt (code=0), truncated data, or data exhaustion.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// PcapOptionIterator iter = new(optionData, swap);
/// while (iter.TryGetNext(out RawOption option))
/// {
///     // process option.Code, option.Value
/// }
/// </code>
/// </remarks>
internal ref struct PcapOptionIterator
{
    #region Fields

    /// <summary>The raw option data being iterated.</summary>
    private readonly ReadOnlySpan<byte> _Data;

    /// <summary>Current byte offset within the data.</summary>
    private int _Offset;

    /// <summary>Reader for byte-order-aware field parsing.</summary>
    private readonly EndianReader _Reader;

    #endregion

    #region Constructors

    /// <summary>Creates an iterator over option data with the specified byte-swap flag.</summary>
    internal PcapOptionIterator(ReadOnlySpan<byte> data, bool swap)
    {
        _Data = data;
        _Offset = 0;
        _Reader = new EndianReader(swap);
    }

    /// <summary>Creates an iterator over option data in native byte order.</summary>
    internal PcapOptionIterator(ReadOnlySpan<byte> data) : this(data, false) { }

    #endregion

    #region Public API

    /// <summary>
    /// Tries to read the next option. Returns false when there are no more options
    /// (end-of-options reached, data exhausted, or truncated entry encountered).
    /// </summary>
    internal bool TryGetNext(out RawOption option)
    {
        // Need at least 4 bytes for the option header (code + length)
        if (_Offset + 4 > _Data.Length)
        {
            option = default;
            return false;
        }

        // Read option header fields
        ushort code = _Reader.ReadU16(_Data[_Offset..]);
        ushort length = _Reader.ReadU16(_Data[(_Offset + 2)..]);

        // End-of-options sentinel
        if (code == PcapConstants.OptEndOfOpt)
        {
            option = default;
            return false;
        }

        // Advance past the 4-byte option header
        _Offset += 4;

        // Validate value region
        if (_Offset + length > _Data.Length)
        {
            // Truncated option — stop iteration
            option = default;
            return false;
        }

        // Extract the unpadded value
        ReadOnlySpan<byte> value = _Data.Slice(_Offset, length);
        option = new RawOption(code, value);

        // Advance past the padded value
        _Offset += PcapPadding.PaddedLength(length);
        return true;
    }

    #endregion
}
