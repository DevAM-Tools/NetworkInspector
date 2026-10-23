// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// An expected failure of a source or exporter registration (missing file, unsupported format, invalid option). It is
/// returned, never thrown, so that callers can show one precise line.
/// </summary>
/// <param name="Code">What kind of failure it is; callers map it to exit codes and dialog texts.</param>
/// <param name="Message">English text for the user; one line.</param>
/// <param name="Option">
/// Name of the option at fault, or <c>paths</c> when the file list is at fault; <see langword="null"/> when no single
/// option is to blame.
/// </param>
public readonly record struct PluginError(PluginErrorCode Code, string Message, string? Option = null);
