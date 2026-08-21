// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Analysis;

/// <summary>
/// Binds filter names to <see cref="IStack"/> metadata and caches the results for the lifetime
/// of one compilation.
/// <para>
/// <b>Name resolution order</b> is protocol, then canonical field, then alias group.
/// Protocols come first because nearly every protocol also registers a container field carrying
/// its own name (field <c>udp</c> of protocol <c>udp</c>). Binding such a name to the protocol
/// keeps <c>udp</c> meaning "this packet contains UDP", which can be answered from the presence
/// index in O(1), and keeps <c>$udp[i]</c> counting UDP layers rather than UDP fields.
/// </para>
/// <para>
/// The resolver also builds a field-to-protocol owner table once per stack. The table lets the
/// evaluator answer "does this subtree belong to protocol X" in O(1) while walking a packet,
/// which is what scope anchors and index-less protocol presence need.
/// </para>
/// </summary>
internal sealed class SymbolResolver
{
    #region Fields

    private readonly IStack _Stack;
    private readonly Dictionary<string, FilterSymbol?> _Cache;
    private readonly ProtocolId[] _FieldOwners;

    #endregion

    #region Construction

    /// <summary>Creates a resolver over a compile-time stack.</summary>
    public SymbolResolver(IStack stack)
    {
        _Stack = stack;
        _Cache = new Dictionary<string, FilterSymbol?>(StringComparer.Ordinal);

        ReadOnlySpan<FieldInfo> fields = stack.Fields.Span;
        _FieldOwners = new ProtocolId[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            _FieldOwners[i] = fields[i].ProtocolId;
        }
    }

    #endregion

    #region Properties

    /// <summary>
    /// Owning protocol per <see cref="FieldId"/>, indexed by <see cref="FieldId.Value"/>.
    /// Shared with the evaluator; never mutated after construction.
    /// </summary>
    public ProtocolId[] FieldOwners => _FieldOwners;

    #endregion

    #region Resolution

    /// <summary>
    /// Resolves a name to a field, alias group or protocol.
    /// Returns <see langword="null"/> when the name is unknown on this stack.
    /// </summary>
    public FilterSymbol? Resolve(string name)
    {
        if (_Cache.TryGetValue(name, out FilterSymbol? cached))
        {
            return cached;
        }

        FilterSymbol? symbol = _ResolveCore(name);
        _Cache[name] = symbol;
        return symbol;
    }

    /// <summary>
    /// Resolves a name that must produce values. Protocol names are rejected because a protocol
    /// has no scalar value to compare against.
    /// </summary>
    public FilterResult<FilterSymbol> ResolveValue(string name, int position, int length)
    {
        FilterSymbol? symbol = Resolve(name);
        if (symbol is null)
        {
            return FilterError.UnknownField(name, position, length);
        }

        if (!symbol.IsValueSource)
        {
            return FilterError.TypeMismatch(
                $"'{name}' is a protocol and has no value; compare one of its fields instead",
                position,
                length);
        }

        return symbol;
    }

    /// <summary>
    /// Ensures every canonical field of <paramref name="symbol"/> is a 64-bit integer, which
    /// is required for <c>by:</c> delta arithmetic. Mixed alias members fail the whole check.
    /// <para>
    /// <paramref name="symbol"/> member ids come from this stack, so <see cref="IStack.GetField"/>
    /// is expected to succeed; a missing record is treated as a type mismatch.
    /// </para>
    /// </summary>
    public FilterError? CheckIntegerFields(FilterSymbol symbol, string name, int position, int length)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        foreach (FieldId fieldId in symbol.Fields)
        {
            FieldInfo? info = _Stack.GetField(fieldId);
            FieldType fieldType = info?.FieldType ?? FieldType.None;
            if (fieldType is not FieldType.I64 and not FieldType.U64)
            {
                return FilterError.TypeMismatch(
                    $"'by:' requires an integer field, but '{name}' has type {fieldType}",
                    position,
                    length);
            }
        }

        return null;
    }

    /// <summary>Resolves a name used as a presence test or scope anchor.</summary>
    public FilterResult<FilterSymbol> ResolveAny(string name, int position, int length)
    {
        FilterSymbol? symbol = Resolve(name);
        if (symbol is null)
        {
            return FilterError.UnknownField(name, position, length);
        }
        return symbol;
    }

    #endregion

    #region Helpers

    private FilterSymbol? _ResolveCore(string name)
    {
        ProtocolId? protocolId = _Stack.GetProtocolId(name);
        if (protocolId is ProtocolId protocol)
        {
            return FilterSymbol.ForProtocol(name, protocol, _FindContainerField(name, protocol));
        }

        FieldId? fieldId = _Stack.GetFieldId(name);
        if (fieldId is FieldId field)
        {
            FieldInfo? info = _Stack.GetField(field);
            return FilterSymbol.ForField(
                name,
                info?.ProtocolId ?? ProtocolId.Invalid,
                field,
                _Stack.GetFieldIndexGroup(field));
        }

        FieldAliasGroupId? aliasId = _Stack.GetFieldAliasGroupId(name);
        if (aliasId is FieldAliasGroupId alias)
        {
            FieldAliasGroupInfo? info = _Stack.GetFieldAliasGroup(alias);
            if (info is not null)
            {
                return FilterSymbol.ForAlias(name, info.ProtocolId, info.Members.ToArray());
            }
        }

        return null;
    }

    /// <summary>
    /// Locates the field a protocol appends as its own subtree root. By convention that field
    /// carries the protocol's own name (for example field <c>udp</c> of protocol <c>udp</c>).
    /// Protocols that do not follow the convention yield <see cref="FieldId.Invalid"/>; the
    /// evaluator then falls back to an owner scan over the packet's fields.
    /// </summary>
    private FieldId _FindContainerField(string name, ProtocolId protocol)
    {
        FieldId? byName = _Stack.GetFieldId(name);
        if (byName is FieldId candidate)
        {
            FieldInfo? info = _Stack.GetField(candidate);
            if (info is not null && info.ProtocolId == protocol)
            {
                return candidate;
            }
        }

        return FieldId.Invalid;
    }

    #endregion
}
