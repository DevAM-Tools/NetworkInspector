// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>One source request handed to <see cref="SourceCreate"/>.</summary>
/// <param name="Paths">The file of this source; empty for kinds without files.</param>
/// <param name="Options">The applied option settings, registered by <see cref="SourceRegistration.RegisterOptions"/>.</param>
/// <param name="SessionSettings">Settings of the receiving session (CLI: the profile settings).</param>
public sealed record SourceRequest(IReadOnlyList<string> Paths, ReadOnlySettingsManagerView Options,
    ReadOnlySettingsManagerView SessionSettings);
