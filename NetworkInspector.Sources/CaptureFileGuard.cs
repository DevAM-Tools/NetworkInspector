// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources;

/// <summary>
/// Keeps a capture file open for reading with <see cref="FileShare.Read"/> for the lifetime of a source. On Windows the
/// share mode is enforced by the operating system: no other handle can open the file for writing, and opening fails
/// while a writer holds it. On Linux and macOS .NET takes an advisory <c>flock</c> (respected only by cooperating
/// writers), so a background monitor compares the file length and last write time with the values at open and sets an
/// atomic change flag.
/// </summary>
/// <remarks>
/// <para>
/// The compact frame indexes store only where each accepted frame starts and re-read the rest from the file. An
/// unchanged file is therefore a correctness precondition of <c>FrameById</c>, not only a data-quality concern; the
/// guard turns a change into a detected failure instead of a wrong frame.
/// </para>
/// <para>
/// <b>Monitor (Linux/macOS only):</b> a <see cref="Timer"/> runs <see cref="IsUnchanged"/> every
/// <see cref="MonitorInterval"/> on a thread-pool thread, so <c>FrameById</c> only reads <see cref="ChangeDetected"/>
/// and never makes a system call. The timer holds the guard through a weak reference: a source that is never disposed
/// stays collectable, and collecting it stops the timer. The monitor stops after the first detected change, on
/// <see cref="StopMonitoring"/> (a preloaded source that copied the file), and on <see cref="Dispose"/>. Windows needs
/// no monitor because writers are denied.
/// </para>
/// <para>
/// <b>Thread-safety:</b> immutable after construction except the change flag (a <c>volatile</c> bool written only from
/// <c>false</c> to <c>true</c>), the change signal (completed once), the dispose latch (interlocked), and the two
/// one-way switches of the monitor: <c>_ChecksChanges</c> (cleared once by <see cref="StopMonitoring"/>) and
/// <c>_Monitor</c> (set once in the constructor, cleared once by <see cref="StopMonitoring"/>).
/// <see cref="IsUnchanged"/> runs on the monitor thread and on source threads at the same time. Residual risk on
/// Linux/macOS: a non-cooperating writer that truncates a memory-mapped file can terminate the process (SIGBUS) before
/// any check runs; documented in the Sources README.
/// </para>
/// </remarks>
internal sealed class CaptureFileGuard : IDisposable
{
    #region Constants

    /// <summary>
    /// Period of the background change check on Linux and macOS: the longest time a change made while only
    /// <c>FrameById</c> runs stays undetected. Two file-status calls per period and open source.
    /// </summary>
    internal static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Sequential reads ask the file for a change once per this many frames (and at the end). A check costs two
    /// file-status calls on Linux and macOS; this interval spreads that cost over thousands of frames, and between
    /// checks only the guard's latch is read. Constant (tuning value, not configurable). Must be a power of two:
    /// <see cref="HasChangedAt"/> tests <c>frameIndex &amp; (interval − 1)</c>.
    /// </summary>
    internal const int SequentialCheckInterval = 4096;

    #endregion

    #region Fields

    /// <summary>
    /// Windows enforces the share mode; only the other platforms need the length and time comparison. Cleared by
    /// <see cref="StopMonitoring"/> once a source holds its own copy of the bytes, so the comparison stops for the rest
    /// of the lifetime.
    /// </summary>
    private volatile bool _ChecksChanges = !OperatingSystem.IsWindows();

    private readonly DateTime _LastWriteUtc;
    private readonly TaskCompletionSource? _ChangeSignal;

    /// <summary>The background check; null where none runs (Windows, or after <see cref="StopMonitoring"/>).</summary>
    private volatile Timer? _Monitor;
    private volatile bool _Changed;
    private volatile int _Disposed;

    #endregion

    #region Constructors

    private CaptureFileGuard(SafeFileHandle handle, TimeSpan monitorInterval)
    {
        Handle = handle;
        Length = RandomAccess.GetLength(handle);
        _LastWriteUtc = File.GetLastWriteTimeUtc(handle);
        if (_ChecksChanges)
        {
            _ChangeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _Monitor = _StartMonitor(this, monitorInterval);
        }
    }

    #endregion

    #region Properties

    /// <summary>The guarded read handle.</summary>
    internal SafeFileHandle Handle { get; }

    /// <summary>File length at open in bytes.</summary>
    internal long Length { get; }

    /// <summary>
    /// True once a check detected a change (one volatile read, no system call). The random-access paths read only this
    /// flag; the background monitor, the check at open, the sequential checks every 4 096 frames, and the check at the
    /// end of a sequential read set it.
    /// </summary>
    internal bool ChangeDetected => _Changed;

    /// <summary>
    /// True when the guard started a background monitor (Linux and macOS) and <see cref="StopMonitoring"/> has not
    /// cleared it. The monitor itself stops after a change or on <see cref="Dispose"/>, which leave this flag set.
    /// </summary>
    internal bool IsMonitored => _Monitor is not null;

    /// <summary>
    /// Completes when a change is detected; <see langword="null"/> where no monitor runs (Windows). Lets callers and
    /// tests await detection instead of polling.
    /// </summary>
    internal Task? ChangeSignal => _ChangeSignal?.Task;

    #endregion

    #region Public API

    /// <summary>Opens <paramref name="path"/> for reading, denying writers, with the default <see cref="MonitorInterval"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file is open for writing elsewhere (Windows) or cannot be read.</exception>
    internal static CaptureFileGuard Open(string path) => Open(path, MonitorInterval);

