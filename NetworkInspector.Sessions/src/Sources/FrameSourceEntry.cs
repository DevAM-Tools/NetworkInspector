// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Sources;

/// <summary>
/// Internal binding between an <see cref="IFrameSource"/>, its registered
/// <see cref="FrameSourceInfo"/>, and the <see cref="Job"/> that drives it.
/// </summary>
internal sealed class FrameSourceEntry
{
    internal FrameSourceEntry(FrameSourceInfo info, IFrameSource source, Job job)
    {
        Info = info;
        Source = source;
        Job = job;
        // Create the public view once so we can add/remove the same reference
        // from the unified job list in Session.
        JobInfo = new JobInfo(job);
    }

    /// <summary>Stack-registered metadata for this source.</summary>
    internal FrameSourceInfo Info
    {
        get;
    }

    /// <summary>The original frame source instance (needed for <see cref="Session.Restart"/>).</summary>
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
    /// unified <see cref="Session"/> job list across restarts.
    /// </summary>
    internal JobInfo JobInfo
    {
        get;
    }
}
