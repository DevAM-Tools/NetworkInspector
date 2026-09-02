// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.ValueCaches;

/// <summary>
/// Public read-only view of a session value-cache subscription.
///
/// <para>
/// <b>Status lifecycle:</b>
/// <list type="bullet">
///   <item><see cref="SubscriptionStatus.Active"/> — listener is receiving notifications.</item>
///   <item><see cref="SubscriptionStatus.Unsubscribed"/> — listener was explicitly unsubscribed
///         via <see cref="Unsubscribe"/> or <see cref="ISession.TryUnsubscribe"/>.</item>
///   <item><see cref="SubscriptionStatus.SessionEnded"/> — session shut down while the listener
///         was still active.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Thread safety:</b>
/// <see cref="Status"/> is a plain volatile read of <see cref="_Status"/>, safe from any thread.
/// <see cref="Cache"/> returns the slot's current writer view so Restart rebind is visible.
/// Status transitions are performed by the session under controlled conditions.
/// </para>
/// </summary>
public sealed class ValueCacheInfo
{
    // Cross-thread status. Plain volatile write; the session is the only writer.
    private volatile int _Status;

    // Current writer. Swapped on Restart rebind; readers load via Volatile.
    private ValueCache? _Writer;

    /// <summary>
    /// Callback set by the session to implement the Unsubscribe convenience API.
    /// Invoked at most once; null-guarded. Null when this info is ingest-only (no listener job).
    /// </summary>
    internal Action? UnsubscribeCallback
    {
        get; set;
    }

    /// <summary>Unique identifier for this value-cache subscription.</summary>
    public ValueCacheId Id
    {
        get; internal init;
    }

    /// <summary>User-visible name as reported by the listener, or <c>ingest</c> for a construction-time cache without a listener.</summary>
    public string UiName { get; internal init; } = "";

    /// <summary>
    /// Job for this subscription when a listener slot exists.
    /// <see langword="null"/> for an ingest cache registered without <see cref="IValueCacheListener"/>.
    /// </summary>
    public JobInfo? Job
    {
        get; internal set;
    }

    /// <summary>Current subscription status (thread-safe read).</summary>
    public SubscriptionStatus Status => (SubscriptionStatus)_Status;

    /// <summary>
    /// Zero-allocation read-only view of the current writer.
    /// After Restart this aliases the rebound cache, not the abandoned instance.
    /// </summary>
    public ValueCacheReaderView Cache
    {
        get
        {
            ValueCache? writer = Volatile.Read(ref _Writer);
            if (writer is null)
            {
                throw new InvalidOperationException("ValueCacheInfo has no writer.");
            }

            return writer.AsReadOnlyView();
        }
    }

    /// <summary>
    /// Convenience API: requests that the session unsubscribe this value-cache listener.
    /// No-op when this info is ingest-only (no listener job). Ingest NotifyOnly unsubscribes
    /// the listener job and does not destroy the ingest cache.
    /// </summary>
    public void Unsubscribe() => UnsubscribeCallback?.Invoke();

    /// <summary>
    /// Transitions the subscription status. Called by the session during
    /// unsubscribe or shutdown. Plain volatile write.
    /// </summary>
    internal void SetStatus(SubscriptionStatus status)
        => _Status = (int)status;

    /// <summary>Installs or replaces the writer this info exposes. Session-only.</summary>
    internal void SetWriter(ValueCache writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Volatile.Write(ref _Writer, writer);
    }
}
