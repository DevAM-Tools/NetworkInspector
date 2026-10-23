// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.BuiltInPlugins;

/// <summary>
/// Typed reads of option settings for the source and exporter registrations. Core already parsed and bounded every option
/// text when the settings were applied, so a missing setting is a programming error (the registration reads a name its own
/// <c>RegisterOptions</c> never registered) and throws instead of silently using a made-up default. Stateless; thread-safe.
/// </summary>
internal static class OptionValues
{
    #region Internal API

    /// <summary>Reads a signed integer option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The current value.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as a signed integer.</exception>
    internal static long GetI64(ReadOnlySettingsManagerView options, string name) =>
        options.GetI64Setting(name)
        ?? throw _NotRegistered(name, "signed integer");

    /// <summary>Reads an unsigned integer option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The current value.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as an unsigned integer.</exception>
    internal static ulong GetU64(ReadOnlySettingsManagerView options, string name) =>
        options.GetU64Setting(name)
        ?? throw _NotRegistered(name, "unsigned integer");

    /// <summary>Reads a floating-point option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The current value.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as a floating-point number.</exception>
    internal static double GetF64(ReadOnlySettingsManagerView options, string name) =>
        options.GetF64Setting(name)
        ?? throw _NotRegistered(name, "floating-point");

    /// <summary>Reads a boolean option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The current value.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as a boolean.</exception>
    internal static bool GetBool(ReadOnlySettingsManagerView options, string name) =>
        options.GetBoolSetting(name)
        ?? throw _NotRegistered(name, "boolean");

    /// <summary>Reads a string option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The current value, possibly empty.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as a string.</exception>
    internal static string GetString(ReadOnlySettingsManagerView options, string name) =>
        options.GetStringSetting(name)
        ?? throw _NotRegistered(name, "string");

    /// <summary>Reads the registered name of the current value of an enumeration option.</summary>
    /// <param name="options">The applied option settings of the request.</param>
    /// <param name="name">Option name as registered.</param>
    /// <returns>The name of the selected value, as registered in the option's metadata.</returns>
    /// <exception cref="InvalidOperationException">The option is not registered as an enumeration.</exception>
    internal static string GetEnumName(ReadOnlySettingsManagerView options, string name) =>
        options.GetEnumSetting(name)?.Name
        ?? throw _NotRegistered(name, "enumeration");

    #endregion

    #region Private Helpers

    /// <summary>Creates the exception for an option that the registration did not register with the expected type.</summary>
    private static InvalidOperationException _NotRegistered(string name, string type) =>
        new(Invariant($"The option '{name}' is not registered as a {type} setting."));

    #endregion
}
