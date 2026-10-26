// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Reads primitive values from byte spans with configurable byte order.
/// Calls the correct <see cref="BinaryPrimitives"/> method directly
/// (big-endian or little-endian) instead of reading LE and then reversing,
/// eliminating redundant operations.
/// </summary>
/// <remarks>
/// Constructed once from a byte-swap detection flag (e.g., from PCAP/PCAPNG
/// magic number detection), then reused for all reads within the same context.
/// <para>
/// The struct stores two flags: <c>_BigEndian</c> for span reads (determines which
/// <see cref="BinaryPrimitives"/> method to call) and <c>_NeedSwap</c> for reversing
/// values already loaded in native machine order (e.g., from MemoryMarshal-loaded structs).
/// On little-endian machines (all modern x86/ARM/WASM targets), both flags have the
/// same value. Both are instance fields, not constants, so the JIT cannot fold the branches away; each read costs
/// one well-predicted branch on a readonly field (the byte order is decided once per file, not per value). The
/// alternative of always reading little-endian and reversing afterwards would cost an extra byte reversal for every
/// big-endian value.
/// </para>
/// <para>
/// All span reads follow the <see cref="BinaryPrimitives"/> contract: a span shorter than the value throws
/// <see cref="ArgumentOutOfRangeException"/> (no validation is added here, the check is not repeated per read).
/// No read allocates.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Immutable <c>readonly struct</c>. Instances may be shared and
/// invoked concurrently from any number of threads.
/// </para>
/// </remarks>
/// <param name="byteSwapped">
/// True when the source byte order differs from the machine byte order.
/// For PCAP/PCAPNG this is determined from the magic number during format detection.
/// </param>
public readonly struct EndianReader(bool byteSwapped)
{
    /// <summary>
    /// True when the source data is in big-endian byte order. Derived from the machine order: a source whose order
    /// differs from the machine's is big-endian on a little-endian machine and little-endian on a big-endian machine.
    /// </summary>
    private readonly bool _BigEndian = BitConverter.IsLittleEndian == byteSwapped;

    /// <summary>
    /// True when an already-loaded native value needs byte reversal.
    /// Equals the raw <c>byteSwapped</c> detection flag.
    /// </summary>
    private readonly bool _NeedSwap = byteSwapped;

    #region Span read methods

    // Each read picks the matching BinaryPrimitives method directly; all are tiny, hence AggressiveInlining, so the
    // call in a parse loop compiles to a load plus the byte-order branch.

    /// <summary>Reads a 16-bit unsigned integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 2 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadU16(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data)
            : BinaryPrimitives.ReadUInt16LittleEndian(data);

    /// <summary>Reads a 32-bit unsigned integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 4 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadU32(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);

    /// <summary>Reads a 64-bit unsigned integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 8 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadU64(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(data)
            : BinaryPrimitives.ReadUInt64LittleEndian(data);

    /// <summary>Reads a 16-bit signed integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 2 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadI16(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data)
            : BinaryPrimitives.ReadInt16LittleEndian(data);

    /// <summary>Reads a 32-bit signed integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 4 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadI32(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data)
            : BinaryPrimitives.ReadInt32LittleEndian(data);

    /// <summary>Reads a 64-bit signed integer from the start of the span.</summary>
    /// <param name="data">Source bytes; at least 8 bytes (otherwise <see cref="ArgumentOutOfRangeException"/>).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadI64(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(data)
            : BinaryPrimitives.ReadInt64LittleEndian(data);

    #endregion

    #region Swap methods

    // For values that were already loaded in native order (for example a struct filled by MemoryMarshal): the bytes
    // are reversed only when the source order differs from the machine order. Overloads exist only for the types
    // listed below (16- and 32-bit unsigned, 64-bit unsigned and signed).

    /// <summary>Conditionally reverses the byte order of an already-loaded 16-bit value.</summary>
    /// <param name="value">Value in native machine order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Swap(ushort value)
    {
        if (_NeedSwap)
        {
            return BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }

    /// <summary>Conditionally reverses the byte order of an already-loaded 32-bit value.</summary>
    /// <param name="value">Value in native machine order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Swap(uint value)
    {
        if (_NeedSwap)
        {
            return BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }

    /// <summary>Conditionally reverses the byte order of an already-loaded 64-bit unsigned value.</summary>
    /// <param name="value">Value in native machine order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Swap(ulong value)
    {
        if (_NeedSwap)
        {
            return BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }

    /// <summary>Conditionally reverses the byte order of an already-loaded 64-bit signed value.</summary>
    /// <param name="value">Value in native machine order.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Swap(long value)
    {
        if (_NeedSwap)
        {
            return BinaryPrimitives.ReverseEndianness(value);
        }
        return value;
    }
    #endregion
}
