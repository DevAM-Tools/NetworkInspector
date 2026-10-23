// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A trace that <see cref="TraceKit"/> has built (or found in the cache): the capture file, how many frames it holds, and
/// the profile settings a test has to apply for the trace to decode (for example the signal configuration file of the CAN
/// trace). Immutable.
/// </summary>
/// <param name="Path">Full path of the capture file in the cache folder.</param>
/// <param name="FrameCount">Number of frames in the file.</param>
/// <param name="ProfileSettings">
/// Setting name to text value (the Core text form); empty when the trace needs none. A value that names a file (the signal
/// configuration) is an absolute path inside the cache folder, and Core accepts a configuration file only inside the settings
/// base, so a test that applies these settings uses the cache folder (or a folder above it) as the storage of its settings.
/// </param>
public sealed record TraceFile(string Path, int FrameCount, IReadOnlyDictionary<string, string> ProfileSettings);
