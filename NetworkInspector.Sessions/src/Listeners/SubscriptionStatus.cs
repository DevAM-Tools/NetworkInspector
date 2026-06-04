// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>Subscription state for a signal listener.</summary>
public enum SubscriptionStatus
{
    /// <summary>Listener is active and receiving signals.</summary>
    Active,

    /// <summary>Listener was explicitly unsubscribed.</summary>
    Unsubscribed,

    /// <summary>Session ended while the listener was still active.</summary>
    SessionEnded,
}
