// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// A buffer that can hold up to ~32 GB of contiguous byte data, bypassing the
/// .NET <c>byte[]</c> limit of ~2 GB. Internally backed by a <see cref="LargeBufferElement"/>
/// array (opaque to the caller), where each element stores 16 bytes (two <c>ulong</c> fields),
/// allowing up to <c>Array.MaxLength * 16</c> bytes in total.
/// <para>
/// Byte-level access is provided through <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/>
/// to reinterpret the <see cref="LargeBufferElement"/> array as <c>Span&lt;byte&gt;</c> windows.
/// Each window is limited to <c>int.MaxValue</c> bytes, but the total buffer capacity can exceed 2 GB.
/// </para>
/// <para>
/// <b>Intentional copy semantics — behaves like a large byte array:</b>
/// <see cref="LargeBuffer"/> is a value type wrapping a reference (array), by design.
/// Copying the struct (assignment, passing by value, storing in a field) creates an alias
/// that shares the same underlying array, exactly as copying a <c>byte[]</c> reference
/// would. Mutations through one copy are immediately visible through all other copies —
/// this is the intended behaviour, not a hazard. The design goal is that <see cref="LargeBuffer"/>
/// feels like a plain <c>byte[]</c> but without the 2 GB limit.
/// Use <see cref="Resize"/> only through a single owning reference, or pass
/// <c>ref LargeBuffer</c>, to avoid confusion about which copy owns the resize operation.
/// </para>
/// <para>
/// <b>Thread-safety:</b> Not thread-safe for concurrent mutation. Reads (<see cref="AsSpan(long, int)"/>,
/// <see cref="Length"/>) require external synchronization if any thread can call a mutating method
/// (write, <see cref="Resize"/>) concurrently. Concurrent reads after the buffer is published and
/// no further mutation occurs are safe.
/// </para>
/// </summary>
public struct LargeBuffer
{
    #region Constants

    /// <summary>
    /// Number of bytes per <see cref="LargeBufferElement"/> (two <c>ulong</c> fields = 16 bytes).
    /// </summary>
    private const int BytesPerElement = sizeof(ulong) * 2; // 16

    /// <summary>Right-shift amount equivalent to dividing by <see cref="BytesPerElement"/> (log2(16) = 4).</summary>
    private const int BytesPerElementShift = 4;

    /// <summary>Bitmask equivalent to modulo <see cref="BytesPerElement"/> (<c>BytesPerElement - 1 = 15</c>).</summary>
    private const int BytesPerElementMask = BytesPerElement - 1;

    #endregion

    #region Fields

    /// <summary>The backing storage. Each <see cref="LargeBufferElement"/> holds 16 bytes of data.</summary>
    private LargeBufferElement[] _Data;

    /// <summary>The logical byte length of the buffer (≤ <see cref="Capacity"/>).</summary>
    private long _Length;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new <see cref="LargeBuffer"/> with the specified byte capacity.
    /// All bytes are initialized to zero.
    /// </summary>
    /// <param name="capacity">
    /// The desired capacity in bytes. Must be ≥ 0 and ≤ <see cref="MaxCapacity"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacity"/> is negative or exceeds <see cref="MaxCapacity"/>.
    /// </exception>
    public LargeBuffer(long capacity)
    {
        if (capacity < 0 || capacity > MaxCapacity)
        {
            ThrowHelpers.ThrowArgumentOutOfRange(nameof(capacity));
        }

        int elementCount = ByteCountToElementCount(capacity);
        _Data = new LargeBufferElement[elementCount];
        _Length = capacity;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The maximum byte capacity a <see cref="LargeBuffer"/> can hold.
    /// Approximately 32 GB on 64-bit systems.
    /// </summary>
    public static long MaxCapacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (long)Array.MaxLength * BytesPerElement;
    }

