// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Severity classification for a <see cref="ValueCacheParseWarning"/>.
/// </summary>
public enum ValueCacheParseWarningSeverity
{
    #region Enum Values

    /// <summary>
    /// Configuration is incomplete or refers to something the stack does not provide.
    /// Caching continues for the remaining valid entries.
    /// </summary>
    Warning,

    /// <summary>
    /// Configuration is malformed (e.g. unparsable storage mode or incompatible
    /// field/storage combination). The entry is rejected entirely.
    /// </summary>
    Error,

    #endregion
}
