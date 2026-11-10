// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Controls how a source scans the input file during opening.
/// </summary>
public enum ScanMode
{
    /// <summary>
    /// Scans the entire file upfront, while the source is opened, before any frame is returned.
    /// Slower to open, but <see cref="IFrameSource.EstimatedFrameCount"/> is
    /// available immediately and the frame index needed for random access is complete once the source is open.
    /// A format that cannot scan lazily (for example ASC) always behaves like this mode.
    /// </summary>
    Full,

    /// <summary>
    /// Scans the file incrementally as frames are requested.
    /// Fast open, but <see cref="IFrameSource.EstimatedFrameCount"/> returns
    /// <c>null</c> until the file is fully consumed.
    /// </summary>
    Lazy,
}
