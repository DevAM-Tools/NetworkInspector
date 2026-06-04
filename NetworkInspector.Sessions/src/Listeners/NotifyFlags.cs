// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Listeners;

/// <summary>
/// Atomic notification flags exchanged between producer threads (source jobs,
/// session coordinator) and consumer threads (listener slots).
///
/// <para>
/// <b>Protocol:</b>
/// Producers set flags via <c>Interlocked.Or(ref slot.Flags, (int)flag)</c>.
/// Consumers read and reset via <c>Interlocked.Exchange(ref _Flags, 0)</c>.
/// Multiple OR writes between two Exchange reads coalesce naturally — the
/// consumer sees the union of all flags set since its last read.
/// </para>
///
/// <para>
/// <b>Memory:</b> One <see cref="int"/> per listener slot (4 bytes).
/// No heap allocation. No queue growth. No per-event object.
/// </para>
/// <para>
/// <b>Bit allocation:</b>
/// <list type="table">
///   <item><term>Bit 0</term><description><see cref="NewPackets"/></description></item>
///   <item><term>Bit 1</term><description><see cref="SourceAdded"/></description></item>
///   <item><term>Bit 2</term><description><see cref="SourceCompleted"/></description></item>
///   <item><term>Bit 3</term><description><see cref="AllSourcesCompleted"/></description></item>
///   <item><term>Bit 4</term><description><see cref="JobAdded"/></description></item>
///   <item><term>Bit 5</term><description><see cref="JobStatusChanged"/></description></item>
///   <item><term>Bit 6</term><description><see cref="PhaseChanged"/></description></item>
///   <item><term>Bit 7</term><description><see cref="ShuttingDown"/></description></item>
///   <item><term>Bit 8</term><description><see cref="JobRemoved"/></description></item>
///   <item><term>Bit 9</term><description><see cref="StackChanged"/></description></item>
///   <item><term>Bits 10–31</term><description>Reserved for future use</description></item>
/// </list>
/// </para>
/// </summary>
[Flags]
public enum NotifyFlags : int
{
    /// <summary>No notifications pending.</summary>
    None = 0,

    // ── High-frequency (source threads) ──────────────────────────────────────

    /// <summary>New packets are available in the <c>PacketStore</c>.</summary>
    NewPackets = 1 << 0,

    // ── Source lifecycle (source threads) ─────────────────────────────────────

    /// <summary>A new source was registered.</summary>
    SourceAdded = 1 << 1,

    /// <summary>At least one source has finished.</summary>
    SourceCompleted = 1 << 2,

    /// <summary>All sources have finished. No further packets expected.</summary>
    AllSourcesCompleted = 1 << 3,

    // ── Job lifecycle (job threads) ──────────────────────────────────────────

    /// <summary>A new job was registered.</summary>
    JobAdded = 1 << 4,

    /// <summary>At least one job changed its status.</summary>
    JobStatusChanged = 1 << 5,

    // ── Session lifecycle (coordinator) ──────────────────────────────────────

    /// <summary>The session phase changed.</summary>
    PhaseChanged = 1 << 6,

    /// <summary>The session is shutting down. Last flag before <c>OnUnsubscribed</c>.</summary>
    ShuttingDown = 1 << 7,

    // ── Job lifecycle continued ──────────────────────────────────────────────

    /// <summary>A completed, cancelled, or failed job was removed from the job list.</summary>
    JobRemoved = 1 << 8,

    // ── Stack swap (coordinator) ─────────────────────────────────────────────

    /// <summary>
    /// The protocol stack was replaced (e.g. after a settings/profile change).
    /// All packets have been re-parsed with the new stack. Listeners should
    /// reset any cached protocol/field state and re-read all packets from the
    /// beginning.
    /// </summary>
    StackChanged = 1 << 9,
}
