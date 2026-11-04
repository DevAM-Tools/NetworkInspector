// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// The per-packet step of a session export: reads the filter verdict of the packet that was just parsed and commits matching
/// packets to the exporter.
/// <para>
/// <b>Why the verdict is read here and not before the parse:</b> an observer filter judges the packet while it is parsed (it is
/// part of the plan, placed before the exporter), so the verdict exists only after the parse. It is read before the next
/// <c>BeginPacket</c> clears it. A rejected packet is simply not committed: the exporter's staged fields are dropped by its next
/// <c>BeginPacket</c> (the commit rule of the field stage), so a rejected packet never reaches an output.
/// </para>
/// <para><b>Thread-safety:</b> called sequentially by the one job thread; <see cref="Stopped"/> is read by the same thread.</para>
/// </summary>
internal sealed class SessionExportVisitor : IPacketVisitor
{
    #region Fields

    private readonly SessionExportJob _Handle;
    private readonly IPacketListener _Exporter;
    private readonly FilterObserver? _ObserverFilter;
    private readonly PacketFilter? _TreeFilter;

    #endregion

    #region Constructors

    /// <param name="handle">Receives the counters and a failure.</param>
    /// <param name="exporter">Receives the matching packets.</param>
    /// <param name="observerFilter">The filter in tree-free form, or <see langword="null"/>.</param>
    /// <param name="treeFilter">The filter in field-tree form (used when the observer form cannot express it), or <see langword="null"/>.</param>
    internal SessionExportVisitor(
        SessionExportJob handle,
        IPacketListener exporter,
        FilterObserver? observerFilter,
        PacketFilter? treeFilter)
    {
        _Handle = handle;
        _Exporter = exporter;
        _ObserverFilter = observerFilter;
        _TreeFilter = treeFilter;
    }

    #endregion

    #region Properties

    /// <summary>Whether the export must not continue: the exporter stopped itself or the filter failed.</summary>
    internal bool Stopped { get; private set; }

    /// <summary>Whether <see cref="Stopped"/> was caused by the cancellation token (the job then ends as cancelled, not completed).</summary>
    internal bool StoppedByCancellation { get; private set; }

    /// <summary>
    /// The token of the job, set by the job thread before the first packet. It is checked for every packet (one volatile read), so
    /// a cancellation or a restart ends the export after the packet in progress and not at the end of a 4 096-id window, which for
    /// a field-tree export can be seconds.
    /// </summary>
    internal CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// The session the job reads from, set by the job thread before the first packet. It supplies the session's packet index to the
    /// field-tree filter: this job's parse is a replay of ids that ingest parsed first, and a replay fills no index.
    /// </summary>
    internal ISessionReader? Reader { get; set; }

    #endregion

    #region IPacketVisitor

    /// <inheritdoc/>
    public bool Visit(Packet packet)
    {
        // A cancelled job commits nothing further: the packet that was just parsed is dropped, the exporter keeps what it has.
        if (CancellationToken.IsCancellationRequested)
        {
            Stopped = true;
            StoppedByCancellation = true;
            return false;
        }

        _Handle.AddVisited();

        // Verdict first. A filter that cannot decide must not keep or drop data silently, so its failure ends the export.
        bool matched = true;
        if (_ObserverFilter is not null)
        {
            if (!_ObserverFilter.TryReadMatch(out matched, out FilterError? observerFailure))
            {
                string reason = "The filter produced no verdict.";
                if (observerFailure is not null)
                {
                    reason = observerFailure.Message;
                }

                _Handle.SetFailure(new SessionFailure(SessionErrorCode.FilterFailed, reason));
                Stopped = true;
                return false;
            }
        }
        else if (_TreeFilter is not null)
        {
            // The session's index of the stack the packet was parsed on. The filter ignores an index of another stack (a view taken
            // across a restart), and a session without an index passes the default view, so the filter scans the packet instead.
            PacketIndexReaderView index = Reader?.PacketIndex ?? default;
            if (!_TreeFilter.TryIsMatch(packet, index, out matched, out FilterError? treeFailure))
            {
                _Handle.SetFailure(new SessionFailure(SessionErrorCode.FilterFailed, treeFailure.Message));
                Stopped = true;
                return false;
            }
        }

        if (!matched)
        {
            return true;
        }

        _Handle.AddExported();
        if (!_Exporter.OnPacket(packet))
        {
            // The exporter unsubscribed itself (a strict-mode error, a target count): the read ends after this packet and the job
            // finishes the exporter.
            Stopped = true;
            return false;
        }

        return true;
    }

    #endregion
}
