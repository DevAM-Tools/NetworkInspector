// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when an ID or name refers to a resource
/// that does not exist (e.g., an unknown protocol table ID or name).
/// </summary>
/// <remarks>Creates a new not-found registration exception with the specified message.</remarks>
public sealed class NotFoundRegistrationException(string message) : RegistrationException(message)
{
    #region Factory Methods

    /// <summary>Creates a not-found exception with the specified message.</summary>
    internal static NotFoundRegistrationException For(string message) => new(message);

    #endregion
}
