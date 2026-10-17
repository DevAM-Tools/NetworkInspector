// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Abstraction over the data backing a capture file — either a pre-loaded
/// in-memory byte array or a memory-mapped file pool.
/// </summary>
internal sealed class DataBackend : IDisposable
{
    #region Fields

    /// <summary>In-memory data (null when using mmap).</summary>
    private readonly byte[]? _InMemoryData;

    /// <summary>Memory-mapped file pool (null when using in-memory).</summary>
    private readonly MmapPool? _MmapPool;

    #endregion

    #region Properties

    /// <summary>File size in bytes.</summary>
    internal long FileSize
    {
        get;
    }

    /// <summary>Whether this backend uses in-memory data.</summary>
    internal bool IsInMemory => _InMemoryData is not null;

    #endregion

    #region Constructors

    /// <summary>Creates an in-memory backend from pre-loaded data.</summary>
    private DataBackend(byte[] data)
    {
        _InMemoryData = data;
        FileSize = data.Length;
    }

    /// <summary>Creates a memory-mapped backend from a pool.</summary>
    private DataBackend(MmapPool pool)
    {
        _MmapPool = pool;
        FileSize = pool.FileSize;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates an in-memory backend from pre-loaded data.</summary>
    internal static DataBackend FromMemory(byte[] data) => new(data);

    /// <summary>Creates a memory-mapped backend from a file path (one read-only view for all readers).</summary>
    internal static DataBackend FromMmap(string path) =>
        new(new MmapPool(path));

    #endregion

    #region Public API

    /// <summary>
    /// Gets a span over the backing data for scanning.
    /// For in-memory: returns a slice of the byte array.
    /// For mmap: returns a span over the primary mmap view (only valid during scan).
    /// </summary>
    internal ReadOnlySpan<byte> GetScanSpan(long offset, int length)
    {
        if (_InMemoryData is not null)
        {
            // Validate offset is within bounds before computing safe slice length
            if (offset < 0 || offset >= _InMemoryData.Length)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            int safeLength = (int)Math.Min(length, _InMemoryData.Length - offset);
            if (safeLength <= 0)
            {
                return ReadOnlySpan<byte>.Empty;
            }
            return _InMemoryData.AsSpan((int)offset, safeLength);
        }

        return _MmapPool!.GetPrimarySpan(offset, length);
    }

    /// <summary>
    /// Starts one sequential step that reads through <see cref="GetScanSpan"/> or <see cref="CopyFrameDataSequential"/>:
    /// the mapped backend takes a reference on its view so a concurrent <see cref="Dispose"/> cannot unmap it mid-copy.
    /// In-memory data needs no lease (always true). False means the backend is being disposed: read nothing.
    /// Every true result needs exactly one <see cref="ReleaseScanLease"/>.
    /// </summary>
    internal bool TryAcquireScanLease() => _MmapPool is null || _MmapPool.TryAcquireScanLease();

    /// <summary>Ends a step started by <see cref="TryAcquireScanLease"/>.</summary>
    internal void ReleaseScanLease() => _MmapPool?.ReleaseScanLease();

    /// <summary>
    /// Reads the bytes of one frame for random access. Thread-safe, also concurrently with <see cref="Dispose"/>.
    /// For in-memory: returns a memory slice (zero-copy; the frame aliases the loaded file).
    /// For mmap: copies the bytes out of the mapping into a new array (reference-counted against unmap).
    /// </summary>
    /// <param name="fileOffset">File offset of the first frame byte.</param>
    /// <param name="capturedLength">Number of frame bytes.</param>
    internal ReadOnlyMemory<byte> ReadFrameData(long fileOffset, int capturedLength) =>
        _InMemoryData != null
            ? new ReadOnlyMemory<byte>(_InMemoryData, (int)fileOffset, capturedLength)
            : _MmapPool!.ReadAt(fileOffset, capturedLength);

    /// <summary>
    /// Reads the bytes of one frame on the sequential (scanning) thread.
    /// For in-memory: returns a memory slice (zero-copy). For mmap: copies from the pinned view the scanner reads
    /// (<see cref="GetScanSpan"/>, no per-frame reference counting), into a slab region when <paramref name="slabs"/> is
    /// set, otherwise into a new array. Must only run on the thread that scans; the owning source keeps scan and dispose
    /// apart exactly as for <see cref="GetScanSpan"/>.
    /// </summary>
    /// <param name="fileOffset">File offset of the first frame byte.</param>
    /// <param name="capturedLength">Number of frame bytes; the index guarantees they lie inside the file.</param>
    /// <param name="slabs">Frame slab allocator of the sequential reader, or null (per-frame array).</param>
    internal ReadOnlyMemory<byte> CopyFrameDataSequential(long fileOffset, int capturedLength, FrameSlabAllocator? slabs)
    {
        if (_InMemoryData != null)
        {
            return new ReadOnlyMemory<byte>(_InMemoryData, (int)fileOffset, capturedLength);
        }

        ReadOnlySpan<byte> source = _MmapPool!.GetPrimarySpan(fileOffset, capturedLength);
        if (slabs is null)
        {
            return source.ToArray();
        }

        // The index only holds frames that lie inside the file, so the span covers every byte of the region.
        Memory<byte> region = slabs.Allocate(source.Length);
        source.CopyTo(region.Span);
        return region;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Atomic dispose latch (0 = live, 1 = disposed).
    /// Read/written via <see cref="Interlocked"/> / <see cref="Volatile"/> because <see cref="Dispose"/> may be
    /// invoked from a different thread than concurrent readers (per SOURCE_GUIDE §13.1).
    /// </summary>
    private volatile int _Disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _MmapPool?.Dispose();
    }

    #endregion
}
