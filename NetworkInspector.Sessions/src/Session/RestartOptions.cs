// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// What the caller of <c>TryRestart</c> chooses: the policy for running stack-bound jobs, the bound of the wait, and a token
/// that cuts the wait short.
///
/// <para>
/// <b>External input:</b> in a host the values come from an end user or an MCP peer, so <c>TryRestart</c> validates them
/// (<see cref="TryValidate"/>) and refuses an out-of-range value with
/// <see cref="SessionErrorCode.RestartOptionOutOfRange"/> instead of clamping it silently.
/// </para>
///
/// <para>Thread-safety: immutable record; safe to share. The token it carries is thread-safe.</para>
/// </summary>
public sealed record RestartOptions
{
    #region Constants

    /// <summary>
    /// Smallest accepted <see cref="JobWaitTimeout"/>. Constant (a fact of the contract, not a host-dependent value): below one
    /// second a wait is pointless and <see cref="RestartJobPolicy.CancelNow"/> says what is meant.
    /// </summary>
    internal static readonly TimeSpan MinJobWaitTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Largest accepted <see cref="JobWaitTimeout"/>. Above one hour the caller has lost track of a blocked thread; the
    /// ceiling keeps "wait forever" (<see cref="Timeout.InfiniteTimeSpan"/>, <see cref="TimeSpan.MaxValue"/>) out.
    /// </summary>
    internal static readonly TimeSpan MaxJobWaitTimeout = TimeSpan.FromHours(1);

    #endregion

    #region Properties

    /// <summary>
    /// The options of a restart that cancels stack-bound jobs at once. A shared immutable instance: no allocation per call.
    /// </summary>
    public static RestartOptions Default { get; } = new();

    /// <summary>What happens to the running stack-bound jobs. Default <see cref="RestartJobPolicy.CancelNow"/>.</summary>
    public RestartJobPolicy JobPolicy { get; init; } = RestartJobPolicy.CancelNow;

    // Limit (Section 4.2; class LIVE, posture OPEN, changeability L per the limits audit reviews/limits-audit_1.md
    // sections 2–3): bounds how long TryRestart may block waiting for StackBound jobs. Threat: a job that never ends holds
    // the restart and the caller's thread (a host command queue, a UI worker). Configurable by the caller per call; the host
    // exposes it to the end user as the session setting session.restartWaitSeconds ("wait up to", unit, range, default) and
    // caps what an MCP peer may request with the operator setting session.restartWaitMaxSeconds (X2 exposure, SEC). Range
    // 1 s .. 1 h: below 1 s a wait is pointless (cancel instead), above 1 h the user has lost track. Posture OPEN: a local
    // tool, the user's own choice to wait (X0). Exceeding the bound is an expected outcome (the rest is cancelled and listed
    // in the report), never silent. Rejected: no bound (hang), a bound derived from the host (nothing to derive it from).
    // Residual risk: a job that ignores its token stays Running after the bound (it is cancelled, not killed); the restart
    // continues and the job is listed in the report.
    /// <summary>
    /// The longest <see cref="RestartJobPolicy.WaitThenCancel"/> waits for the jobs, as one budget for all of them. Default
    /// 30 s; allowed range 1 s .. 1 h (validated by <c>TryRestart</c>, which refuses a value outside it and names the range).
    /// Ignored by the other policies. Raising it keeps a caller thread blocked longer; lowering it cancels slow jobs sooner.
    /// </summary>
    public TimeSpan JobWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cuts the wait short: when it fires during <see cref="RestartJobPolicy.WaitThenCancel"/>, the remaining jobs are
    /// cancelled at once and the restart continues (it does not abort: the user said "go on", an abort would need a second
    /// decision). Default: a token that never fires.
    /// </summary>
    public CancellationToken WaitCancellation { get; init; }

    #endregion

    #region Validation

    /// <summary>
    /// Validates the options as external input. The message follows the limits audit's hit form: what was refused, the value,
    /// the limit, and where to change it.
    /// </summary>
    /// <param name="options">The options to check.</param>
    /// <param name="failure">The refusal; <see langword="null"/> when the options are valid.</param>
    /// <returns><see langword="true"/> when every option is inside its range.</returns>
    internal static bool TryValidate(RestartOptions options, [NotNullWhen(false)] out SessionFailure? failure)
    {
        if (!Enum.IsDefined(options.JobPolicy))
        {
            failure = new SessionFailure(
                SessionErrorCode.RestartOptionOutOfRange,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Restart refused: the job policy {0} is not defined. Limit: CancelNow, WaitThenCancel or Refuse (option RestartOptions.JobPolicy).",
                    (int)options.JobPolicy));
            return false;
        }

        if (options.JobWaitTimeout < MinJobWaitTimeout || options.JobWaitTimeout > MaxJobWaitTimeout)
        {
            failure = new SessionFailure(
                SessionErrorCode.RestartOptionOutOfRange,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Restart refused: the job wait bound {0} is outside its range. Limit: 1 s .. 1 h (option RestartOptions.JobWaitTimeout).",
                    options.JobWaitTimeout));
            return false;
        }

        failure = null;
        return true;
    }

    #endregion
}
