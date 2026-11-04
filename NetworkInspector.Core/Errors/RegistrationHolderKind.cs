// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// The kind of registration that already holds a name, reported by <see cref="DuplicateNameRegistrationException.HolderKind"/>.
/// Protocols, fields and field alias groups share one conflict namespace in the stack builder.
/// </summary>
/// <seealso cref="DuplicateNameRegistrationException"/>
public enum RegistrationHolderKind
{
    /// <summary>Not reported: a duplicate within one kind of registration (protocol tables, settings).</summary>
    Unknown = 0,

    /// <summary>A protocol holds the name.</summary>
    Protocol = 1,

    /// <summary>A field holds the name.</summary>
    Field = 2,

    /// <summary>A field alias group holds the name.</summary>
    FieldAliasGroup = 3,
}
