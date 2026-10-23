// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>The identity of a plugin that is being configured, handed to <see cref="IPluginBuilderFactory.Create"/>.</summary>
/// <param name="Info">Validated plugin info.</param>
/// <param name="Tier">Tier of the entry type.</param>
/// <param name="Path">Assembly path; the application assembly for built-ins.</param>
/// <param name="BuiltIn"><see langword="true"/> for a built-in plugin.</param>
public sealed record PluginRecordDraft(PluginInfo Info, PluginTier Tier, string Path, bool BuiltIn);
