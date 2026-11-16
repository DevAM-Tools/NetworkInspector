// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>One export request of a format whose library opens the file itself, handed to <see cref="FileExporterCreate{TListener}"/>.</summary>
/// <param name="FilePath">
/// Full path of the output file, prepared by the host: its folder exists and nothing lies at the path. Only for formats whose
/// library must open the file itself.
/// </param>
/// <param name="Options">The applied option settings, registered by <see cref="ExporterRegistration{TListener}.RegisterOptions"/>.</param>
/// <param name="Stack">The stack that parsed the packets, so listeners can resolve field ids by name.</param>
/// <param name="ExpectedItemCount">Informational number of items to write (progress only); never a stop condition.</param>
/// <param name="CancellationToken">Cancels the export.</param>
/// <seealso cref="StreamExportRequest"/>
public sealed record FileExportRequest(string FilePath, ReadOnlySettingsManagerView Options, IStack Stack,
    long ExpectedItemCount, CancellationToken CancellationToken);
