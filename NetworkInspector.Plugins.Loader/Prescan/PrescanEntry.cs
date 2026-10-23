// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>One entry type found in metadata.</summary>
/// <param name="TypeName">Full name; nested types are joined with <c>+</c>.</param>
/// <param name="Tier">Tier of the most derived plugin interface the type lists.</param>
/// <param name="Problem">A problem decided from metadata alone (not public, generic), or <see langword="null"/>.</param>
internal sealed record PrescanEntry(string TypeName, PluginTier Tier, string? Problem);
