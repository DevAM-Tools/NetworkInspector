// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Compact per-frame metadata for the BLF frame index.
/// Stores the information needed to locate and reconstruct a frame's data
/// from the BLF file: which container holds it, where within the container,
/// and its bus/channel classification.
///
/// Layout (32 bytes, sequential):
///   ContainerOffset  (8B) — file offset of the container holding this object
///   ObjectOffset     (4B) — byte offset within the decompressed container
///   ObjectLength     (4B) — total object byte length in the container
///   ObjectType       (4B) — BLF object type (CAN, Ethernet, etc.)
///   Channel          (2B) — BLF channel number
///   HeaderSize       (2B) — total header size (block + log object)
///   TimestampNanos   (8B) — resolved timestamp in nanoseconds
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BlfFrameEntry
{
    /// <summary>File offset of the container (or raw object) that holds this frame.</summary>
    internal long ContainerOffset;

    /// <summary>
    /// Byte offset of this object within the decompressed container data,
    /// or <c>-1</c> if this entry represents a raw (non-container) object stored
    /// directly in the file at <see cref="ContainerOffset"/>.
    /// </summary>
    internal int ObjectOffset;

    /// <summary>Total object length in the container (headers + payload).</summary>
    internal int ObjectLength;

    /// <summary>BLF object type identifier.</summary>
    internal uint ObjectType;

    /// <summary>Channel number.</summary>
    internal ushort Channel;

    /// <summary>Total header size (block header + log object header).</summary>
    internal ushort HeaderSize;

    /// <summary>Resolved timestamp in nanoseconds (relative or absolute).</summary>
    internal long TimestampNanos;
}

/// <summary>
/// Growable index of BLF frame entries stored in a single <see cref="BlfFrameEntry"/> array.
/// Each entry contains both the location metadata and the timestamp, so the array is the
/// sole authoritative storage — there is no split-array layout.
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> A single writer (the scanner) calls <see cref="Push"/>; concurrent
/// readers may call <see cref="Count"/> and <see cref="GetEntry"/> at any time. The writer
/// publishes growth via <see cref="System.Threading.Volatile"/> Write on both the array reference
/// and the count, and readers take a single <see cref="System.Threading.Volatile"/> Read snapshot of
/// the array per access. Each entry is fully written before the count is published, so any
/// index <c>i &lt; Count</c> observed by a reader is guaranteed to be initialised on the
/// array snapshot the reader holds.
/// </remarks>
internal sealed class BlfFrameIndex
{
    #region Fields

    private BlfFrameEntry[] _Entries;
    private int _Count;

    #endregion

    #region Constructors

    /// <summary>Creates a new BLF frame index with an initial capacity.</summary>
    internal BlfFrameIndex(int initialCapacity = 1024)
    {
        _Entries = new BlfFrameEntry[Math.Max(initialCapacity, 16)];
        _Count = 0;
    }

    #endregion

    #region Properties

    /// <summary>Number of indexed frames. Safe for concurrent readers.</summary>
    internal int Count => Volatile.Read(ref _Count);

    /// <summary>Whether the index has reached its maximum capacity of <see cref="int.MaxValue"/> entries.</summary>
    internal bool IsFull => Volatile.Read(ref _Count) == int.MaxValue;

    #endregion

    #region Public API

    /// <summary>
    /// Appends a frame entry to the index. Single-writer only.
    /// Returns <c>false</c> if the index is full (<see cref="int.MaxValue"/> entries).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Push(in BlfFrameEntry entry)
    {
        int count = _Count;
        if (count == int.MaxValue)
        {
            return false;
        }

        if (count == _Entries.Length)
        {
            Grow();
        }
        _Entries[count] = entry;
        Volatile.Write(ref _Count, count + 1);
        return true;
    }

    /// <summary>Returns a readonly reference to the entry at the given frame index. Safe for concurrent readers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly BlfFrameEntry GetEntry(int index) => ref Volatile.Read(ref _Entries)[index];

    /// <summary>Trims the internal array to exactly fit the current count. Single-writer only.</summary>
    internal void ShrinkToFit()
    {
        int count = _Count;
        if (count < _Entries.Length)
        {
            BlfFrameEntry[] trimmed = new BlfFrameEntry[count];
            Array.Copy(_Entries, trimmed, count);
            Volatile.Write(ref _Entries, trimmed);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Doubles the internal capacity (saturating at <see cref="int.MaxValue"/>) and publishes
    /// the new array via <see cref="System.Threading.Volatile"/> Write so concurrent readers never observe
    /// a partially copied array.
    /// </summary>
    private void Grow()
    {
        long doubled = (long)_Entries.Length * 2;
        long target = Math.Max(doubled, 1024);
        int newCapacity = (int)Math.Min(target, int.MaxValue);
        BlfFrameEntry[] newEntries = new BlfFrameEntry[newCapacity];
        Array.Copy(_Entries, newEntries, _Count);
        Volatile.Write(ref _Entries, newEntries);
    }

    #endregion
}
