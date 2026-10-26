// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Base class for all exceptions thrown during the registration (build) phase.
/// Not used on the hot path — only during <see cref="StackBuilder"/> setup.
/// Registration mistakes (duplicate or invalid names, unknown ids) are programming errors of the setup code that the
/// caller cannot repair locally, which is why they are exceptions rather than result values. The subclasses are sealed
/// and created through internal factories.
/// </summary>
public abstract class RegistrationException : Exception
{
    #region Constructors

    /// <summary>Creates a new registration exception with the specified message.</summary>
    /// <param name="message">Message text; must not contain secrets.</param>
    protected RegistrationException(string message) : base(message) { }

    /// <summary>Creates a new registration exception with an inner exception.</summary>
    /// <param name="message">Message text; must not contain secrets.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    protected RegistrationException(string message, Exception innerException)
        : base(message, innerException) { }

    #endregion
}
