// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Pool of memory-mapped file view accessors for concurrent random access.
/// Uses a primary mmap reference for lock-free sequential scanning and a pool
/// of mutex-protected slots for concurrent random access reads.
/// </summary>
/// <remarks>
/// <para>
/// The primary pointer (<see cref="_PrimaryPtr"/>) is acquired once in the constructor
/// and held for the entire lifetime of the pool, then released in <see cref="Dispose"/>.
/// This lifetime pin is intentional: it lets <see cref="GetPrimarySpan"/> return a
/// <see cref="ReadOnlySpan{Byte}"/> that remains valid as long as the caller holds a
/// live reference to this <see cref="MmapPool"/>, without any per-call
/// <c>AcquirePointer</c>/<c>ReleasePointer</c> overhead or the
/// use-after-release hazard that arises when releasing inside a <c>finally</c>
/// before the span is consumed by the caller.
/// </para>
/// <para>
/// Slot distribution: <c>frameId % poolSize</c> distributes concurrent access
/// across multiple mmap handles to reduce lock contention.
/// </para>
/// </remarks>
internal sealed class MmapPool : IDisposable
{
    #region Fields

    /// <summary>The underlying memory-mapped file.</summary>
    private readonly MemoryMappedFile _MmapFile;

    /// <summary>Primary view accessor for lock-free sequential scanning.</summary>
    private readonly MemoryMappedViewAccessor _Primary;

    /// <summary>
    /// Base pointer into the primary view, already adjusted for <see cref="MemoryMappedViewAccessor.PointerOffset"/>.
    /// Acquired once in the constructor and held until <see cref="Dispose"/>.
    /// </summary>
    private unsafe readonly byte* _PrimaryPtr;

    /// <summary>Pool of view accessors for random access, each protected by a lock.</summary>
    private readonly (MemoryMappedViewAccessor Accessor, Lock Lock)[] _Slots;

    /// <summary>Total file size in bytes.</summary>
    private readonly long _FileSize;

    /// <summary>Atomic dispose latch (0 = live, 1 = disposed).</summary>
    private volatile int _Disposed;

    /// <summary>
    /// Number of exceptions swallowed during <see cref="Dispose"/>.
    /// Each native cleanup step is individually guarded to ensure all slots are
    /// released even when one step throws. Failures are counted here so callers
    /// can detect that resource cleanup was not fully clean.
    /// </summary>
    private volatile int _DisposeErrors;

    #endregion

    #region Properties

    /// <summary>Gets the file size in bytes.</summary>
    internal long FileSize => _FileSize;

    /// <summary>
    /// Number of exceptions swallowed during disposal. Non-zero indicates that one
    /// or more native memory-map handles could not be cleanly released.
    /// </summary>
    internal int DisposeErrors => _DisposeErrors;

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a new MmapPool for the given file.
    /// </summary>
    /// <param name="path">Path to the capture file.</param>
    /// <param name="poolSize">Number of random-access slots (default: CPU core count, clamped [1, 256]).</param>
    internal unsafe MmapPool(string path, int poolSize = 0)
    {
        if (poolSize <= 0)
        {
            poolSize = Math.Clamp(Environment.ProcessorCount, 1, 256);
        }

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

        // Acquire the pointer once and hold it for the pool's lifetime.
        // Released in Dispose(). This makes GetPrimarySpan a single pointer
        // addition with no refcount manipulation.
        byte* ptr = null;
        _Primary.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _PrimaryPtr = ptr + _Primary.PointerOffset;

        _Slots = new (MemoryMappedViewAccessor, Lock)[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            _Slots[i] = (_MmapFile.CreateViewAccessor(0, _FileSize, MemoryMappedFileAccess.Read), new Lock());
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Reads frame data using a pooled random-access slot.
    /// Thread-safe: slot = frameId mod poolSize.
    /// </summary>
    /// <param name="frameId">Frame ID used to select the pool slot.</param>
    /// <param name="offset">File offset to read from.</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <returns>Byte array containing the frame data.</returns>
    internal byte[] ReadAt(int frameId, long offset, int length)
    {
        if (length == 0)
        {
            return [];
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
        ReadInto(frameId, offset, buffer);
        return buffer;
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes from the file into
    /// <paramref name="destination"/>. Unread tail bytes are cleared so behaviour matches
    /// <c>new byte[length]</c> (zeros past EOF / partial read).
    /// </summary>
    internal void ReadInto(int frameId, long offset, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        int slotIndex = (int)((uint)frameId % (uint)_Slots.Length);

        lock (_Slots[slotIndex].Lock)
        {
            int readable = (int)Math.Min(destination.Length, Math.Max(0, _FileSize - offset));
            if (readable > 0)
            {
                _Slots[slotIndex].Accessor.ReadArray(offset, destination[..readable]);
            }

            if (readable < destination.Length)
            {
                destination[readable..].Clear();
            }
        }
    }

    /// <summary>
    /// Returns a <see cref="ReadOnlySpan{Byte}"/> over the primary memory-mapped view
    /// at the given file offset.
    /// </summary>
    /// <remarks>
    /// The span is valid for as long as this <see cref="MmapPool"/> instance is alive
    /// (i.e. not disposed). The underlying pointer was acquired once at construction
    /// time and is never released until <see cref="Dispose"/> — no per-call
    /// <c>AcquirePointer</c>/<c>ReleasePointer</c> is needed.
    /// </remarks>
    /// <param name="offset">File offset of the first byte.</param>
    /// <param name="length">Number of bytes to expose.</param>
    /// <returns>A span over the requested file region.</returns>
    internal unsafe ReadOnlySpan<byte> GetPrimarySpan(long offset, int length)
    {
        if (offset + length > _FileSize)
        {
            length = (int)Math.Max(0, _FileSize - offset);
        }

        return new ReadOnlySpan<byte>(_PrimaryPtr + offset, length);
    }

    #endregion

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        // Release the lifetime-pinned primary pointer before disposing the accessor.
        // Each step is wrapped independently so that a failure in one step does not
        // prevent the remaining native resources from being released.
        // Failures are counted in _DisposeErrors so callers can detect incomplete cleanup.
        try
        {
            _Primary.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        catch (Exception) { Interlocked.Increment(ref _DisposeErrors); }
        try
        {
            _Primary.Dispose();
        }
        catch (Exception) { Interlocked.Increment(ref _DisposeErrors); }
        for (int i = 0; i < _Slots.Length; i++)
        {
            try
            {
                _Slots[i].Accessor.Dispose();
            }
            catch (Exception) { Interlocked.Increment(ref _DisposeErrors); }
        }
        try
        {
            _MmapFile.Dispose();
        }
        catch (Exception) { Interlocked.Increment(ref _DisposeErrors); }
    }

    #endregion
}

/// <summary>
/// Extension to allow reading into a Span from a MemoryMappedViewAccessor.
/// </summary>
internal static class MemoryMappedViewAccessorExtensions
{
    #region Extension Methods

    /// <summary>
    /// Reads bytes from a MemoryMappedViewAccessor into a Span.
    /// </summary>
    internal static unsafe void ReadArray(this MemoryMappedViewAccessor accessor, long position, Span<byte> buffer)
    {
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        try
        {
            ReadOnlySpan<byte> source = new(ptr + accessor.PointerOffset + position, buffer.Length);
            source.CopyTo(buffer);
        }
        finally
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    #endregion
}
