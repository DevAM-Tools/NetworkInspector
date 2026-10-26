// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a name that has already been registered
/// is registered again (e.g., duplicate protocol name, field name, or table name).
/// Names are the stable keys of the stack's lookup maps, so a second registration would either replace the first
/// silently or make lookups ambiguous; it is rejected at registration time instead.
/// </summary>
/// <param name="name">The name that was registered twice; also reported in the message.</param>
public sealed class DuplicateNameRegistrationException(string name) : RegistrationException($"Duplicate name: '{name}'")
{
    #region Properties

    /// <summary>The name that was already registered.</summary>
    public string Name { get; } = name;

    #endregion

    #region Factory Methods

    /// <summary>Creates a duplicate-name exception for the given name.</summary>
    /// <param name="name">The name that was registered twice.</param>
    internal static DuplicateNameRegistrationException For(string name) => new(name);

    #endregion
}
