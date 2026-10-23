// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Plugin metadata shown by the application and used as the identity of the plugin. The loader validates the record when
/// the plugin is configured; the contract itself keeps records plain data.
/// </summary>
/// <param name="Name">
/// Unique name, 1–256 characters, a dot-separated C-style identifier (<c>Sample.Protocol</c>). Names compare ignoring case.
/// The name also names the plugin's data folder and prefixes its settings and commands in the application.
/// </param>
/// <param name="Version">Version text of the plugin, for example <c>1.2.0</c>; may be empty, never <see langword="null"/>.</param>
/// <param name="License">License name of the plugin itself, for example <c>MIT</c>; may be empty, never <see langword="null"/>.</param>
/// <param name="Description">One or two sentences about what the plugin contributes; may be empty, never <see langword="null"/>.</param>
/// <param name="Dependencies">Dependencies the plugin ships privately with their licenses; empty when it has none.</param>
public sealed record PluginInfo(string Name, string Version, string License, string Description,
    IReadOnlyList<PluginDependency> Dependencies);
