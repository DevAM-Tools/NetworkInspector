// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>One export request of a format that writes through streams, handed to <see cref="StreamExporterCreate{TListener}"/>.</summary>
/// <param name="Target">
/// Where the export writes; the format opens <see cref="ExportEntryName.Main"/> (single-file format) or one entry per file
/// (dataset format). The host prepared it; the format never sees a path.
/// </param>
/// <param name="Options">The applied option settings, registered by <see cref="ExporterRegistration{TListener}.RegisterOptions"/>.</param>
/// <param name="Stack">The stack that parsed the packets, so listeners can resolve field ids by name.</param>
/// <param name="ExpectedItemCount">Informational number of items to write (progress only); never a stop condition.</param>
/// <param name="CancellationToken">Cancels the export.</param>
/// <seealso cref="FileExportRequest"/>
public sealed record StreamExportRequest(IExportTarget Target, ReadOnlySettingsManagerView Options, IStack Stack,
    long ExpectedItemCount, CancellationToken CancellationToken);
