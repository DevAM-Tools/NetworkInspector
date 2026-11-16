// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Creates the Core listener of a format that writes through streams. The host prepared the target; the format opens its entries
/// itself, usually with the first item. Returns <see langword="false"/> with an error for every expected failure and never throws
/// for them (returned errors let callers show one precise line; a throw signals a bug in the plugin).
/// </summary>
/// <typeparam name="TListener">The Core listener type.</typeparam>
/// <param name="request">The request: target, applied options, stack, expected item count, cancellation.</param>
/// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
/// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
/// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
/// <seealso cref="FileExporterCreate{TListener}"/>
public delegate bool StreamExporterCreate<TListener>(StreamExportRequest request, [NotNullWhen(true)] out TListener? listener,
    [NotNullWhen(false)] out PluginError? error) where TListener : class;
