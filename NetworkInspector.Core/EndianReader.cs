// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
/// same value and the JIT eliminates the dead branch entirely.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Immutable <c>readonly struct</c>. Instances may be shared and
/// invoked concurrently from any number of threads.
/// </para>
/// </remarks>
/// <remarks>
/// Creates an <see cref="EndianReader"/> from a byte-swap detection flag.
/// </remarks>
/// <param name="byteSwapped">
/// True when the source byte order differs from the machine byte order.
/// For PCAP/PCAPNG this is determined from the magic number during format detection.
/// </param>
public readonly struct EndianReader(bool byteSwapped)
{
    /// <summary>True when the source data is in big-endian byte order.</summary>
    private readonly bool _BigEndian = BitConverter.IsLittleEndian == byteSwapped;

    /// <summary>
    /// True when an already-loaded native value needs byte reversal.
    /// Equals the raw <c>byteSwapped</c> detection flag.
    /// </summary>
    private readonly bool _NeedSwap = byteSwapped;

    #region Span read methods

    /// <summary>Reads a 16-bit unsigned integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadU16(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data)
            : BinaryPrimitives.ReadUInt16LittleEndian(data);

    /// <summary>Reads a 32-bit unsigned integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadU32(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);

    /// <summary>Reads a 64-bit unsigned integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadU64(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(data)
            : BinaryPrimitives.ReadUInt64LittleEndian(data);

    /// <summary>Reads a 16-bit signed integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadI16(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data)
            : BinaryPrimitives.ReadInt16LittleEndian(data);

    /// <summary>Reads a 32-bit signed integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadI32(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data)
            : BinaryPrimitives.ReadInt32LittleEndian(data);
    /// <summary>Reads a 64-bit signed integer from the start of the span.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadI64(ReadOnlySpan<byte> data) =>
        _BigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(data)
            : BinaryPrimitives.ReadInt64LittleEndian(data);

    #endregion

    #region Swap methods

    /// <summary>Conditionally reverses the byte order of an already-loaded 16-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Swap(ushort value) =>
        _NeedSwap ? BinaryPrimitives.ReverseEndianness(value) : value;

    /// <summary>Conditionally reverses the byte order of an already-loaded 32-bit value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Swap(uint value) =>
        _NeedSwap ? BinaryPrimitives.ReverseEndianness(value) : value;

    /// <summary>Conditionally reverses the byte order of an already-loaded 64-bit unsigned value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Swap(ulong value) =>
        _NeedSwap ? BinaryPrimitives.ReverseEndianness(value) : value;

    /// <summary>Conditionally reverses the byte order of an already-loaded 64-bit signed value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Swap(long value) =>
        _NeedSwap ? BinaryPrimitives.ReverseEndianness(value) : value;
    #endregion
}
