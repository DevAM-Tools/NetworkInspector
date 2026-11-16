// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Where an export writes: the host hands an exporter a target instead of a path, and the exporter receives one write stream per
/// entry, an entry being one named output file of the export. The host decides where each entry lands (a file, a folder, a
/// network share, memory); the exporter never learns it. A single-file format opens one entry, <see cref="ExportEntryName.Main"/>;
/// a dataset format (Parquet) opens one entry per table file.
/// <para>
/// <b>Thread safety:</b> an exporter calls <see cref="TryOpenWrite"/> sequentially, but the calls may come from different threads
/// (the caller's thread and the exporter's writer thread); an implementation must not depend on thread affinity.
/// </para>
/// </summary>
/// <remarks>
/// Security: a target is a contract, not a sandbox. Plugins are trusted in-process code and can still call file APIs; the
/// target keeps the built-in exporters away from the file system and lets the host route the output.
/// Decision: docs/decisions/export-output-target.md.
/// </remarks>
/// <seealso cref="ExportEntryName"/>
public interface IExportTarget
{
    /// <summary>Opens one entry for writing.</summary>
    /// <param name="name">The entry; valid by construction of <see cref="ExportEntryName"/>.</param>
    /// <param name="stream">
    /// A write-only stream that need not seek or report a position; <see langword="null"/> on failure. The caller owns it and
    /// disposes it when the entry is complete; disposing ends the entry.
    /// </param>
    /// <param name="failure">Why the entry cannot be opened, in one sentence for the export error; empty on success.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="stream"/> is set. <see langword="false"/> for every expected failure (the
    /// target refuses the name, the entry was opened before, the storage is unavailable, the target's own limit of entries is
    /// reached); an implementation throws only for bugs. It never blocks until another entry is closed: a dataset format keeps
    /// all its entries open until the export finishes, so a wait would never end.
    /// </returns>
    bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure);
}
