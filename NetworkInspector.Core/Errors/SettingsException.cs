// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Base class for all exceptions thrown by the settings system.
/// Covers registration errors, validation failures, and persistence problems.
/// </summary>
public abstract class SettingsException : Exception
{
    #region Constructors

    /// <summary>Creates a new settings exception with the specified message.</summary>
    protected SettingsException(string message) : base(message) { }

    /// <summary>Creates a new settings exception with an inner exception.</summary>
    protected SettingsException(string message, Exception innerException)
        : base(message, innerException) { }

    #endregion
}