    /// <summary>Opens <paramref name="path"/> for reading, denying writers, with a background check every <paramref name="monitorInterval"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="monitorInterval"/> is not positive.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="IOException">The file is open for writing elsewhere (Windows) or cannot be read.</exception>
    internal static CaptureFileGuard Open(string path, TimeSpan monitorInterval)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(monitorInterval, TimeSpan.Zero);
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            return new CaptureFileGuard(handle, monitorInterval);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True while the file is unchanged. Windows, and every call after <see cref="StopMonitoring"/>: always true without a
    /// system call (writers are denied on Windows; a copied file is no longer read). Linux/macOS: two file-status calls
    /// (length, last write time) per check; a change sets the flag for the rest of the lifetime and completes
    /// <see cref="ChangeSignal"/>.
    /// </summary>
    internal bool IsUnchanged()
    {
        // The switch comes first: a stopped guard is true for the rest of its lifetime, also after Dispose, as on Windows.
        if (!_ChecksChanges)
        {
            return true;
        }

        if (_Changed)
        {
            return false;
        }

        // A disposed guard cannot be checked; report "changed" so callers fail closed instead of throwing.
        if (_Disposed != 0)
        {
            return false;
        }

        try
        {
            if (RandomAccess.GetLength(Handle) == Length && File.GetLastWriteTimeUtc(Handle) == _LastWriteUtc)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // A handle that can no longer be queried cannot prove the file is unchanged.
        }

        _MarkChanged();
        return false;
    }

    /// <summary>
    /// The change check of a sequential read at frame <paramref name="frameIndex"/>: every
    /// <see cref="SequentialCheckInterval"/>-th frame (and frame 0, which callers also use at the end of a read) asks the
    /// file through <see cref="IsUnchanged"/>; for the other frames it only reads the latch.
    /// </summary>
    /// <returns>True when the file changed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasChangedAt(int frameIndex) =>
        (frameIndex & (SequentialCheckInterval - 1)) == 0
            ? !IsUnchanged()
            : ChangeDetected;

    /// <summary>Reads the whole file through the guarded handle (preload backends).</summary>
    /// <remarks>Caller bounds the size (<see cref="PreloadDecision.ShouldPreload"/> limits it to an array).</remarks>
    /// <exception cref="IOException">The file is shorter than at open.</exception>
    internal byte[] ReadAll()
    {
        byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)Length));
        int total = 0;
        while (total < data.Length)
        {
            int read = RandomAccess.Read(Handle, data.AsSpan(total), total);
            if (read == 0)
            {
                throw new IOException("Capture file ended before its length at open.");
            }

            total += read;
        }

        return data;
    }

    /// <summary>
    /// Ends change detection for the rest of the lifetime: the background check stops and <see cref="IsUnchanged"/>
    /// reports true without a system call. Call it only after the caller holds its own copy of the whole file and has
    /// verified with <see cref="IsUnchanged"/> that the copy is consistent, so that later changes of the file cannot
    /// reach any read of that copy. The handle and the share lock stay until <see cref="Dispose"/>. Idempotent; any
    /// thread after the open-time check.
    /// </summary>
    /// <remarks>
    /// Preloaded (in-memory) sources only. A source that still reads the file keeps detection: its frame re-reads depend
    /// on the file, and a change must fail them closed.
    /// </remarks>
    internal void StopMonitoring()
    {
        // The switch goes first: a monitor tick that runs meanwhile sees it and does nothing more.
        _ChecksChanges = false;
        Timer? monitor = _Monitor;
        _Monitor = null;
        monitor?.Dispose();
    }

    /// <inheritdoc/>
    /// <remarks>Stops the monitor first, then closes the handle; a tick that still runs sees the dispose latch or a closed handle and stops.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _Monitor?.Dispose();
        Handle.Dispose();
    }

    #endregion

    #region Private helpers

    /// <summary>Sets the change flag (release), then completes the signal; the flag is what readers rely on.</summary>
    private void _MarkChanged()
    {
        _Changed = true;
        _ = _ChangeSignal?.TrySetResult();
    }

    /// <summary>Starts the periodic check. The timer state is a weak reference, so the timer never roots the guard.</summary>
    private static Timer _StartMonitor(CaptureFileGuard guard, TimeSpan interval)
    {
        WeakReference<CaptureFileGuard> target = new(guard);

        // No ambient ExecutionContext (async locals of the opening caller) is captured by the long-lived timer.
        bool suppress = !ExecutionContext.IsFlowSuppressed();
        AsyncFlowControl flow = suppress ? ExecutionContext.SuppressFlow() : default;
        try
        {
            return new Timer(static state => _OnMonitorTick((WeakReference<CaptureFileGuard>)state!), target, interval, interval);
        }
        finally
        {
            if (suppress)
            {
                flow.Undo();
            }
        }
    }

    /// <summary>One background check; stops the timer once the result can no longer change (changed or disposed).</summary>
    private static void _OnMonitorTick(WeakReference<CaptureFileGuard> target)
    {
        // _Monitor may still be null for a tick that fires before the constructor stored the timer (only with a tiny
        // interval, as tests pass). The change is latched anyway, so the next tick finds the field set and stops the timer.
        if (target.TryGetTarget(out CaptureFileGuard? guard) && !guard.IsUnchanged())
        {
            guard._Monitor?.Dispose();
        }
    }

    #endregion
}
