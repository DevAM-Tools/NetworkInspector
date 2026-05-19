// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a settings persistence operation fails
/// (I/O error, JSON parsing error, or missing storage path).
/// </summary>
public sealed class PersistenceSettingsException : SettingsException
{
    #region Constructors

    private PersistenceSettingsException(string message) : base(message) { }

    private PersistenceSettingsException(string message, Exception innerException)
        : base(message, innerException) { }

    #endregion

    #region Factory Methods

    /// <summary>Creates an exception for an I/O failure during persistence.</summary>
    internal static PersistenceSettingsException ForIo(IOException innerException) =>
        new($"I/O error: {innerException.Message}", innerException);

    /// <summary>Creates an exception for a JSON parsing or serialization failure.</summary>
    internal static PersistenceSettingsException ForJson(JsonException innerException) =>
        new($"JSON error: {innerException.Message}", innerException);

    /// <summary>Creates an exception indicating no storage path is configured.</summary>
    internal static PersistenceSettingsException ForNoStoragePath() =>
        new("No storage path configured");

    #endregion
}