// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

    /// <summary>Creates a memory-mapped backend from a file path.</summary>
    internal static DataBackend FromMmap(string path, int poolSize = 0) =>
        new(new MmapPool(path, poolSize));

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
    /// Reads frame data for a specific frame. Thread-safe for random access.
    /// For in-memory: returns a memory slice (zero-copy).
    /// For mmap: returns a pooled read (allocated copy).
    /// </summary>
    internal ReadOnlyMemory<byte> ReadFrameData(int frameId, long fileOffset, int capturedLength) =>
        _InMemoryData != null
            ? new ReadOnlyMemory<byte>(_InMemoryData, (int)fileOffset, capturedLength)
            : _MmapPool!.ReadAt(frameId, fileOffset, capturedLength);

    #endregion

    #region IDisposable

    /// <summary>
    /// Whether this instance has been disposed.
    /// Read/written via <see cref="Volatile"/> because <see cref="Dispose"/> may be
    /// invoked from a different thread than concurrent readers (per SOURCE_GUIDE §13.1).
    /// </summary>
    private bool _Disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
        {
            return;
        }

        Volatile.Write(ref _Disposed, true);
        _MmapPool?.Dispose();
    }

    #endregion
}
