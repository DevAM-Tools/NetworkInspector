// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Failure counters of a <see cref="BlfIncrementalScanner"/>. <see cref="BlfSource"/> turns every increment into one
/// <c>FrameSkipped</c> event; each <see cref="BlfScanDiagnosticsMark"/> carries the counters after one failure, so the
/// source can do so in file order without keeping the scanner.
/// </summary>
/// <param name="DecompressionFailures">Containers that failed to decompress (reported as <c>Other</c>).</param>
/// <param name="CorruptedContainers">Containers whose header offset is out of bounds (<c>CorruptedBlock</c>).</param>
/// <param name="TruncatedObjects">Container tails that are neither padding nor a split object (<c>CorruptedBlock</c>).</param>
/// <param name="UnparsableObjects">Frame objects whose header or channel field could not be read (<c>MalformedHeader</c>).</param>
/// <param name="CorruptedInnerObjects">Corrupt bytes skipped inside containers (<c>CorruptedBlock</c>).</param>
/// <param name="TruncatedFileObjects">File-level objects cut by the end of the file (<c>TruncatedStream</c>).</param>
internal readonly record struct BlfScanDiagnostics(
    long DecompressionFailures,
    long CorruptedContainers,
    long TruncatedObjects,
    long UnparsableObjects,
    long CorruptedInnerObjects,
    long TruncatedFileObjects);

/// <summary>
/// One counted scanner failure: the counters right after it and the number of frames indexed at that moment (also
/// inside a container). <see cref="BlfSource"/> replays each mark before returning frame <see cref="FrameIndex"/>, so
/// strict mode stops exactly at the first failure in file order, in full and lazy scans alike.
/// </summary>
/// <param name="FrameIndex">Frames indexed before the failure; the failure lies between frame <c>FrameIndex - 1</c> and <c>FrameIndex</c>.</param>
/// <param name="Counts">Cumulative counters after the failure.</param>
internal readonly record struct BlfScanDiagnosticsMark(int FrameIndex, BlfScanDiagnostics Counts);
