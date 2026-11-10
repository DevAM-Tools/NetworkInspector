// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered frame source (e.g., a pcap file, live capture device).
///
/// <para>
/// <b>Stop lifecycle:</b>
/// A running source can be stopped via <see cref="Stop"/> without disposing it.
/// The source thread exits after the current frame, but the source remains available
/// for random access (<see cref="IRandomAccessFrameSource.FrameById"/>) and reparse.
/// Final disposal happens during session shutdown.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="Id"/> and <see cref="Source"/> are immutable. The stop callback is the only
/// mutable state; it is registered, cleared and read through atomic operations on one reference, so
/// <see cref="RegisterStopCallback"/>, <see cref="ClearStopCallback"/>, <see cref="IsStoppable"/> and
/// <see cref="Stop"/> may be called from any thread. <see cref="Stop"/> takes no lock: concurrent calls may each
/// invoke the registered callback, so the callback must tolerate that.
/// </para>
/// </summary>
/// <param name="id">The unique frame source identifier.</param>
/// <param name="source">The registered source, or <see langword="null"/> for synthetic or test scenarios.</param>
public sealed class FrameSourceInfo(FrameSourceId id, IFrameSource? source)
{
    #region Source identity

    /// <summary>Unique frame source identifier.</summary>
    public FrameSourceId Id { get; } = id;

    /// <summary>
    /// The frame source instance that was registered.
    /// May be <c>null</c> for synthetic or test scenarios where no real source exists.
    /// </summary>
    public IFrameSource? Source { get; } = source;

    /// <summary>
    /// Human-readable display name.
    /// Delegated from <see cref="IFrameSource.UiName"/> if available, otherwise the string
    /// representation of the <see cref="Id"/>.
    /// </summary>
    public string UiName
    {
        get
        {
            if (Source is not null && Source.UiName is not null)
            {
                return Source.UiName;
            }
            return Id.ToString();
        }
    }

    /// <summary>Optional description text (delegated from <see cref="IFrameSource.Description"/>).</summary>
    public string? Description => Source?.Description;

    #endregion

    #region Stop callback

    /// <summary>
    /// Callback set by the session to implement the <see cref="Stop"/> convenience API; <see langword="null"/> while the source
    /// is not stoppable. Cleared only when the callback returns true, so a refused stop can be retried.
    /// A single reference is the whole state, so volatile publication plus Interlocked compare-exchange is enough and no
    /// lock is needed, and the callback (which calls back into the session) is invoked without holding any lock of this
    /// class.
    /// </summary>
    private volatile Func<bool>? _StopCallback;

    /// <summary>
    /// Registers a callback that will be invoked when <see cref="Stop"/> is called.
    /// Typically wired by the session to unsubscribe the source's job.
    /// </summary>
    /// <param name="callback">
    /// The stop callback to register. It returns <see langword="true"/> when the stop was accepted (the callback is then
    /// dropped) and <see langword="false"/> when it was refused (the callback stays registered for a retry).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A stop callback is already registered.</exception>
    public void RegisterStopCallback(Func<bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Atomic "register if none": the compare-exchange installs the callback only while the slot is empty, so two
        // racing registrations cannot both succeed and silently replace each other.
        if (Interlocked.CompareExchange(ref _StopCallback, callback, null) is not null)
        {
            throw new InvalidOperationException("A stop callback is already registered.");
        }
    }

    /// <summary>
    /// Drops the stop callback without invoking it.
    /// Used by the session when a source is removed or unsubscribed so
    /// <see cref="IsStoppable"/> becomes false without calling <see cref="Stop"/>.
    /// </summary>
    public void ClearStopCallback() => Interlocked.Exchange(ref _StopCallback, null);

    /// <summary>
    /// Whether this source can be stopped via <see cref="Stop"/>.
    /// Returns <see langword="true"/> when the source is registered with a session
    /// that supports stopping individual sources.
    /// </summary>
    public bool IsStoppable => _StopCallback is not null;

    /// <summary>
    /// Convenience API: requests that the session stop this source's frame-reading loop.
    /// The source thread exits after the current frame. The source remains available
    /// for random access and reparse; final disposal happens during session shutdown.
    ///
    /// <para>
    /// Equivalent to calling <c>ISession.TryUnsubscribe</c> with this source's job.
    /// The callback is cleared only when it returns <see langword="true"/>.
    /// A false return leaves <see cref="IsStoppable"/> true so the caller can retry.
    /// </para>
    /// </summary>
    public void Stop()
    {
        // Read the callback once into a local so the invocation and the later clear refer to the same delegate even if
        // another thread clears or re-registers in between. A source that is not stoppable (or already stopped) is a no-op.
        Func<bool>? callback = _StopCallback;
        if (callback is null)
        {
            return;
        }

        // A refused stop (false) keeps the callback registered so the caller can retry.
        if (!callback())
        {
            return;
        }

        // Clear only if the slot still holds the delegate that just succeeded: a callback registered meanwhile by
        // someone else must not be dropped.
        Interlocked.CompareExchange(ref _StopCallback, null, callback);
    }

    #endregion
}
