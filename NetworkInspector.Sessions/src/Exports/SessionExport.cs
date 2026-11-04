// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Runs an export of a packet range as a stack-bound session job by the pull principle.
/// <para>
/// <b>How it works:</b> on the caller's thread, the current <see cref="StackView"/> is taken, the exporter is created on it and the
/// filter is compiled on it (a tree-free <see cref="FilterObserver"/> when the expression allows; the field-tree filter when it
/// needs the tree). One <see cref="ParsePlan"/> combines the filter's demand (first, so the verdict exists before the exporter
/// commits) and the exporter's demand (<see cref="IParseDemandSource"/>; a listener without it demands a field tree). The job
/// then pulls the range in windows of 4 096 ids through <see cref="ISessionReader.TryVisitPackets"/>, commits matching packets
/// with <see cref="IPacketListener.OnPacket"/>, and calls <see cref="IPacketListener.OnFinish"/> in every outcome.
/// </para>
/// <para>
/// <b>Why a job and not a listener:</b> a listener is notified per ingest wake and reads what accumulated; an export has a fixed
/// range and ends. The job thread parses; ingest only publishes ids, so a long export never slows ingest (pull principle), and no
/// ingest parse ever carries the export's observer.
/// </para>
/// <para>
/// <b>Filter forms:</b> the same rule as the command line: the observer form is tried first and any compile failure falls back
/// to the field-tree filter when that one compiles; a syntax error fails both and is returned once, with the tree compiler's
/// message. A session job and the command line therefore accept and judge the same expressions. The field-tree filter reads
/// protocol presence from the session's own packet index (the packets were first-parsed by ingest, so this job's parse is a
/// replay and fills no index of its own).
/// </para>
/// <para>
/// <b>Restart:</b> the job is <see cref="JobStackBinding.StackBound"/>; a restart ends it per the caller's restart policy
/// (<see cref="JobEndReason.StackReplaced"/>). The exporter is finished, so the output holds the packets committed before; the
/// owner starts a new export on the new stack. A job whose thread starts on another generation than the one the exporter was
/// built on finishes the exporter without packets and reports <see cref="SessionErrorCode.OptionsStackMismatch"/>.
/// </para>
/// <para>
/// <b>Ownership of the exporter:</b> after a successful start the job finishes the exporter when it ends and the caller disposes it
/// (<see cref="SessionExportJob.Exporter"/>) after <c>Job.Join()</c>; when <c>TryStart</c> returns <see langword="false"/> after the
/// exporter was created, <c>TryStart</c> has finished and disposed it.
/// </para>
/// <para><b>Thread-safety:</b> stateless; callable from any thread. The exporter must not be used by anyone else after the call.</para>
/// </summary>
public static class SessionExport
{
    /// <summary>
    /// Ids per <see cref="ISessionReader.TryVisitPackets"/> call: bounds how long a call holds the stack lease after a restart
    /// starts (the call ends at the next packet anyway) and how often cancellation is checked between calls.
    /// Value 4 096 · threat: a retired stack kept alive by a long call, a cancellation noticed late · constant (a tuning value,
    /// not a limit on the export size) · no security posture.
    /// </summary>
    private const int _WindowSize = 4096;

    /// <summary>Starts the export job.</summary>
    /// <param name="session">The session to export from.</param>
    /// <param name="uiName">Job name shown in the job list.</param>
    /// <param name="createExporter">Creates the exporter on the given view (resolve field names on <see cref="StackView.Stack"/>).</param>
    /// <param name="fromId">First packet id (inclusive), ≥ 0.</param>
    /// <param name="toIdExclusive">End of the range (exclusive), ≤ the session's packet count.</param>
    /// <param name="filterExpression">Optional filter; <see langword="null"/> or whitespace exports every packet of the range.</param>
    /// <param name="export">The handle; set on success.</param>
    /// <param name="failure">Session refusal (range, plan conflict, phase, restart pending); set when the call returns <see langword="false"/> without a filter error.</param>
    /// <param name="filterError">Compile error of <paramref name="filterExpression"/>; set when the call returns <see langword="false"/> because of it.</param>
    /// <returns><see langword="true"/> when the job was started.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/>, <paramref name="uiName"/> or <paramref name="createExporter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromId"/> is negative or <paramref name="toIdExclusive"/> is below it.</exception>
    public static bool TryStart(
        ISession session,
        string uiName,
        Func<StackView, IPacketListener> createExporter,
        int fromId,
        int toIdExclusive,
        string? filterExpression,
        [NotNullWhen(true)] out SessionExportJob? export,
        out SessionFailure? failure,
        out FilterError? filterError)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(uiName);
        ArgumentNullException.ThrowIfNull(createExporter);
        ArgumentOutOfRangeException.ThrowIfNegative(fromId);
        ArgumentOutOfRangeException.ThrowIfLessThan(toIdExclusive, fromId);
        export = null;
        failure = null;
        filterError = null;

