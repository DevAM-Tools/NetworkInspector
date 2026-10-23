// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Where a setting lives. <see cref="Application"/>: one value per installation (<c>&lt;base&gt;/application/</c>).
/// <see cref="Profile"/>: one value per profile (<c>&lt;base&gt;/&lt;profile&gt;/</c>). <see cref="Session"/>: one value
/// per open session (host only). This is the one scope enumeration of NetworkInspector, the host, the app, and the CLI,
/// so that every layer names the same three places.
/// </summary>
public enum SettingScope : byte
{
    /// <summary>Application-wide value, stored once per installation.</summary>
    Application = 0,

    /// <summary>Value of one profile; every profile has its own copy.</summary>
    Profile = 1,

    /// <summary>Value of one open session; only a host has sessions, so plugins cannot register session settings.</summary>
    Session = 2,
}
