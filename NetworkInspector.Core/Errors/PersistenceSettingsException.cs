// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown when a settings persistence operation fails
/// (I/O error, access denied, JSON parsing error, missing or invalid storage path, or a settings folder locked by another
/// process).
/// The factory methods take the original exception and keep it as <see cref="Exception.InnerException"/>, so the
/// cause (and its stack trace) is not lost when the settings layer reports a uniform exception type.
/// </summary>
public sealed class PersistenceSettingsException : SettingsException
{
    #region Constructors

    /// <summary>Creates an exception without an inner exception (no underlying failure exists).</summary>
    /// <param name="message">Message text.</param>
    private PersistenceSettingsException(string message) : base(message) { }

    /// <summary>Creates an exception that wraps the failure that caused it.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="innerException">The original exception.</param>
    private PersistenceSettingsException(string message, Exception innerException)
        : base(message, innerException) { }

    #endregion

    #region Factory Methods

    /// <summary>Creates an exception for an I/O failure during persistence.</summary>
    /// <param name="innerException">The original I/O exception.</param>
    internal static PersistenceSettingsException ForIo(IOException innerException) =>
        new($"I/O error: {innerException.Message}", innerException);

    /// <summary>Creates an exception for a file-system access denial during persistence.</summary>
    /// <param name="innerException">The original access exception.</param>
    internal static PersistenceSettingsException ForAccessDenied(UnauthorizedAccessException innerException) =>
        new($"Access denied: {innerException.Message}", innerException);

    /// <summary>
    /// Creates an exception for a storage path the file system cannot use at all (empty, a null character, a form the platform
    /// does not support). The framework reports these with <see cref="ArgumentException"/> or <see cref="NotSupportedException"/>,
    /// which are caller-bug exceptions elsewhere; for a path that came from configuration they are an expected persistence failure.
    /// </summary>
    /// <param name="innerException">The original <see cref="ArgumentException"/> or <see cref="NotSupportedException"/>.</param>
    internal static PersistenceSettingsException ForInvalidPath(Exception innerException) =>
        new($"Invalid storage path: {innerException.Message}", innerException);

    /// <summary>Creates an exception for a JSON parsing or serialization failure.</summary>
    /// <param name="innerException">The original JSON exception.</param>
    internal static PersistenceSettingsException ForJson(JsonException innerException) =>
        new($"JSON error: {innerException.Message}", innerException);

    /// <summary>Creates an exception indicating no storage path is configured.</summary>
    internal static PersistenceSettingsException ForNoStoragePath() =>
        new("No storage path configured");

    /// <summary>
    /// Creates an exception indicating that another process (or another manager) holds the folder lock and did not release it
    /// within the retry budget.
    /// </summary>
    /// <param name="storagePath">The settings folder that could not be locked; named in the message.</param>
    internal static PersistenceSettingsException ForLocked(string storagePath) =>
        new($"The settings folder '{storagePath}' is in use by another process; try again.");

    #endregion
}
