// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>One named contribution of a plugin, for the plugin list and the plugin page.</summary>
/// <param name="Name">Name of the contribution (protocol name, source kind, setting name, …).</param>
/// <param name="Description">Short description; never <see langword="null"/>.</param>
public sealed record ContributionEntry(string Name, string Description);
