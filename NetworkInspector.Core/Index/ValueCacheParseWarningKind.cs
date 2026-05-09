// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Categorizes the reason an entry was skipped by
/// <see cref="PacketIndex.ParseValueCacheSettingValue(string?, Stack, out System.Collections.Generic.IReadOnlyList{ValueCacheParseWarning})"/>.
/// </summary>
public enum ValueCacheParseWarningKind
{
    #region Enum Values

    /// <summary>
    /// The entry or the field name was empty after trimming.
    /// </summary>
    EmptyEntry,

    /// <summary>
    /// The storage mode string following the colon is not a recognized mode identifier.
    /// </summary>
    InvalidStorageMode,

    /// <summary>
    /// The field name is not registered in the stack.
    /// </summary>
    UnknownField,

    /// <summary>
    /// The field type cannot be value-cached (e.g. container or structural fields).
    /// </summary>
    UncacheableFieldType,

    /// <summary>
    /// The specified storage mode is incompatible with the field's value type.
    /// </summary>
    IncompatibleStorageMode,

    #endregion
}
