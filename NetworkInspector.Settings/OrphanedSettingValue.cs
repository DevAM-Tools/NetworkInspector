// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// A stored value for a setting that is not registered in this run, for example the value of a setting of a component while that
/// component is not loaded. The manager keeps such values and writes them back on save, so they are not lost; they apply as
/// soon as the setting is registered.
/// </summary>
/// <param name="GroupName">Group the value was stored in (the group file name; the default group has an empty name).</param>
/// <param name="Name">Name of the setting.</param>
/// <param name="Value">The stored JSON value (a clone; changing it does not change the manager).</param>
public sealed record OrphanedSettingValue(string GroupName, string Name, JsonElement Value);
