// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Thrown during the registration phase when a name that has already been registered
/// is registered again (e.g., duplicate protocol name, field name, or table name).
/// Names are the stable keys of the stack's lookup maps, so a second registration would either replace the first
/// silently or make lookups ambiguous; it is rejected at registration time instead.
/// Protocols, fields and field alias groups share one namespace for this rule; the exception then names the kind of the
/// holder in <see cref="HolderKind"/>.
/// </summary>
/// <seealso cref="RegistrationHolderKind"/>
public sealed class DuplicateNameRegistrationException : RegistrationException
{
    #region Constructors

    /// <summary>Creates the exception for a name that is registered twice within one kind of registration.</summary>
    /// <param name="name">The name that was registered twice; also reported in the message.</param>
    public DuplicateNameRegistrationException(string name)
        : base($"Duplicate name: '{name}'")
    {
        Name = name;
        HolderKind = RegistrationHolderKind.Unknown;
    }

    /// <summary>Creates the exception for a name that another registration already holds.</summary>
    /// <param name="name">The name that was refused; also reported in the message.</param>
    /// <param name="holderKind">The kind of the registration that holds the name; also reported in the message.</param>
    public DuplicateNameRegistrationException(string name, RegistrationHolderKind holderKind)
        : base($"Duplicate name: '{name}' is already registered as a {_Describe(holderKind)}.")
    {
        Name = name;
        HolderKind = holderKind;
    }

    #endregion

    #region Properties

    /// <summary>The name that was already registered.</summary>
    public string Name { get; }

    /// <summary>The kind of registration that holds the name; <see cref="RegistrationHolderKind.Unknown"/> when not reported.</summary>
    public RegistrationHolderKind HolderKind { get; }

    #endregion

    #region Factory Methods

    /// <summary>Creates a duplicate-name exception for the given name.</summary>
    /// <param name="name">The name that was registered twice.</param>
    internal static DuplicateNameRegistrationException For(string name) => new(name);

    /// <summary>Creates a duplicate-name exception that names the kind of the holder.</summary>
    /// <param name="name">The name that was refused.</param>
    /// <param name="holderKind">The kind of the registration that holds the name.</param>
    internal static DuplicateNameRegistrationException For(string name, RegistrationHolderKind holderKind) => new(name, holderKind);

    #endregion

    #region Message

    /// <summary>The noun the message uses for <paramref name="kind"/>; "registration" for an unknown or undefined kind.</summary>
    /// <param name="kind">The holder kind.</param>
    /// <returns>"protocol", "field", "field alias group", or "registration".</returns>
    private static string _Describe(RegistrationHolderKind kind) => kind switch
    {
        RegistrationHolderKind.Protocol => "protocol",
        RegistrationHolderKind.Field => "field",
        RegistrationHolderKind.FieldAliasGroup => "field alias group",
        _ => "registration",
    };

    #endregion
}
