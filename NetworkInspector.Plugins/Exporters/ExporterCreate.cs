// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Creates the Core listener for one export. The target was prepared by the application (existence and overwrite are decided
/// before this call); returns <see langword="false"/> with an error for every expected failure and never throws for them.
/// </summary>
/// <typeparam name="TListener">The Core listener type.</typeparam>
/// <param name="request">The request: target, applied options, stack, expected item count, cancellation.</param>
/// <param name="listener">The created listener; <see langword="null"/> on failure.</param>
/// <param name="error">Why no listener was created; <see langword="null"/> on success.</param>
/// <returns><see langword="true"/> when <paramref name="listener"/> is set.</returns>
public delegate bool ExporterCreate<TListener>(ExportRequest request, [NotNullWhen(true)] out TListener? listener,
    [NotNullWhen(false)] out PluginError? error) where TListener : class;
