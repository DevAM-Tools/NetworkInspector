// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Error codes for <see cref="SessionException"/>.
/// </summary>
public enum SessionErrorCode
{
    /// <summary>The operation is not valid in the current session phase.</summary>
    InvalidPhase,

    /// <summary>The listener's <c>UiName</c> is null or whitespace.</summary>
    ListenerUiNameEmpty,

    /// <summary>The job's <c>UiName</c> is null or whitespace.</summary>
    JobUiNameEmpty,

    /// <summary>
    /// A job removal was requested but the job is still pending or running.
    /// Cancel the job first and wait for it to finish before removing it.
    /// </summary>
    JobStillRunning,

    /// <summary>The session has been disposed.</summary>
    Disposed,

    /// <summary>The maximum number of job IDs has been allocated.</summary>
    JobIdExhausted,

    /// <summary>The maximum number of listener IDs has been allocated.</summary>
    ListenerIdExhausted,

    /// <summary>The maximum number of packet IDs has been allocated.</summary>
    PacketIdExhausted,

    /// <summary>The supplied <c>ListenerId</c> does not identify a registered listener.</summary>
    ListenerNotFound,

    /// <summary>The value-cache listener's <c>UiName</c> is null or whitespace.</summary>
    ValueCacheUiNameEmpty,

    /// <summary>The maximum number of value-cache IDs has been allocated.</summary>
    ValueCacheIdExhausted,

    /// <summary>The supplied <c>ValueCacheId</c> does not identify a registered value cache.</summary>
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
}
