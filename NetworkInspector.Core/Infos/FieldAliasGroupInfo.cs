// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered field alias group.
/// <para>
/// A field alias group exposes an "any-match" name (e.g., <c>"eth.addr"</c>) that resolves to
/// a set of canonical member fields (e.g., <c>{ eth.dst, eth.src }</c>) without duplicating
/// those fields in the parse tree. Alias groups are metadata-only: the parsing hot path does
/// not consult them, and <see cref="IStack.GetFieldId(string)"/> never returns an
/// ID for an alias name.
/// </para>
/// <para>
/// Member fields may carry different <see cref="FieldType"/> values. The alias name
/// itself has no field type; consumers that need a uniform value type must inspect each member
/// individually via <see cref="Members"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> immutable after construction. The owning <see cref="Stack"/> publishes
/// the instance once during <see cref="StackBuilder.Build"/> and never mutates it afterwards;
/// concurrent reads are therefore safe without synchronization.
/// </para>
/// </summary>
public sealed class FieldAliasGroupInfo
{
    #region Fields

    private readonly FieldId[] _Members;

    #endregion

    #region Constructors

    /// <summary>Creates field alias group metadata during stack registration.</summary>
    /// <param name="id">The unique identifier assigned by the builder.</param>
    /// <param name="protocolId">The protocol that owns this alias group.</param>
    /// <param name="name">The alias name (machine-readable, dot-separated identifier).</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="members">
    /// The canonical member field IDs in registration order. The array is taken by reference;
    /// the caller must not mutate it after construction.
    /// </param>
    internal FieldAliasGroupInfo(
        FieldAliasGroupId id,
        ProtocolId protocolId,
        string name,
        string? description,
        FieldId[] members)
    {
        Id = id;
        ProtocolId = protocolId;
        Name = name;
        Description = description;
        _Members = members;
    }

    #endregion

    #region Properties

    /// <summary>Unique alias group identifier.</summary>
    public FieldAliasGroupId Id
    {
        get;
    }

    /// <summary>Protocol that owns this alias group.</summary>
    public ProtocolId ProtocolId
    {
        get;
    }

    /// <summary>Machine-readable alias name (e.g., "eth.addr").</summary>
    public string Name
    {
        get;
    }

    /// <summary>Optional description text.</summary>
    public string? Description
    {
        get;
    }

    /// <summary>
    /// Canonical member field IDs in the order in which they were supplied at registration.
    /// Members may have heterogeneous <see cref="FieldType"/> values.
    /// </summary>
    public ReadOnlyMemory<FieldId> Members => _Members;

    /// <summary>Number of canonical member fields in this alias group.</summary>
    public int MemberCount => _Members.Length;

    #endregion
}
