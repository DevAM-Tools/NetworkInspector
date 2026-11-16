// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Creates the Core listener of a format whose library opens the output file itself. The host prepared the path (existence and
/// overwrite are decided before this call). Returns <see langword="false"/> with an error for every expected failure and never
/// throws for them (returned errors let callers show one precise line; a throw signals a bug in the plugin).
/// </summary>
/// <typeparam name="TListener">The Core listener type.</typeparam>
/// <param name="request">The request: file path, applied options, stack, expected item count, cancellation.</param>
/// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
/// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
/// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
/// <seealso cref="StreamExporterCreate{TListener}"/>
public delegate bool FileExporterCreate<TListener>(FileExportRequest request, [NotNullWhen(true)] out TListener? listener,
    [NotNullWhen(false)] out PluginError? error) where TListener : class;
