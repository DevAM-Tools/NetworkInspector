// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Fields;

/// <summary>Discriminant for the data type stored in a field value.</summary>
public enum FieldType : byte
{
    #region Enum Values

    /// <summary>No value (container/grouping field).</summary>
    None = 0,
    /// <summary>Signed 64-bit integer.</summary>
    I64 = 1,
    /// <summary>Unsigned 64-bit integer.</summary>
    U64 = 2,
    /// <summary>64-bit floating point.</summary>
    F64 = 3,
    /// <summary>String value.</summary>
    String = 4,
    /// <summary>Raw byte sequence.</summary>
    Bytes = 5,
    /// <summary>48-bit MAC address.</summary>
    MacAddress = 6,
    /// <summary>32-bit IPv4 address.</summary>
    IPv4Address = 7,
    /// <summary>128-bit IPv6 address.</summary>
    IPv6Address = 8,
    /// <summary>64-bit Extended Unique Identifier.</summary>
    Eui64 = 9,
    /// <summary>128-bit UUID.</summary>
    Uuid = 10,
    /// <summary>Nanosecond-precision timestamp.</summary>
    Timestamp = 11,
    /// <summary>Boolean value.</summary>
    Bool = 12,

    #endregion
}
