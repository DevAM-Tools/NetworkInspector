// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// An export format. Contract rule: a listener that is disposed without <c>OnFinish</c> leaves no partial output — either it
/// deletes its output itself, or (for the Core exporters, whose <c>Dispose</c> finishes the file) the application's export
/// runner deletes the target afterwards.
/// </summary>
/// <typeparam name="TListener">The Core listener this format creates: <see cref="IFrameListener"/> or <see cref="IPacketListener"/>.</typeparam>
/// <param name="Format">Unique format name, same rule as source kinds, unique across frame and packet exporters.</param>
/// <param name="UiName">Name shown in dialogs and listings; not empty.</param>
/// <param name="Description">One sentence about the output; never <see langword="null"/>.</param>
/// <param name="FileExtension">Extension of the output file starting with <c>.</c>; empty for directory output.</param>
/// <param name="RegisterOptions">
/// Registers the option settings into a fresh <see cref="SettingsManager"/> per request (see <see cref="SourceRegistration"/>).
/// </param>
/// <param name="Create">Creates the listener for one export.</param>
public sealed record ExporterRegistration<TListener>(string Format, string UiName, string Description,
    string FileExtension, Action<SettingsRegistrar> RegisterOptions, ExporterCreate<TListener> Create)
    where TListener : class;
