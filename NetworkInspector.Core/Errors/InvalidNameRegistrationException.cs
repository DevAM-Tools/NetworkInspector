// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a name does not conform to the
/// required naming convention (C-style identifier segments separated by dots,
/// e.g. "ip.src", "tcp.flags.syn"). The name is validated when it is registered, so an invalid name fails at
/// setup time and never becomes a key of the stack's lookup maps.
/// </summary>
/// <param name="name">The rejected name; also reported in the message.</param>
public sealed class InvalidNameRegistrationException(string name)
    : RegistrationException(Invariant($"Invalid name: '{name}'. Names must be dot-separated C-style identifiers (e.g. \"ip.src\", \"tcp.flags.syn\")."))
{
    /// <summary>The invalid name that failed validation.</summary>
    public string Name { get; } = name;

    /// <summary>Creates an invalid-name exception for the given name.</summary>
    /// <param name="name">The rejected name.</param>
    /// <returns>The exception, ready to be thrown by the core.</returns>
    internal static InvalidNameRegistrationException For(string name) => new(name);
}
