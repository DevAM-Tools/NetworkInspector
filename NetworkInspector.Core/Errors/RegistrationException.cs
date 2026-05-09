// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Base class for all exceptions thrown during the registration (build) phase.
/// Not used on the hot path — only during <see cref="StackBuilder"/> setup.
/// </summary>
public abstract class RegistrationException : Exception
{
    #region Constructors

    /// <summary>Creates a new registration exception with the specified message.</summary>
    protected RegistrationException(string message) : base(message) { }

    /// <summary>Creates a new registration exception with an inner exception.</summary>
    protected RegistrationException(string message, Exception innerException)
        : base(message, innerException) { }

    #endregion
}
