// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Type discriminant for setting values.
/// </summary>
public enum SettingType : byte
{
    #region Enum Values

    /// <summary>Boolean value.</summary>
    Bool = 0,

    /// <summary>String value.</summary>
    String = 1,

    /// <summary>64-bit floating-point value.</summary>
    F64 = 2,

    /// <summary>Unsigned 64-bit integer value.</summary>
    U64 = 3,

    /// <summary>Signed 64-bit integer value.</summary>
    I64 = 4,

    /// <summary>Raw byte array value.</summary>
    Bytes = 5,

    /// <summary>Named enum value with a numeric representation.</summary>
    Enum = 6,

    #endregion
}
