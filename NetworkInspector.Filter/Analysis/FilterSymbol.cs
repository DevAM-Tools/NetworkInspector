// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

#region Kind

/// <summary>What a filter name resolved to on the compile-time stack.</summary>
internal enum FilterSymbolKind : byte
{
    /// <summary>A registered protocol such as <c>udp</c>.</summary>
    Protocol = 0,

    /// <summary>A canonical field such as <c>udp.srcport</c>.</summary>
    Field = 1,

    /// <summary>A field alias group such as <c>udp.port</c>, expanding to several canonical fields.</summary>
    Alias = 2,
}

#endregion

#region Symbol

/// <summary>
/// A filter name bound to stack metadata.
/// <para>
/// Alias groups are flattened to their member fields at compile time so the runtime only ever
/// deals with <see cref="FieldId"/> arrays; a canonical field produces a one-element array. The
/// arrays are shared by every emitted closure and must never be mutated.
/// </para>
/// </summary>
internal sealed class FilterSymbol
{
    #region Fields

    private static readonly FieldId[] _NoFields = [];

    #endregion

    #region Construction

    private FilterSymbol(
        FilterSymbolKind kind,
        string name,
        ProtocolId protocolId,
        FieldId containerField,
        FieldId[] fields,
        IndexGroupId indexGroup)
    {
        Kind = kind;
        Name = name;
        ProtocolId = protocolId;
        ContainerField = containerField;
        Fields = fields;
        IndexGroup = indexGroup;
    }

    /// <summary>Creates a protocol symbol.</summary>
    public static FilterSymbol ForProtocol(string name, ProtocolId protocolId, FieldId containerField) =>
        new(FilterSymbolKind.Protocol, name, protocolId, containerField, _NoFields, IndexGroupId.Invalid);

    /// <summary>Creates a canonical-field symbol.</summary>
    public static FilterSymbol ForField(string name, ProtocolId protocolId, FieldId fieldId, IndexGroupId indexGroup) =>
        new(FilterSymbolKind.Field, name, protocolId, FieldId.Invalid, [fieldId], indexGroup);

    /// <summary>Creates an alias-group symbol.</summary>
    public static FilterSymbol ForAlias(string name, ProtocolId protocolId, FieldId[] members) =>
        new(FilterSymbolKind.Alias, name, protocolId, FieldId.Invalid, members, IndexGroupId.Invalid);

    #endregion

    #region Properties

    /// <summary>What the name resolved to.</summary>
    public FilterSymbolKind Kind { get; }

    /// <summary>The name as written in the expression.</summary>
    public string Name { get; }

    /// <summary>Owning protocol.</summary>
    public ProtocolId ProtocolId { get; }

    /// <summary>
    /// For <see cref="FilterSymbolKind.Protocol"/>: the field the protocol appends as its own
    /// subtree root, or <see cref="FieldId.Invalid"/> when the protocol registers no such field.
    /// </summary>
    public FieldId ContainerField { get; }

    /// <summary>The canonical fields this symbol reads; empty for a protocol symbol.</summary>
    public FieldId[] Fields { get; }

    /// <summary>Index group of a canonical field, or <see cref="IndexGroupId.Invalid"/>.</summary>
    public IndexGroupId IndexGroup { get; }

    /// <summary>Whether this symbol produces values (field or alias) rather than only presence.</summary>
    public bool IsValueSource => Kind != FilterSymbolKind.Protocol;

    #endregion
}

#endregion
