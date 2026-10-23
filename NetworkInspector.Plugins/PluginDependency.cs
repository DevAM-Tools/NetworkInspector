// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// A dependency the plugin ships privately next to its own assembly, with the license information the application lists
/// on its license page.
/// </summary>
/// <param name="Name">Package or library name.</param>
/// <param name="Version">Version text of the shipped dependency.</param>
/// <param name="License">License name, for example <c>MIT</c> or <c>Apache-2.0</c>.</param>
/// <param name="CopyrightHolder">Copyright holder named by the dependency.</param>
/// <param name="Link">Project or package address.</param>
/// <param name="LicenseText">Full license text; may be empty when the license name suffices.</param>
public sealed record PluginDependency(string Name, string Version, string License, string CopyrightHolder,
    string Link, string LicenseText);
