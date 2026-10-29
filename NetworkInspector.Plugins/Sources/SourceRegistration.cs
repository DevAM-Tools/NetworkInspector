// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// A source kind: how the application opens a Core <see cref="IFrameSource"/> of this kind. The loader validates the record
/// when the plugin is configured, using the rules stated on each parameter below, so a broken registration fails that
/// plugin, not the application.
/// </summary>
/// <param name="Kind">Unique kind name, 1–256 characters matching <c>[A-Za-z][A-Za-z0-9_.-]*</c>; unique ignoring case.</param>
/// <param name="UiName">Name shown in dialogs and listings; not empty.</param>
/// <param name="Description">One sentence about what the source reads or generates; never <see langword="null"/>.</param>
/// <param name="FileExtensions">
/// Extensions (each starting with <c>.</c>) of the files this kind reads, used to pick a kind for a bare path; empty when the
/// kind reads no files.
/// </param>
/// <param name="AllowsMultipleFiles">
/// An application may select several files at once; it then creates one source per file, so every request carries exactly
/// one path.
/// </param>
/// <param name="RegisterOptions">
/// Registers the option settings (group <c>""</c>, Core names, types, bounds, defaults) into a fresh
/// <see cref="SettingsManager"/> per request. Core parses and validates every option text, so every application accepts the
/// same forms and gives the same messages.
/// </param>
/// <param name="Create">Creates the source from a request.</param>
public sealed record SourceRegistration(string Kind, string UiName, string Description,
    IReadOnlyList<string> FileExtensions, bool AllowsMultipleFiles,
    Action<SettingsRegistrar> RegisterOptions, SourceCreate Create);
