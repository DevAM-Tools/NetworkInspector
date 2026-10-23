// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>A plugin contribution that threw while it was applied to a real stack or settings manager.</summary>
/// <param name="PluginName">Plugin that owns the contribution.</param>
/// <param name="Message">One-line description: exception type and message.</param>
/// <param name="Exception">The exception, for logs.</param>
public sealed record PluginContributionFailure(string PluginName, string Message, Exception Exception);
