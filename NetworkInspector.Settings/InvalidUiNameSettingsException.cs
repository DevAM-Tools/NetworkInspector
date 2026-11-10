// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Thrown when the text a user interface shows for a setting or a group is invalid: empty, or holding a control character
/// (a line break or a tab would break tables and one-line help).
/// </summary>
/// <param name="uiName">The rejected text; also reported in the message.</param>
/// <seealso cref="InvalidNameSettingsException"/>
public sealed class InvalidUiNameSettingsException(string uiName)
    : SettingsException(Invariant($"Invalid UI name: '{uiName}'. A UI name is non-empty text without control characters."))
{
    #region Rejected text

    /// <summary>The invalid user-interface name that caused the error.</summary>
    public string UiName { get; } = uiName;

    #endregion

    #region Creating the exception

    /// <summary>Creates an exception for an invalid user-interface name.</summary>
    /// <param name="uiName">The rejected text.</param>
    internal static InvalidUiNameSettingsException For(string uiName) => new(uiName);

    #endregion
}
