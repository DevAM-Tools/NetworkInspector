// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a name does not conform to the
/// required naming convention (C-style identifier segments separated by dots,
/// e.g. "ip.src", "tcp.flags.syn").
/// </summary>
/// <remarks>Creates a new invalid-name registration exception.</remarks>
public sealed class InvalidNameRegistrationException(string name)
    : RegistrationException($"Invalid name: '{name}'. Names must be dot-separated C-style identifiers (e.g. \"ip.src\", \"tcp.flags.syn\").")
{
    #region Properties

    /// <summary>The invalid name that failed validation.</summary>
    public string Name { get; } = name;

    #endregion

    #region Factory Methods

    /// <summary>Creates an invalid-name exception for the given name.</summary>
    internal static InvalidNameRegistrationException For(string name) => new(name);

    #endregion
}
