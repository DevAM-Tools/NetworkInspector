// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// The shared settings group <c>plugins</c> of the loader's application settings, so that <see cref="PluginPathsSetting"/> and
/// <see cref="PluginTimeoutSettings"/> register it once whichever runs first. Stateless; the check and the registration are
/// two calls on the manager, so call it from one thread per manager (the loading thread).
/// </summary>
internal static class PluginSettingsGroup
{
    #region Internal API

    /// <summary>Registers the group when it is missing.</summary>
    /// <param name="applicationSettings">The application settings manager.</param>
    internal static void EnsureRegistered(SettingsManager applicationSettings)
    {
        // The group is registered explicitly so that it carries a UI name and a description. A second RegisterGroup call for the
        // same name throws (duplicate), hence the check first; the loader's settings and the CLI, host, and app all call this.
        if (applicationSettings.GetGroup(PluginPathsSetting.GroupName) is null)
        {
            applicationSettings.RegisterGroup(PluginPathsSetting.GroupName, "Plugins", "Plugin loading.");
        }
    }

    #endregion
}
