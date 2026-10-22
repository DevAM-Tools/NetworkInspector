// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Read-only memory mapping of a whole capture file with exactly one view. The same view serves the
/// sequential scan and every concurrent random-access reader.
/// </summary>
/// <remarks>
/// <para>
/// The view pointer (<see cref="_PrimaryPtr"/>) is acquired once in the constructor and held for the lifetime of
/// the mapping, then released in <see cref="Dispose"/>. <see cref="GetPrimarySpan"/> therefore costs one pointer
/// addition. The scanning thread brackets each step with <see cref="TryAcquireScanLease"/> /
/// <see cref="ReleaseScanLease"/> (one reference per step, not per span), so a concurrent <see cref="Dispose"/> — a
/// session shutdown with a timeout disposes sources whose read loop may still run — never unmaps under a running copy.
/// </para>
/// <para>
/// Random access does not take a lease: the owning source runs each random read inside a section of its
/// <see cref="ReaderGate"/>, and its <see cref="Dispose"/> closes the gate (waiting for running sections on other
/// threads) before it disposes this pool. A lease is a reference on the view's <see cref="SafeHandle"/>
/// (<see cref="SafeHandle.DangerousAddRef"/>): the add fails once the handle is closed, and an unmap requested while
/// leases are held is deferred until the last <see cref="SafeHandle.DangerousRelease"/>; the sequential thread keeps
/// using it because an error handler inside its call may dispose the source. The mapping is read-only, so concurrent
/// readers need no mutual exclusion. One view instead of a pool of views keeps the reserved address space at one file
/// size, independent of the core count.
/// </para>
/// <para><b>Thread-safety:</b> <see cref="TryAcquireScanLease"/> and <see cref="ReleaseScanLease"/> are safe from any
/// thread, also concurrently with <see cref="Dispose"/>. <see cref="GetPrimarySpan"/> and <see cref="PrefetchRange"/>
/// are for a thread that holds a lease (the scanning thread per step) or is inside the owner's reader-gate section
/// (a random-access reader). Leases are reference counts and do not exclude each other.</para>
/// </remarks>
internal sealed class MmapPool : IDisposable
{
    #region Fields

    /// <summary>The underlying memory-mapped file.</summary>
    private readonly MemoryMappedFile _MmapFile;

    /// <summary>The single view accessor over the whole file.</summary>
    private readonly MemoryMappedViewAccessor _Primary;

    /// <summary>Handle of <see cref="_Primary"/>; its reference count guards concurrent copies against unmap.</summary>
    private readonly SafeMemoryMappedViewHandle _ViewHandle;

    /// <summary>
    /// Base pointer into the view, already adjusted for <see cref="MemoryMappedViewAccessor.PointerOffset"/>.
    /// Acquired once in the constructor and held until <see cref="Dispose"/>.
    /// </summary>
    private unsafe readonly byte* _PrimaryPtr;

    /// <summary>Total file size in bytes.</summary>
    private readonly long _FileSize;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    #endregion

    #region Properties

    /// <summary>Gets the file size in bytes.</summary>
    internal long FileSize => _FileSize;

    #endregion

    #region Constructors

    /// <summary>Maps the whole file read-only with a single view.</summary>
    /// <param name="path">Path to the capture file.</param>
    internal unsafe MmapPool(string path)
    {
        FileStream fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _FileSize = fileStream.Length;

        _MmapFile = MemoryMappedFile.CreateFromFile(
            fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            leaveOpen: false);

        _Primary = _MmapFile.CreateViewAccessor(0, _FileSize, MemoryMappedFileAccess.Read);
        _ViewHandle = _Primary.SafeMemoryMappedViewHandle;

        // Acquire the pointer once and hold it for the mapping's lifetime (released in Dispose). The constructor's
        // reference keeps the view mapped; every scan lease (TryAcquireScanLease) adds its own reference on top of it.
        // A reader inside the owner's ReaderGate section needs no lease: Dispose waits for the gate first.
        byte* ptr = null;
        _ViewHandle.AcquirePointer(ref ptr);
        _PrimaryPtr = ptr + _Primary.PointerOffset;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Takes a reference on the view for the reads of one sequential step (<see cref="GetPrimarySpan"/> on the scanning
    /// thread), so a concurrent <see cref="Dispose"/> defers the unmap until <see cref="ReleaseScanLease"/>. Returns false
    /// once disposal started; the caller must then not touch the view. Every true result needs exactly one release.
    /// </summary>
    internal bool TryAcquireScanLease()
    {
        bool added = false;
        try
        {
            _ViewHandle.DangerousAddRef(ref added);
        }
        catch (ObjectDisposedException)
        {
            // The handle is already closed: nothing was added, nothing to release.
            return false;
        }

        // Dispose started but has not closed the handle yet: refuse, so no new step begins on a pool being torn down.
        if (added && _Disposed != 0)
        {
            _ViewHandle.DangerousRelease();
            return false;
        }

        return added;
    }

    /// <summary>Releases one lease of <see cref="TryAcquireScanLease"/>; the last release after a dispose unmaps the view.</summary>
    internal void ReleaseScanLease() => _ViewHandle.DangerousRelease();

    /// <summary>
    /// Returns a <see cref="ReadOnlySpan{Byte}"/> over the view at the given file offset.
    /// </summary>
    /// <remarks>
    /// The span is valid while this mapping is not disposed. The pointer was acquired once at construction and is
    /// released only in <see cref="Dispose"/>, so no per-call <c>AcquirePointer</c>/<c>ReleasePointer</c> is needed.
    /// Callers must not use the span across a concurrent <see cref="Dispose"/> (the sources serialise that).
    /// </remarks>
    /// <param name="offset">File offset of the first byte; a negative or past-end offset yields an empty span.</param>
    /// <param name="length">Number of bytes to expose; clipped at EOF.</param>
    /// <returns>A span over the requested file region.</returns>
    internal unsafe ReadOnlySpan<byte> GetPrimarySpan(long offset, int length)
    {
        // One unsigned compare rejects negative and past-end offsets alike, so no pointer arithmetic lands before the view.
        // The in-memory backend (DataBackend.GetScanSpan) returns an empty span for the same offsets.
        if ((ulong)offset >= (ulong)_FileSize)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        if (offset + length > _FileSize)
        {
            length = (int)(_FileSize - offset);
        }

        return new ReadOnlySpan<byte>(_PrimaryPtr + offset, length);
    }

    /// <summary>Requests the lines of a range of the view. Caller holds a lease or a gate section, and the range lies inside the file.</summary>
    internal unsafe void PrefetchRange(long offset, int length) => CachePrefetch.Range(_PrimaryPtr + offset, length);

    #endregion

    #region IDisposable

    /// <inheritdoc />
    /// <exception cref="AggregateException">Releasing the view or the mapping failed at the OS level.</exception>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // ReleasePointer drops the constructor's reference; the view is unmapped when the last outstanding scan lease
        // is released. Each step runs even when an earlier one fails, and one
        // aggregate exception reports all failures afterwards.
        List<Exception> failures = [];
        try
        {
            _ViewHandle.ReleasePointer();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            _Primary.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            _MmapFile.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Memory-map dispose failed.", failures);
        }
    }

    #endregion
}
