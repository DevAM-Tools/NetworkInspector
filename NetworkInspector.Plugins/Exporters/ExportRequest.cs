// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>One export request handed to <see cref="ExporterCreate{TListener}"/>.</summary>
/// <param name="TargetPath">Full path of the output file (or directory for directory formats), already prepared by the application.</param>
/// <param name="Options">The applied option settings, registered by <see cref="ExporterRegistration{TListener}.RegisterOptions"/>.</param>
/// <param name="Stack">The stack that parsed the packets, so listeners can resolve field ids by name.</param>
/// <param name="ExpectedItemCount">Informational number of items to write (progress only); never a stop condition.</param>
/// <param name="CancellationToken">Cancels the export.</param>
public sealed record ExportRequest(string TargetPath, ReadOnlySettingsManagerView Options, IStack Stack,
    long ExpectedItemCount, CancellationToken CancellationToken);
