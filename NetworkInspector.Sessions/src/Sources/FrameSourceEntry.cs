// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Sources;

/// <summary>
/// Internal binding between an <see cref="IFrameSource"/>, its registered
/// <see cref="FrameSourceInfo"/>, and the <see cref="Job"/> that drives it.
/// <para>
/// Thread safety: immutable after construction (get-only properties); the referenced objects have their own contracts.
/// </para>
/// </summary>
internal sealed class FrameSourceEntry
{
    /// <summary>Binds a registered source to its job and creates the public job view once.</summary>
    /// <param name="info">Stack-registered metadata of the source.</param>
    /// <param name="source">The bound source (possibly the cache wrapper), which the session disposes.</param>
    /// <param name="job">The job that reads frames from <paramref name="source"/>.</param>
    internal FrameSourceEntry(FrameSourceInfo info, IFrameSource source, Job job)
    {
        Info = info;
        Source = source;
        Job = job;

        // Create the public view once so we can add/remove the same reference
        // from the unified job list in Session. Lookups by TryUnsubscribe compare it by reference, so a second
        // JobInfo wrapper for the same job would never be found.
        JobInfo = new JobInfo(job);
    }

    #region Fields

    /// <summary>Stack-registered metadata for this source.</summary>
    internal FrameSourceInfo Info
    {
        get;
    }

    /// <summary>
    /// The source as bound by the session: the <see cref="CachedFrameSource"/> wrapper when the session wrapped it,
    /// otherwise the caller's instance. The session disposes it at shutdown (or on removal of a never-started source);
    /// it stays available for random access after its job ended.
    /// </summary>
    internal IFrameSource Source
    {
        get;
    }

    /// <summary>The job that reads frames from this source on a dedicated thread.</summary>
    internal Job Job
    {
        get;
    }

    /// <summary>
    /// Public read-only view of the source job.
    /// Stored here so the same reference can be added to and removed from the
    /// unified <see cref="Session"/> job list; <c>Session.TryUnsubscribe</c> finds the entry by reference equality
    /// with this instance.
    /// </summary>
    internal JobInfo JobInfo
    {
        get;
    }

    #endregion
}
