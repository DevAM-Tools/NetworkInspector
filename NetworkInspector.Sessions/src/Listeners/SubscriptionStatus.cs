// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>
/// Subscription state of a listener or value-cache subscription. A subscription starts <see cref="Active"/> and moves
/// at most once to one of the two final states; the session sets the final state before it cancels the listener, so
/// the listener's <c>OnUnsubscribed</c> callback already sees the reason.
/// </summary>
public enum SubscriptionStatus
{
    #region Values

    /// <summary>Listener is active and receiving notifications.</summary>
    Active,

    /// <summary>Listener was explicitly unsubscribed.</summary>
    Unsubscribed,

    /// <summary>Session ended while the listener was still active.</summary>
    SessionEnded,

    #endregion
}
