// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// How a change of a <see cref="Setting"/> takes effect. The value is declared with the setting (the <c>activation</c>
/// parameter of the factory methods) and only informs the application: the manager applies every change at once, and the
/// application decides whether a session or the whole process has to be restarted before the new value is used.
/// Every setting of a manager that belongs to one session is effectively <see cref="SessionReset"/>; the application decides.
/// </summary>
public enum SettingActivation : byte
{
    /// <summary>The new value is used as soon as it is applied. This is the default.</summary>
    Immediate = 0,

    /// <summary>The new value is used by the next session (or after the current session is reset).</summary>
    SessionReset = 1,

    /// <summary>The new value is used after the application has been restarted (for example the folders an application reads components from).</summary>
    Restart = 2,
}
