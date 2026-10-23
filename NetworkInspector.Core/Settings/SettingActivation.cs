// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// How a change of a <see cref="Setting"/> takes effect. The value is declared with the setting (the <c>activation</c>
/// parameter of the factory methods) and only informs the application: Core applies every change at once, and a host or an
/// app decides whether a session or the whole process has to be restarted before the new value is used.
/// Every setting of a session settings manager is effectively <see cref="SessionReset"/>; the host decides.
/// </summary>
public enum SettingActivation : byte
{
    /// <summary>The new value is used as soon as it is applied. This is the default.</summary>
    Immediate = 0,

    /// <summary>The new value is used by the next session (or after the current session is reset).</summary>
    SessionReset = 1,

    /// <summary>The new value is used after the application has been restarted (for example the plugin folders).</summary>
    Restart = 2,
}
