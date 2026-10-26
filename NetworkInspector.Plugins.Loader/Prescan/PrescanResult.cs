// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>Result of inspecting one candidate file; immutable and cached by path, length, and last write time.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Length">
/// File length in bytes when it was inspected. A result with length 0 and time 0 means "could not be read or vanished": it
/// never equals a real file, so such a file is inspected again at the next scan instead of being remembered.
/// </param>
/// <param name="LastWriteUtcTicks">Last write time (UTC ticks) when it was inspected; 0 for the untrusted result above.</param>
/// <param name="AssemblyName">Assembly name from metadata; <see langword="null"/> for files that are not managed assemblies.</param>
/// <param name="AssemblyVersion">Assembly version from metadata; <see langword="null"/> for files that are not managed assemblies.</param>
/// <param name="Entries">The entry types found; empty for files that are not plugins.</param>
internal sealed record PrescanResult(string Path, long Length, long LastWriteUtcTicks, string? AssemblyName,
    string? AssemblyVersion, IReadOnlyList<PrescanEntry> Entries)
{
    /// <summary>True when the file holds at least one entry type.</summary>
    internal bool IsPluginAssembly => Entries.Count > 0;
}
