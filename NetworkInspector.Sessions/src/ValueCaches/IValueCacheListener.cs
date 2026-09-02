// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.ValueCaches;

/// <summary>
/// Receives notifications and pulls value-cache columns from the session.
/// All methods are called on the subscription's dedicated background thread.
///
/// <para>
/// <b>Pull-based model:</b>
/// Instead of receiving pushed row copies, the listener is notified via
/// <see cref="NotifyFlags"/> and reads columns from <see cref="ValueCacheReaderView"/>
/// at its own pace. Natural coalescing: if multiple packets arrive before the
/// listener polls, they are all consumed in one <see cref="OnNewRows"/> batch.
/// </para>
///
/// <para>
/// <b>Threading:</b>
    /// All methods are called on the dedicated value-cache slot thread,
    /// the same threading contract as <see cref="ISessionListener"/>.
/// Do not block for extended periods — it stalls notification processing.
/// </para>
///
/// <para>
/// <b>Implementing:</b>
/// Only <see cref="UiName"/> and <see cref="OnNewRows"/> are required.
/// All other methods have default no-op implementations.
/// Core <c>ValueCache</c> has no public growth callback; this interface is the Session push.
/// </para>
/// </summary>
public interface IValueCacheListener
{
    /// <summary>User-visible name for monitoring and diagnostics.</summary>
    string UiName
    {
        get;
    }

    // ── High-frequency (row delivery) ────────────────────────────────────────

    /// <summary>
    /// New packets have been processed for this subscription's packet-id window.
    /// <paramref name="fromIndex"/> (inclusive) to <paramref name="toIndexExclusive"/>
    /// (exclusive) are packet ids, the same coalescing window as
    /// <see cref="ISessionListener.OnNewPackets"/>. They are not series row indexes:
    /// a packet without the configured field, or one
    /// <c>TryGetPacket</c> cannot load, adds no rows. Index columns with
    /// <see cref="ValueCacheSeries.Count"/>, not with these packet ids.
    /// Pull columns from <paramref name="cache"/> or packets from <paramref name="session"/>.
    /// </summary>
    /// <param name="session">Read-only session view.</param>
    /// <param name="cache">Current read-only view of this subscription's cache.</param>
    /// <param name="fromIndex">First new packet id (inclusive).</param>
    /// <param name="toIndexExclusive">One past the last new packet id.</param>
    void OnNewRows(ISessionReader session, ValueCacheReaderView cache, int fromIndex, int toIndexExclusive);

    // ── Source lifecycle ──────────────────────────────────────────────────────

    /// <summary>Sources changed (added or completed). Query via <see cref="ISessionReader.GetFrameSources"/>.</summary>
    void OnSourcesChanged(ISessionReader session)
    {
    }

    /// <summary>All sources finished. No further packets expected.</summary>
    void OnAllSourcesCompleted(ISessionReader session)
    {
    }

    // ── Job lifecycle ────────────────────────────────────────────────────────

    /// <summary>Jobs changed (added or status changed). Query via <see cref="ISessionReader.GetJobs"/>.</summary>
    void OnJobsChanged(ISessionReader session)
    {
    }

    // ── Session lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// The protocol stack was replaced and all packets have been re-parsed.
    /// The listener should discard any cached protocol/field state and treat
    /// subsequent <see cref="OnNewRows"/> calls as a fresh data set starting
    /// from index 0. The previous writer is abandoned; <paramref name="session"/>
    /// exposes the rebound cache.
    /// </summary>
    /// <param name="session">Read-only session view for pulling the re-parsed packets.</param>
    void OnStackChanged(ISessionReader session)
    {
    }

    /// <summary>Session phase changed. Check <paramref name="phase"/> for the new state.</summary>
    void OnPhaseChanged(SessionPhase phase)
    {
    }

    /// <summary>Session is shutting down. Last callback before <see cref="OnUnsubscribed"/>.</summary>
    void OnShuttingDown()
    {
    }

    /// <summary>Listener was unsubscribed. No further callbacks will follow.</summary>
    void OnUnsubscribed()
    {
    }
}
