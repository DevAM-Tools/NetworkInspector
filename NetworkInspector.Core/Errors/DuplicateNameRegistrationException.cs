// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a name that has already been registered
/// is registered again (e.g., duplicate protocol name, field name, or table name).
/// </summary>
/// <remarks>Creates a new duplicate-name registration exception.</remarks>
public sealed class DuplicateNameRegistrationException(string name) : RegistrationException($"Duplicate name: '{name}'")
{
    #region Properties

    /// <summary>The name that was already registered.</summary>
    public string Name { get; } = name;

    #endregion

    #region Factory Methods

    /// <summary>Creates a duplicate-name exception for the given name.</summary>
    internal static DuplicateNameRegistrationException For(string name) => new(name);

    #endregion
}