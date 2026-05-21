// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
