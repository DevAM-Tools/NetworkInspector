// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>The storage layout most settings tests use: the default bookkeeping file names and a list of reserved folders.</summary>
internal static class TestLayout
{
    /// <summary>
    /// An application named <c>TestApp</c> with the default lock and manifest file names and seven reserved folders (the
    /// profile tests need names to refuse and a folder list to skip).
    /// </summary>
    internal static SettingsStorageLayout Default { get; } = SettingsStorageLayout.Create(
        "TestApp",
        ["logs", "plugins", "plugins-data", "plugin-scan-cache", "host", "app", "presets"]);
}
