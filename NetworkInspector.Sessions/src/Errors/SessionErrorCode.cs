// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Error codes shared by <see cref="SessionException"/> (bugs and conditions the caller cannot handle locally) and
/// <see cref="SessionFailure"/> (expected failures returned from <c>Try*</c> calls). A code does not say which of the
/// two carries it; the doc of the member that reports it does. Members take their numeric value from declaration
/// order, so new members are appended at the end to keep existing values stable.
/// </summary>
public enum SessionErrorCode
{
    #region Values

    /// <summary>The operation is not valid in the current session phase.</summary>
    InvalidPhase,

    /// <summary>The listener's <c>UiName</c> is null or whitespace.</summary>
    ListenerUiNameEmpty,

    /// <summary>The job's <c>UiName</c> is null or whitespace.</summary>
    JobUiNameEmpty,

    /// <summary>
    /// A job removal was requested but the job is still pending or running.
    /// Cancel the job first and wait for it to finish before removing it. Reported as a
    /// <see cref="SessionFailure"/> by <c>TryRemoveJob</c>: a caller may poll for it while a job winds down, so it is an
    /// expected refusal, not a thrown <see cref="SessionException"/>.
    /// </summary>
    JobStillRunning,

    /// <summary>
    /// The session has been disposed. Reported as a <see cref="SessionException"/> by the registration members; using
    /// a disposed session is a caller bug.
    /// </summary>
    Disposed,

    /// <summary>The maximum number of job IDs has been allocated.</summary>
    JobIdExhausted,

    /// <summary>The maximum number of listener IDs has been allocated.</summary>
    ListenerIdExhausted,

    /// <summary>
    /// The maximum number of packet IDs has been allocated (valid ids are <c>0 .. ArrayIndexIdRange.MaxValue</c>,
    /// bounded by the maximum .NET array length because ids index arrays). Reported as a
    /// <see cref="SessionException"/> during ingest.
    /// </summary>
    PacketIdExhausted,

    /// <summary>
    /// A mapped frame could not be re-read (<see cref="IRandomAccessFrameSource.FrameById"/>
    /// returned <see langword="null"/>, the mapping slot is missing, or the source is not
    /// random-access). A restart (which then leaves the session <see cref="SessionPhase.Faulted"/> and reports it as a
    /// <see cref="SessionFailure"/>) and ValueCache PullFill (which throws a <see cref="SessionException"/>) fail closed
    /// instead of compressing PacketIds or skipping rows, because ids must stay dense and aligned with the packet-to-frame
    /// mapping. Packet and frame reads report it as a <see cref="SessionFailure"/>.
    /// </summary>
    FrameUnavailable,

    /// <summary>The supplied <c>ListenerId</c> does not identify a registered listener.</summary>
    ListenerNotFound,

    /// <summary>The value-cache listener's <c>UiName</c> is null or whitespace.</summary>
    ValueCacheUiNameEmpty,

    /// <summary>The maximum number of value-cache IDs has been allocated.</summary>
    ValueCacheIdExhausted,

    /// <summary>
    /// The supplied <c>ValueCacheId</c> does not identify a registered value cache. (No code in the session currently
    /// reports this value.)
    /// </summary>
    ValueCacheNotFound,

    /// <summary>A field or group name in the value-cache request is not on the current stack.</summary>
    ValueCacheUnknownField,

    /// <summary>
    /// A field or group name in the value-cache request is not a valid protocol/field identifier
    /// (<see cref="NameValidation.IsValidName"/>).
    /// </summary>
    ValueCacheInvalidFieldName,

    /// <summary>
    /// <see cref="SessionOptions.ValueCacheListener"/> was set without
    /// <see cref="SessionOptions.ValueCache"/>.
    /// </summary>
    ValueCacheListenerWithoutRequest,

    /// <summary>
    /// Packet and frame reads, and new subscriptions, are refused while queries are disabled: during a restart (the
    /// stack is being swapped and ids are re-parsed) and after shutdown. A pause for a restart is temporary and
    /// after shutdown it is permanent; neither case means a frame is missing.
    /// </summary>
    QueriesDisabled,

    /// <summary>The packet id is not valid or not in the announced range.</summary>
    PacketNotFound,

    /// <summary>The job is not registered in this session.</summary>
    JobNotFound,

    /// <summary>The job is already completed, cancelled, or failed.</summary>
    JobTerminal,

    /// <summary>
    /// A value-cache request is well-formed as far as names go but the cache cannot be built from it: the request is
    /// empty (no field, no group, and no <c>RecordAllFields</c>), a field records nothing, a payload series is listed
    /// twice, or <c>ChunkShift</c> is outside its range. Reported as a <see cref="SessionException"/>; the message of
    /// the exception names the cause. Names that are not on the stack are <see cref="ValueCacheUnknownField"/>, names
    /// that are not identifiers are <see cref="ValueCacheInvalidFieldName"/>.
    /// </summary>
    ValueCacheInvalidRequest,

    /// <summary>
    /// A stack-bound job was refused because a restart is between its job check and its end: a job added then would extend
    /// the restart's wait or run on into the swap. Reported as a <see cref="SessionFailure"/> by <c>TryAddJob</c>; an
    /// <see cref="Jobs.JobStackBinding.Independent"/> job is accepted. Retry after the restart returned.
    /// </summary>
    RestartPending,

    /// <summary>
    /// A restart was refused because it was called from the thread of a running stack-bound job: under
    /// <see cref="RestartJobPolicy.WaitThenCancel"/> it would wait for itself, under <see cref="RestartJobPolicy.CancelNow"/> it
    /// would cancel itself. Use an <see cref="Jobs.JobStackBinding.Independent"/> job or a worker thread. Reported as a
    /// <see cref="SessionFailure"/> by <c>TryRestart</c>; nothing changed.
    /// </summary>
    RestartFromJob,

    /// <summary>
    /// A restart was refused under <see cref="RestartJobPolicy.Refuse"/> because stack-bound jobs are running; the message names
    /// them. Reported as a <see cref="SessionFailure"/> by <c>TryRestart</c>; nothing changed.
    /// </summary>
    JobsRunning,

    /// <summary>
    /// The session is <see cref="SessionPhase.Faulted"/>: a restart failed after the stack was swapped. Every <c>Try*</c> call
    /// except <c>TryRestart</c> and <c>Shutdown</c> and every packet read reports this code; the message carries the original
    /// fault (<see cref="ISessionReader.Fault"/>). Reported as a <see cref="SessionFailure"/>.
    /// </summary>
    SessionFaulted,

    /// <summary>
    /// Parse options were built for another stack generation than the one the session reads with now (see
    /// <see cref="ISessionReader.TryGetPacket(PacketId, in ParseOptions, StackView, Packet?, out Packet?, out SessionFailure?)"/>);
    /// the message names both generations. Rebuild the options on <see cref="ISessionReader.CurrentStack"/>. Reported as a
    /// <see cref="SessionFailure"/>.
    /// </summary>
    OptionsStackMismatch,

    /// <summary>
    /// A restart was refused because another restart is running. Reported as a <see cref="SessionFailure"/> by
    /// <c>TryRestart</c> (two UI commands can race, so it is an expected outcome, not an exception).
    /// </summary>
    RestartInProgress,

    /// <summary>
    /// A restart option is outside its documented range (<see cref="RestartOptions.JobWaitTimeout"/>: 1 s .. 1 h); the message
    /// names the option, the value and the range. Reported as a <see cref="SessionFailure"/> by <c>TryRestart</c>; nothing
    /// changed.
    /// </summary>
    RestartOptionOutOfRange,

    #endregion
}
