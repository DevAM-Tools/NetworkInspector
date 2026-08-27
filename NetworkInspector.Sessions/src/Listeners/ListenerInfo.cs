// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>
/// Public read-only view of a listener subscription.
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
/// Status transitions are performed by the session under controlled conditions.
/// </para>
/// </summary>
public sealed class ListenerInfo
{
    // Cross-thread status. Plain volatile read/write; the session is the only writer.
    private volatile int _Status;

    // Callback set by the session to implement the Unsubscribe convenience API.
    // Invoked at most once; null-guarded for safety.
    internal Action? UnsubscribeCallback
    {
        get; set;
    }

    /// <summary>Unique identifier for this subscription.</summary>
    public ListenerId Id
    {
        get; internal init;
    }

    /// <summary>User-visible listener name as reported by <see cref="ISessionListener.UiName"/>.</summary>
    public string UiName { get; internal init; } = "";

    /// <summary>Current subscription status (thread-safe read).</summary>
    public SubscriptionStatus Status => (SubscriptionStatus)_Status;

    /// <summary>
    /// Convenience API: requests that the session unsubscribe this listener.
    /// Equivalent to calling <see cref="ISession.TryUnsubscribe"/> with this listener's job.
    ///
    /// <para>
    /// The actual unsubscription is performed by the session. The listener's
    /// <see cref="ISessionListener.OnUnsubscribed"/> callback is guaranteed to be called
    /// before the listener thread exits.
    /// </para>
    /// </summary>
    public void Unsubscribe() => UnsubscribeCallback?.Invoke();

    /// <summary>
    /// Transitions the subscription status. Called by the session during
    /// unsubscribe or shutdown. Plain volatile write.
    /// </summary>
    internal void SetStatus(SubscriptionStatus status)
        => _Status = (int)status;
}
