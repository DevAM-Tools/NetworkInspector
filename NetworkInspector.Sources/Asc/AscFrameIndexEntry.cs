// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// A single entry in the ASC frame index, storing the location and type of
/// a frame-producing line for random access.
/// </summary>
internal readonly struct AscFrameIndexEntry
{
    /// <summary>
    /// For in-memory backend: zero-based index into the <c>string[]</c> lines array.
    /// For disk backend: byte offset of the line start in the file.
    /// </summary>
    internal long Location
    {
        get; init;
    }

    /// <summary>The classified type of this line.</summary>
    internal AscLineType LineType
    {
        get; init;
    }
}