        // Range against the published count (the same refusal as TryVisitPackets), checked here so the caller gets it
        // synchronously and not as a failed job.
        int packetCount = session.PacketCount;
        if (toIdExclusive > packetCount)
        {
            failure = new SessionFailure(
                SessionErrorCode.PacketNotFound,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The range {0} to {1} (exclusive) reaches past the {2} announced packets.",
                    fromId,
                    toIdExclusive,
                    packetCount));
            return false;
        }

        // One view for everything built now: the exporter (field names → ids), the filter and the plan. If a restart replaces the
        // stack before the job thread starts, the generation check in _Run refuses the parse instead of using stale ids.
        StackView view = session.CurrentStack;

        // Filter: the tree-free observer first, the field-tree filter when the observer form cannot express the expression.
        FilterObserver? observerFilter = null;
        PacketFilter? treeFilter = null;
        ParseDemand filterDemand = ParseDemand.FramesOnly;
        if (!string.IsNullOrWhiteSpace(filterExpression))
        {
            FilterObserverOptions observerOptions = FilterObserverOptions.Default;
            if (PacketFilter.TryCompile(filterExpression, view.Stack, in observerOptions, out FilterObserver? observer, out FilterError? _))
            {
                observerFilter = observer;
                filterDemand = observer.ParseDemand;
            }
            else if (PacketFilter.TryCompile(filterExpression, view.Stack, out PacketFilter? tree, out FilterError? treeError))
            {
                // Every id of the range was parsed once by ingest, so a parse here is a replay, and a replay records nothing into an
                // index (Packet._ParseAndSeal). A private index would stay empty and make every protocol-presence query answer
                // "absent". The tree filter therefore reads the session's own index per packet (SessionExportVisitor.Visit); the
                // demand is the field tree alone. Rejected: adding the session index to the demand (a parse never writes it on a
                // replay, and a plan with an index would conflict with an index of the caller). Residual risk: none; a session
                // without an index makes the filter scan each packet (slower, correct).
                treeFilter = tree;
                filterDemand = ParseDemand.FieldTree;
            }
            else
            {
                filterError = treeError;
                return false;
            }
        }

        // Exporter and plan. A plan conflict (two caches or indexes) is a caller error reported as a session failure; the exporter
        // is finished so that it releases what its constructor opened.
        IPacketListener exporter = createExporter(view);
        ParseDemand exporterDemand = ParseDemand.FieldTree;
        if (exporter is IParseDemandSource demandSource)
        {
            exporterDemand = demandSource.ParseDemand;
        }

        ReadOnlySpan<ParseDemand> demands = [filterDemand, exporterDemand];
        if (!ParsePlan.TryCreate(view.Stack, demands, out ParsePlan? plan, out ParsePlanError planError))
        {
            _FinishAndDispose(exporter);
            failure = new SessionFailure(SessionErrorCode.InvalidArgument, planError.Message);
            return false;
        }

        // The work runs entirely on the job thread (pull principle).
        SessionExportJob handle = new(fromId, toIdExclusive, exporter);
        SessionExportVisitor visitor = new(handle, exporter, observerFilter, treeFilter);
        string description = string.Create(CultureInfo.InvariantCulture, $"export packets {fromId}..{toIdExclusive}");
        if (!session.TryAddJob(
            uiName,
            description,
            JobStackBinding.StackBound,
            context => _Run(context, view, plan, visitor, handle),
            out JobInfo? job,
            out failure))
        {
            _FinishAndDispose(exporter);
            return false;
        }

        handle.Job = job;
        export = handle;
        return true;
    }

    /// <summary>Finishes an exporter that never got a job and disposes it, so a refused start leaves no open file behind.</summary>
    private static void _FinishAndDispose(IPacketListener exporter)
    {
        exporter.OnFinish();
        if (exporter is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// The job body: runs the windows and finishes the exporter in every outcome. A cancellation (user, restart, shutdown) ends
    /// the work by throwing the job's own cancellation after the exporter was finished, which is how a job reaches the
    /// <see cref="JobStatus.Cancelled"/> status; every other ending (the range done, the exporter or a failure stopped it) is a
    /// normal return.
    /// </summary>
    private static void _Run(JobContext context, StackView view, ParsePlan plan, SessionExportVisitor visitor, SessionExportJob handle)
    {
        bool ranToItsEnd;
        try
        {
            ranToItsEnd = _RunWindows(context, view, plan, visitor, handle);
        }
        finally
        {
            // Every outcome finishes the exporter: the packets committed so far form a valid output (exporter contract).
            handle.Exporter.OnFinish();
        }

        if (!ranToItsEnd)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The windows of <see cref="_WindowSize"/> ids through the reader; <see langword="false"/> when a cancellation ended them.</summary>
    private static bool _RunWindows(JobContext context, StackView view, ParsePlan plan, SessionExportVisitor visitor, SessionExportJob handle)
    {
        // The plan's ids belong to `view`; a job thread that starts after a restart must not parse with them.
        if (context.Stack.Generation != view.Generation)
        {
            handle.SetFailure(new SessionFailure(
                SessionErrorCode.OptionsStackMismatch,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The export was prepared for generation {0}; the job started on generation {1}.",
                    view.Generation,
                    context.Stack.Generation)));
            return true;
        }

        visitor.CancellationToken = context.CancellationToken;
        visitor.Reader = context.Session;
        ParseOptions options = plan.Options;
        for (int from = handle.FromId; from < handle.ToIdExclusive; from += _WindowSize)
        {
            // Cancellation is checked between windows and, through the visitor, for every packet inside one; a restart that
            // starts also ends the read through the reader's per-packet queries-disabled check.
            if (context.CancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (visitor.Stopped)
            {
                return true;
            }

            int to = (int)Math.Min((long)from + _WindowSize, handle.ToIdExclusive);
            if (!context.Session.TryVisitPackets(from, to, in options, view, visitor, out _, out SessionFailure? failure))
            {
                // A restart that ended the read is a cancellation (the job's end reason says StackReplaced), not a failure; a
                // failure the visitor recorded already is not overwritten.
                if (context.CancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (handle.Failure is null)
                {
                    handle.SetFailure(failure.Value);
                }

                return true;
            }

            // The visitor ended the window early: by the token (a cancelled job) or by the exporter or the filter (a normal end).
            if (visitor.StoppedByCancellation)
            {
                return false;
            }
        }

        return true;
    }
}
