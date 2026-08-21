// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Asc;

/// <summary>
/// A single entry in the ASC frame index, storing the location and type of
/// a frame-producing line for random access.
/// </summary>
/// <param name="Location">
/// For in-memory backend: zero-based index into the <c>string[]</c> lines array.
/// For disk backend: byte offset of the line start in the file.
/// </param>
/// <param name="LineType">The classified type of this line.</param>
internal readonly record struct AscFrameIndexEntry(long Location, AscLineType LineType);
