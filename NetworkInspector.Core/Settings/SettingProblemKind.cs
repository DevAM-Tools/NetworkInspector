// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>Why a setting value is refused; see <see cref="SettingProblem"/>.</summary>
public enum SettingProblemKind : byte
{
    /// <summary>The text or JSON has the wrong shape for the setting type, or the value has another type.</summary>
    WrongType,

    /// <summary>A number is below the minimum of the setting (or of its type).</summary>
    BelowMinimum,

    /// <summary>A number is above the maximum of the setting (or of its type).</summary>
    AboveMaximum,

    /// <summary>A floating-point number is NaN or infinite.</summary>
    NotFinite,

    /// <summary>The enumeration name or number is not one of the allowed values.</summary>
    UnknownEnum,

    /// <summary>A path setting names an invalid path. Reserved: no member of the library produces it yet (settings carry no path metadata).</summary>
    InvalidPath,

    /// <summary>No setting with the given name is registered. Reserved for lookups: no member of the library produces it yet.</summary>
    UnknownSetting,
}
