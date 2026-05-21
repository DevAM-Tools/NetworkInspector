// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Abstraction over the data backing a BLF file — either a pre-loaded in-memory byte array
/// or a memory-mapped file.
///
/// Design rationale:
/// <list type="bullet">
/// <item>
///   <b>In-memory:</b> the entire file is loaded into a byte array at open time.
///   All span operations are zero-copy slices into this array.
///   Chosen when the file size is within <see cref="BlfSourceOptions.PreloadBudget"/>.
/// </item>
/// <item>
///   <b>Memory-mapped:</b> the OS memory-maps the file. Sequential scanning uses a primary
///   <see cref="System.IO.MemoryMappedFiles.MemoryMappedViewAccessor"/> (lock-free span reads);
///   concurrent random-access reads use a pool of per-slot accessors.
///   Chosen when the file size exceeds <see cref="BlfSourceOptions.PreloadBudget"/>.
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Thread-safety:</b> <see cref="GetSpan"/> is safe to call from multiple threads
/// concurrently because both the in-memory array and the mmap primary view are read-only
/// once created. The mmap primary view's pointer is acquired and released on each call
/// but the underlying memory remains valid for the lifetime of this instance.
/// </remarks>
internal sealed class BlfDataBackend : IDisposable
{
    #region Fields

    /// <summary>In-memory data (null when using mmap).</summary>
    private readonly byte[]? _InMemoryData;

    /// <summary>Memory-mapped file pool (null when using in-memory).</summary>
    private readonly MmapPool? _MmapPool;

    /// <summary>
    /// Whether this instance has been disposed.
    /// Read/written via <see cref="Volatile"/> because <see cref="Dispose"/> may be
    /// invoked from a different thread than concurrent readers (per SOURCE_GUIDE §13.1).
    /// </summary>
    private bool _Disposed;

    #endregion

    #region Properties

    /// <summary>Total file size in bytes.</summary>
    internal long FileSize
    {
        get;
    }

    /// <summary>Whether this backend uses an in-memory byte array.</summary>
    internal bool IsInMemory => _InMemoryData is not null;

    #endregion

    #region Constructors

    /// <summary>Creates an in-memory backend from a pre-loaded byte array.</summary>
    private BlfDataBackend(byte[] data)
    {
        _InMemoryData = data;
        FileSize = data.Length;
    }

    /// <summary>Creates a memory-mapped backend from a file pool.</summary>
    private BlfDataBackend(MmapPool pool)
    {
        _MmapPool = pool;
        FileSize = pool.FileSize;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates an in-memory backend from a pre-loaded byte array.</summary>
    internal static BlfDataBackend FromMemory(byte[] data) => new(data);

    /// <summary>
    /// Creates a memory-mapped backend for the given file.
    /// </summary>
    /// <param name="path">Path to the BLF file.</param>
    /// <param name="slotCount">
    /// Number of random-access pool slots.
    /// <c>0</c> selects <see cref="Environment.ProcessorCount"/>, clamped to [1, 256].
    /// </param>
    internal static BlfDataBackend FromMmap(string path, int slotCount = 0) =>
        new(new MmapPool(path, slotCount));

    #endregion

    #region Public API

    /// <summary>
    /// Returns a read-only span over a region of the backing data.
    /// Safe to call from multiple threads concurrently.
    /// </summary>
    /// <param name="offset">Byte offset within the file.</param>
    /// <param name="length">Maximum number of bytes to return.</param>
    /// <returns>
    /// A span over the requested bytes, truncated to the available data.
    /// Returns <see cref="ReadOnlySpan{T}.Empty"/> when <paramref name="offset"/>
    /// is out of range or the backend has been disposed.
    /// </returns>
    /// <remarks>
    /// <b>Span lifetime:</b> the returned span is valid only while the caller
    /// holds the <see cref="BlfSource"/>._LifetimeLock read lock (or, for in-memory
    /// backends, until the backing array is garbage-collected). Callers must never escape
    /// the span beyond the lock scope or store it in a field. For mmap backends the
    /// pointer is borrowed from the OS-mapped view; the pinned pointer is released as
    /// part of <see cref="MmapPool.GetPrimarySpan"/>'s internal accounting, and the
    /// backing pages remain valid until <see cref="Dispose"/> is called, which the
    /// write lock in <see cref="BlfSource.Dispose"/> ensures cannot happen concurrently
    /// with any caller still inside the read lock.
    /// </remarks>
    internal ReadOnlySpan<byte> GetSpan(long offset, int length)
    {
        // Guard against reading after dispose. Between a disposed-check in the
        // caller and this entry point a racing Dispose() could have released the mmap
        // pointer. The Volatile read gives the correct observed value without the full
        // overhead of the _LifetimeLock read lock that FrameById acquires externally.
        if (Volatile.Read(ref _Disposed))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (_InMemoryData is not null)
        {
            if (offset < 0 || offset >= _InMemoryData.Length)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            int safeLength = (int)Math.Min(length, _InMemoryData.Length - offset);
            return safeLength <= 0
                ? ReadOnlySpan<byte>.Empty
                : _InMemoryData.AsSpan((int)offset, safeLength);
        }

        // Snapshot before use so that a parallel Dispose() cannot null _MmapPool
        // between the null-conditional read and the method call.
        MmapPool? pool = _MmapPool;
        return pool is null ? ReadOnlySpan<byte>.Empty : pool.GetPrimarySpan(offset, length);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Volatile.Read(ref _Disposed))
        {
            return;
        }

        Volatile.Write(ref _Disposed, true);

        // Do not swallow exceptions silently. MmapPool.Dispose() is expected
        // to succeed under normal operation (no callers hold the view open after the
        // LifetimeLock write lock is acquired). Let any unexpected OS-level exception
        // propagate so the caller is aware of the failure.
        _MmapPool?.Dispose();
    }

    #endregion
}
