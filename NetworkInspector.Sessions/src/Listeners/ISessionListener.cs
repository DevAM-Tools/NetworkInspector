// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>
/// Receives notifications and pulls data actively from the session.
/// All methods are called on the listener's dedicated background thread.
///
/// <para>
/// <b>Pull-based model:</b>
/// Instead of receiving pushed data copies, the listener is notified via
/// <see cref="NotifyFlags"/> and reads data from the <see cref="ISessionReader"/>
/// at its own pace. Natural coalescing: if multiple packets arrive before the
/// listener polls, they are all consumed in one batch.
/// </para>
///
/// <para>
/// <b>Threading:</b>
/// All methods are called on the dedicated <see cref="ListenerSlot"/> thread.
/// Do not block for extended periods — it stalls notification processing.
/// </para>
///
/// <para>
/// <b>Implementing:</b>
/// Only <see cref="UiName"/> and <see cref="OnNewPackets"/> are required.
/// All other methods have default no-op implementations.
/// </para>
/// </summary>
public interface ISessionListener
{
    /// <summary>User-visible name for monitoring and diagnostics.</summary>
    string UiName
    {
        get;
    }

    // ── High-frequency (packet delivery) ─────────────────────────────────────

    /// <summary>
    /// New packets are available in the store. The listener reads from
    /// <paramref name="fromIndex"/> (inclusive) to <paramref name="toIndexExclusive"/>
    /// (exclusive) via <paramref name="session"/>.
    /// </summary>
    /// <param name="session">Read-only session view for pulling packets.</param>
    /// <param name="fromIndex">First new packet index (inclusive).</param>
    /// <param name="toIndexExclusive">One past the last new packet index.</param>
    void OnNewPackets(ISessionReader session, int fromIndex, int toIndexExclusive);

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
    /// subsequent <see cref="OnNewPackets"/> calls as a fresh data set starting
    /// from index 0.
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
