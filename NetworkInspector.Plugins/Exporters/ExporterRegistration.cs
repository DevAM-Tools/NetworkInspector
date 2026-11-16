// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// An export format. Contract rule: a listener that is disposed without <c>OnFinish</c> leaves no partial output — a format that
/// writes through the streams of a target leaves the removal to the host (the host owns the target); a format that opens a
/// file itself deletes its file itself, or the host deletes the path afterwards.
/// </summary>
/// <typeparam name="TListener">The Core listener this format creates: <see cref="IFrameListener"/> or <see cref="IPacketListener"/>.</typeparam>
/// <param name="Format">Unique format name, same rule as source kinds, unique across frame and packet exporters.</param>
/// <param name="UiName">Name shown in dialogs and listings; not empty.</param>
/// <param name="Description">One sentence about the output; never <see langword="null"/>.</param>
/// <param name="FileExtension">
/// Extension of the output file starting with <c>.</c>; empty for directory output. A format that receives a file path
/// (<see cref="ExporterCreation.ForFile{TListener}"/>) must have an extension.
/// </param>
/// <param name="RegisterOptions">
/// Registers the option settings into a fresh <see cref="SettingsManager"/> per request (see <see cref="SourceRegistration"/>).
/// </param>
/// <param name="Creation">
/// How the format creates its listener: from a target of streams or from a prepared file path; exactly one of the two.
/// </param>
public sealed record ExporterRegistration<TListener>(string Format, string UiName, string Description,
    string FileExtension, Action<SettingsRegistrar> RegisterOptions, ExporterCreation<TListener> Creation)
    where TListener : class;