    /// <summary>The logical byte length of the buffer.</summary>
    public readonly long Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Length;
    }

    /// <summary>
    /// The total byte capacity of the backing array.
    /// Always a multiple of 8 and ≥ <see cref="Length"/>.
    /// </summary>
    public readonly long Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (long)_Data.Length * BytesPerElement;
    }

    /// <summary>True when <see cref="Length"/> is zero.</summary>
    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Length == 0;
    }

    #endregion

    #region Indexer

    /// <summary>Gets or sets a single byte at the specified offset.</summary>
    /// <param name="index">Zero-based byte offset.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public byte this[long index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get
        {
            if ((ulong)index >= (ulong)_Length)
            {
                ThrowHelpers.ThrowArgumentOutOfRange(nameof(index));
            }

            // Compute which element contains this byte and its byte offset within the element (0–15).
            int elementIndex = (int)(index >> BytesPerElementShift);
            int byteOffset = (int)(index & BytesPerElementMask);

            // Low covers byte offsets 0–7; High covers 8–15.
            ref LargeBufferElement elem = ref _Data[elementIndex];
            ulong word = byteOffset < 8 ? elem.Low : elem.High;
            // The shift within the selected ulong uses only the lower 3 bits of byteOffset.
            return (byte)(word >> ((byteOffset & 7) * 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if ((ulong)index >= (ulong)_Length)
            {
                ThrowHelpers.ThrowArgumentOutOfRange(nameof(index));
            }

            int elementIndex = (int)(index >> BytesPerElementShift);
            int byteOffset = (int)(index & BytesPerElementMask);

            // The shift within the selected ulong uses only the lower 3 bits of byteOffset.
            int shift = (byteOffset & 7) * 8;
            ulong mask = (ulong)0xFF << shift;
            ulong newByte = (ulong)value << shift;

            // Low covers byte offsets 0–7; High covers 8–15.
            ref LargeBufferElement elem = ref _Data[elementIndex];
            if (byteOffset < 8)
            {
                elem.Low = (elem.Low & ~mask) | newByte;
            }
            else
            {
                elem.High = (elem.High & ~mask) | newByte;
            }
        }
    }

    #endregion

    #region Span Access

    /// <summary>
    /// Returns a writable <see cref="Span{Byte}"/> over a region of the buffer.
    /// </summary>
    /// <param name="offset">Zero-based byte offset into the buffer.</param>
    /// <param name="length">Number of bytes in the span. Must be ≤ <c>int.MaxValue</c>.</param>
    /// <returns>A <see cref="Span{Byte}"/> covering the specified region.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The region <c>[offset, offset + length)</c> exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<byte> AsSpan(long offset, int length)
    {
        ValidateRange(offset, length);
        return GetByteSpan(offset, length);
    }

    /// <summary>
    /// Returns a read-only <see cref="ReadOnlySpan{Byte}"/> over a region of the buffer.
    /// </summary>
    /// <param name="offset">Zero-based byte offset into the buffer.</param>
    /// <param name="length">Number of bytes in the span. Must be ≤ <c>int.MaxValue</c>.</param>
    /// <returns>A <see cref="ReadOnlySpan{Byte}"/> covering the specified region.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The region <c>[offset, offset + length)</c> exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> AsReadOnlySpan(long offset, int length)
    {
        ValidateRange(offset, length);
        return GetByteSpan(offset, length);
    }

    #endregion

    #region Bulk Operations

    /// <summary>
    /// Copies bytes from this buffer into the destination span.
    /// </summary>
    /// <param name="sourceOffset">Byte offset in this buffer to start copying from.</param>
    /// <param name="destination">Target span to copy bytes into.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The source region exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    public readonly void CopyTo(long sourceOffset, Span<byte> destination) =>
        AsReadOnlySpan(sourceOffset, destination.Length).CopyTo(destination);

    /// <summary>
    /// Copies bytes from the source span into this buffer.
    /// </summary>
    /// <param name="source">Source span to copy bytes from.</param>
    /// <param name="destinationOffset">Byte offset in this buffer to start writing at.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The destination region exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    public readonly void CopyFrom(ReadOnlySpan<byte> source, long destinationOffset) =>
        source.CopyTo(AsSpan(destinationOffset, source.Length));

    /// <summary>
    /// Sets all bytes in the specified region to zero.
    /// </summary>
    /// <param name="offset">Byte offset to start clearing.</param>
    /// <param name="length">Number of bytes to clear.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The region exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    public readonly void Clear(long offset, int length) =>
        AsSpan(offset, length).Clear();

    /// <summary>
    /// Fills the specified region with a single byte value.
    /// </summary>
    /// <param name="offset">Byte offset to start filling.</param>
    /// <param name="length">Number of bytes to fill.</param>
    /// <param name="value">The byte value to fill with.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The region exceeds the buffer's <see cref="Length"/>.
    /// </exception>
    public readonly void Fill(long offset, int length, byte value) =>
        AsSpan(offset, length).Fill(value);

    #endregion

    #region Read — Big Endian

    /// <summary>Reads a single byte at the specified offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte ReadByte(long offset) => AsReadOnlySpan(offset, 1)[0];

    /// <summary>Reads a big-endian 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort ReadUInt16BE(long offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(AsReadOnlySpan(offset, sizeof(ushort)));

    /// <summary>Reads a big-endian 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint ReadUInt32BE(long offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(AsReadOnlySpan(offset, sizeof(uint)));

    /// <summary>Reads a big-endian 64-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong ReadUInt64BE(long offset) =>
        BinaryPrimitives.ReadUInt64BigEndian(AsReadOnlySpan(offset, sizeof(ulong)));

    /// <summary>Reads a big-endian 16-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly short ReadInt16BE(long offset) =>
        BinaryPrimitives.ReadInt16BigEndian(AsReadOnlySpan(offset, sizeof(short)));

    /// <summary>Reads a big-endian 32-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int ReadInt32BE(long offset) =>
        BinaryPrimitives.ReadInt32BigEndian(AsReadOnlySpan(offset, sizeof(int)));

    /// <summary>Reads a big-endian 64-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long ReadInt64BE(long offset) =>
        BinaryPrimitives.ReadInt64BigEndian(AsReadOnlySpan(offset, sizeof(long)));

    /// <summary>Reads a big-endian 32-bit IEEE 754 float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float ReadSingleBE(long offset) =>
        BinaryPrimitives.ReadSingleBigEndian(AsReadOnlySpan(offset, sizeof(float)));

    /// <summary>Reads a big-endian 64-bit IEEE 754 double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double ReadDoubleBE(long offset) =>
        BinaryPrimitives.ReadDoubleBigEndian(AsReadOnlySpan(offset, sizeof(double)));

    #endregion

    #region Read — Little Endian

    /// <summary>Reads a little-endian 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ushort ReadUInt16LE(long offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(AsReadOnlySpan(offset, sizeof(ushort)));

    /// <summary>Reads a little-endian 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly uint ReadUInt32LE(long offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(AsReadOnlySpan(offset, sizeof(uint)));

    /// <summary>Reads a little-endian 64-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong ReadUInt64LE(long offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(AsReadOnlySpan(offset, sizeof(ulong)));

    /// <summary>Reads a little-endian 16-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly short ReadInt16LE(long offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(AsReadOnlySpan(offset, sizeof(short)));

    /// <summary>Reads a little-endian 32-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int ReadInt32LE(long offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(AsReadOnlySpan(offset, sizeof(int)));

    /// <summary>Reads a little-endian 64-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long ReadInt64LE(long offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(AsReadOnlySpan(offset, sizeof(long)));

    /// <summary>Reads a little-endian 32-bit IEEE 754 float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float ReadSingleLE(long offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(AsReadOnlySpan(offset, sizeof(float)));

    /// <summary>Reads a little-endian 64-bit IEEE 754 double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly double ReadDoubleLE(long offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(AsReadOnlySpan(offset, sizeof(double)));

    #endregion

    #region Write — Big Endian

    /// <summary>Writes a single byte at the specified offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteByte(long offset, byte value) => AsSpan(offset, 1)[0] = value;

    /// <summary>Writes a big-endian 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt16BE(long offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(AsSpan(offset, sizeof(ushort)), value);

    /// <summary>Writes a big-endian 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt32BE(long offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(AsSpan(offset, sizeof(uint)), value);

    /// <summary>Writes a big-endian 64-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt64BE(long offset, ulong value) =>
        BinaryPrimitives.WriteUInt64BigEndian(AsSpan(offset, sizeof(ulong)), value);

    /// <summary>Writes a big-endian 16-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt16BE(long offset, short value) =>
        BinaryPrimitives.WriteInt16BigEndian(AsSpan(offset, sizeof(short)), value);

    /// <summary>Writes a big-endian 32-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt32BE(long offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(AsSpan(offset, sizeof(int)), value);

    /// <summary>Writes a big-endian 64-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt64BE(long offset, long value) =>
        BinaryPrimitives.WriteInt64BigEndian(AsSpan(offset, sizeof(long)), value);

    /// <summary>Writes a big-endian 32-bit IEEE 754 float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteSingleBE(long offset, float value) =>
        BinaryPrimitives.WriteSingleBigEndian(AsSpan(offset, sizeof(float)), value);

    /// <summary>Writes a big-endian 64-bit IEEE 754 double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteDoubleBE(long offset, double value) =>
        BinaryPrimitives.WriteDoubleBigEndian(AsSpan(offset, sizeof(double)), value);

    #endregion

    #region Write — Little Endian

    /// <summary>Writes a little-endian 16-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt16LE(long offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(AsSpan(offset, sizeof(ushort)), value);

    /// <summary>Writes a little-endian 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt32LE(long offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(AsSpan(offset, sizeof(uint)), value);

    /// <summary>Writes a little-endian 64-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteUInt64LE(long offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(AsSpan(offset, sizeof(ulong)), value);

    /// <summary>Writes a little-endian 16-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt16LE(long offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(AsSpan(offset, sizeof(short)), value);

    /// <summary>Writes a little-endian 32-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt32LE(long offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(AsSpan(offset, sizeof(int)), value);

    /// <summary>Writes a little-endian 64-bit signed integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteInt64LE(long offset, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(AsSpan(offset, sizeof(long)), value);

    /// <summary>Writes a little-endian 32-bit IEEE 754 float.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteSingleLE(long offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(AsSpan(offset, sizeof(float)), value);

    /// <summary>Writes a little-endian 64-bit IEEE 754 double.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteDoubleLE(long offset, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(AsSpan(offset, sizeof(double)), value);

    #endregion

    #region Read — Bytes & Strings

    /// <summary>
    /// Returns a read-only span over the specified byte region.
    /// The span is valid only while no resize or GC compaction occurs.
    /// </summary>
    /// <param name="offset">Zero-based byte offset.</param>
    /// <param name="length">Number of bytes to read.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ReadOnlySpan<byte> ReadBytes(long offset, int length) =>
        AsReadOnlySpan(offset, length);

    /// <summary>Decodes a UTF-8 string from the specified region.</summary>
    /// <param name="offset">Zero-based byte offset.</param>
    /// <param name="length">Number of bytes to decode.</param>
    public readonly string ReadUtf8String(long offset, int length) =>
        Encoding.UTF8.GetString(AsReadOnlySpan(offset, length));

    /// <summary>Decodes an ASCII string from the specified region.</summary>
    /// <param name="offset">Zero-based byte offset.</param>
    /// <param name="length">Number of bytes to decode.</param>
    public readonly string ReadAsciiString(long offset, int length) =>
        Encoding.ASCII.GetString(AsReadOnlySpan(offset, length));

    /// <summary>Decodes a Latin-1 (ISO 8859-1) string from the specified region.</summary>
    /// <param name="offset">Zero-based byte offset.</param>
    /// <param name="length">Number of bytes to decode.</param>
    public readonly string ReadLatin1String(long offset, int length) =>
        Encoding.Latin1.GetString(AsReadOnlySpan(offset, length));

    #endregion

    #region Write — Bytes & Strings

    /// <summary>
    /// Writes the source bytes into the buffer at the specified offset.
    /// </summary>
    /// <param name="offset">Zero-based byte offset to start writing.</param>
    /// <param name="data">The bytes to write.</param>
    public readonly void WriteBytes(long offset, ReadOnlySpan<byte> data) =>
        CopyFrom(data, offset);

    /// <summary>
    /// Encodes the string as UTF-8 and writes it into the buffer.
    /// </summary>
    /// <param name="offset">Zero-based byte offset to start writing.</param>
    /// <param name="value">The string to encode and write.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The encoded byte sequence does not fit in the buffer starting at <paramref name="offset"/>.
    /// </exception>
    /// <remarks>
    /// Computes the actual encoded byte count via <see cref="Encoding.GetByteCount(string)"/>
    /// rather than the conservative <see cref="Encoding.GetMaxByteCount(int)"/>. This avoids
    /// spurious <see cref="ArgumentOutOfRangeException"/>s for tightly sized buffers where
    /// the worst-case estimate would exceed the remaining capacity but the real encoding fits.
    /// </remarks>
    public readonly int WriteUtf8String(long offset, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return Encoding.UTF8.GetBytes(value, AsSpan(offset, byteCount));
    }

    /// <summary>
    /// Encodes the string as ASCII and writes it into the buffer.
    /// </summary>
    /// <param name="offset">Zero-based byte offset to start writing.</param>
    /// <param name="value">The string to encode and write.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The encoded byte sequence does not fit in the buffer starting at <paramref name="offset"/>.
    /// </exception>
    /// <remarks>
    /// Computes the actual encoded byte count via <see cref="Encoding.GetByteCount(string)"/>
    /// rather than the conservative <see cref="Encoding.GetMaxByteCount(int)"/>. For ASCII
    /// the two values coincide, so the change is purely defensive but keeps the behaviour
    /// symmetrical with <see cref="WriteUtf8String"/>.
    /// </remarks>
    public readonly int WriteAsciiString(long offset, string value)
    {
        int byteCount = Encoding.ASCII.GetByteCount(value);
        return Encoding.ASCII.GetBytes(value, AsSpan(offset, byteCount));
    }

    #endregion

    #region Resize

    /// <summary>
    /// Resizes the buffer to a new capacity, preserving existing data up to
    /// <c>min(oldLength, newCapacity)</c>. Similar to <see cref="Array.Resize{T}"/>.
    /// </summary>
    /// <param name="buffer">The buffer to resize (passed by reference).</param>
    /// <param name="newCapacity">The new capacity in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="newCapacity"/> is negative or exceeds <see cref="MaxCapacity"/>.
    /// </exception>
    public static void Resize(ref LargeBuffer buffer, long newCapacity)
    {
        if (newCapacity < 0 || newCapacity > MaxCapacity)
        {
            ThrowHelpers.ThrowArgumentOutOfRange(nameof(newCapacity));
        }

        int newElementCount = ByteCountToElementCount(newCapacity);
        LargeBufferElement[] newData = new LargeBufferElement[newElementCount];

        // Copy existing data element-wise, preserving all bytes up to the smaller of the two sizes.
        int elementsToCopy = Math.Min(buffer._Data.Length, newElementCount);
        Array.Copy(buffer._Data, newData, elementsToCopy);

        buffer._Data = newData;
        buffer._Length = newCapacity;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Converts a byte count to the number of <see cref="LargeBufferElement"/> entries needed
    /// (ceiling division by <see cref="BytesPerElement"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ByteCountToElementCount(long byteCount) =>
        (int)((byteCount + BytesPerElementMask) >> BytesPerElementShift);

    /// <summary>
    /// Validates that the region <c>[offset, offset + length)</c> is within bounds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void ValidateRange(long offset, int length)
    {
        // Single unsigned comparison catches negative offset and overflow
        if ((ulong)(offset + length) > (ulong)_Length || offset < 0)
        {
            ThrowHelpers.ThrowArgumentOutOfRange(nameof(offset));
        }
    }

    /// <summary>
    /// Returns a <see cref="Span{Byte}"/> window into the backing <c>ulong[]</c> at the
    /// specified byte offset and length, using <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/>.
    /// </summary>
    /// <remarks>
    /// The method computes which <c>ulong</c> elements are covered by the byte region,
    /// casts that slice to <c>Span&lt;byte&gt;</c>, then returns the sub-slice at the
    /// correct byte remainder offset.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly Span<byte> GetByteSpan(long offset, int length)
    {
        if (length == 0)
        {
            return Span<byte>.Empty;
        }

        // Determine which LargeBufferElement entries span the requested byte range.
        long elementStart = offset >> BytesPerElementShift;
        int byteRemainder = (int)(offset & BytesPerElementMask);
        int elementsNeeded = (byteRemainder + length + BytesPerElementMask) >> BytesPerElementShift;

        // Cast the relevant element slice to bytes, then slice to the exact byte range.
        Span<LargeBufferElement> elemSpan = _Data.AsSpan((int)elementStart, elementsNeeded);
        Span<byte> byteSpan = MemoryMarshal.Cast<LargeBufferElement, byte>(elemSpan);
        return byteSpan.Slice(byteRemainder, length);
    }

    #endregion
}