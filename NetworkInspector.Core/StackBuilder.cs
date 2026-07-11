// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Accumulates protocol, field, table, and setting registrations.
/// Call <see cref="Build"/> to freeze into an immutable <see cref="Stack"/>.
/// <para>
/// <b>Thread-safety:</b> Not thread-safe. All registration methods mutate plain
/// <see cref="List{T}"/> / <see cref="Dictionary{TKey,TValue}"/> state without locking.
/// All registrations and the final <see cref="Build"/> call must happen on a single thread
/// (or under external mutual exclusion). The resulting <see cref="Stack"/> is, in contrast,
/// fully concurrent for read-only use.
/// </para>
/// </summary>
public sealed class StackBuilder : IStackBuilder
{
    // Registration storage
    private readonly List<ProtocolInfo> _Protocols = [];
    private readonly List<IProtocol> _ProtocolInstances = [];
    private readonly Dictionary<string, ProtocolId> _ProtocolNameMap = new(StringComparer.Ordinal);

    private readonly List<FieldInfo> _Fields = [];
    private readonly Dictionary<string, FieldId> _FieldNameMap = new(StringComparer.Ordinal);

    // Field alias groups (independent namespace; never resolves through _FieldNameMap)
    private readonly List<FieldAliasGroupInfo> _FieldAliasGroups = [];
    private readonly Dictionary<string, FieldAliasGroupId> _FieldAliasGroupNameMap = new(StringComparer.Ordinal);
    // Snapshot cache for FieldAliasGroups property; invalidated on each RegisterFieldAliasGroup call.
    private FieldAliasGroupInfo[]? _FieldAliasGroupsSnapshot;

    private readonly List<ProtocolTable> _ProtocolTables = [];
    private readonly List<ProtocolTableInfo> _ProtocolTableInfos = [];
    private readonly Dictionary<string, ProtocolTableId> _ProtocolTableNameMap = new(StringComparer.Ordinal);

    private readonly List<HeuristicProtocolTable> _HeuristicTables = [];
    private readonly List<HeuristicProtocolTableInfo> _HeuristicTableInfos = [];
    private readonly Dictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap = new(StringComparer.Ordinal);

    private readonly List<PostParserInfo> _PostParsers = [];
    private readonly SettingsManager _SettingsManager;

    // Stream reassembly configs (keyed by protocol ID)
    private readonly Dictionary<ProtocolId, StreamReassemblyConfig> _ReassemblyConfigs = [];

    // Index groups
    private readonly Dictionary<string, IndexGroupId> _IndexGroupMap = new(StringComparer.Ordinal);
    private int _NextIndexGroupId;

    // Frame interface registry (shared with Stack after Build)
    private readonly FrameInterfaceRegistry _FrameInterfaceRegistry;

    // Deferred callbacks
    private readonly Dictionary<string, List<Action<ProtocolId>>> _DeferredProtocol = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<FieldId>>> _DeferredField = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<ProtocolTableId>>> _DeferredTable = new(StringComparer.Ordinal);

    // Built-in field IDs
    private readonly FieldId _RootFieldId = FieldId.Invalid;
    private readonly FieldId _PacketErrorFieldId = FieldId.Invalid;
    private readonly FieldId _PacketChoiceFieldId = FieldId.Invalid;
    private readonly ProtocolId _PacketProtocolId = ProtocolId.Invalid;

    /// <summary>
    /// Protocol name used to auto-discover the frame protocol during <see cref="Build"/>.
    /// Must match <c>FrameProtocol.ProtocolName</c> from <c>NetworkInspector.Protocols</c>.
    /// </summary>
    private const string _FrameProtocolName = "frame";

    /// <inheritdoc/>
    /// <remarks>
    /// Use object initializer syntax (<c>new StackBuilder(sm, reg) { IncludeExceptionStackTrace = true }</c>)
    /// to set this flag. The value is captured by <see cref="Build"/>; mutating it after build is impossible
    /// because the property is <see langword="init"/>-only.
    /// </remarks>
    public bool IncludeExceptionStackTrace { get; init; }

    /// <summary>Creates a new stack builder with externally provided dependencies and registers built-in root and error fields.</summary>
    /// <param name="settingsManager">The settings manager instance for managing protocol settings.</param>
    /// <param name="frameInterfaceRegistry">The frame interface registry for managing capture interfaces.</param>
    public StackBuilder(SettingsManager settingsManager, FrameInterfaceRegistry frameInterfaceRegistry)
    {
        _SettingsManager = settingsManager;
        _FrameInterfaceRegistry = frameInterfaceRegistry;

        // Register built-in protocols:
        // RootProtocol is an empty dummy that owns the root field.
        // PacketProtocol is the top-level parse entry point (appends packet metadata, dispatches to frame).
        RootProtocol rootProtocol = new();
        ProtocolId rootProtocolId = RegisterProtocol(
            rootProtocol,
            static (builder, id, _) => RootProtocol.RegisterWith(builder, id));

        _PacketProtocolId = RegisterProtocol<PacketProtocol>(
            new(),
            static (builder, id, proto) => proto.RegisterWith(builder, id));

        // Root field is owned by RootProtocol (the dummy exists so that root has an owning protocol).
        _RootFieldId = RegisterField(rootProtocolId, "root", "Root", FieldType.None);

        // Error and choice fields are owned by PacketProtocol.
        _PacketErrorFieldId = RegisterField(_PacketProtocolId, "packet.error", "Error", FieldType.String);
        _PacketChoiceFieldId = RegisterField(
            _PacketProtocolId, "packet.choice", "Choice", FieldType.String,
            "Groups alternative parse results from ambiguous protocol dispatch");
    }

    #region Protocol Access

    /// <inheritdoc/>
    public ProtocolInfo? GetProtocol(ProtocolId id)
    {
        if (_IsValidIndex(id.Value, _Protocols.Count))
        {
            return _Protocols[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    public ProtocolId? GetProtocolId(string name)
    {
        if (_ProtocolNameMap.TryGetValue(name, out ProtocolId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolInfo> Protocols => _Protocols.ToArray();

    /// <inheritdoc/>
    public int ProtocolCount => _Protocols.Count;

    #endregion

    #region Field Access

    /// <inheritdoc/>
    public FieldInfo? GetField(FieldId id)
    {
        if (_IsValidIndex(id.Value, _Fields.Count))
        {
            return _Fields[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    public FieldId? GetFieldId(string name)
    {
        if (_FieldNameMap.TryGetValue(name, out FieldId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldInfo> Fields => _Fields.ToArray();

    /// <inheritdoc/>
    public int FieldCount => _Fields.Count;

    /// <inheritdoc/>
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId)
    {
        if (_IsValidIndex(fieldId.Value, _Fields.Count))
        {
            if (_Fields[fieldId.Value].IndexGroup is { } indexGroup)
            {
                return indexGroup;
            }
        }
        return IndexGroupId.Invalid;
    }

    #endregion

    #region Field Alias Group Access

    /// <inheritdoc/>
    public FieldAliasGroupInfo? GetFieldAliasGroup(FieldAliasGroupId id)
    {
        if (_IsValidIndex(id.Value, _FieldAliasGroups.Count))
        {
            return _FieldAliasGroups[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    public FieldAliasGroupId? GetFieldAliasGroupId(string name)
    {
        if (_FieldAliasGroupNameMap.TryGetValue(name, out FieldAliasGroupId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldAliasGroupInfo> FieldAliasGroups
    {
        get
        {
            _FieldAliasGroupsSnapshot ??= [.. _FieldAliasGroups];
            return _FieldAliasGroupsSnapshot;
        }
    }

    /// <inheritdoc/>
    public int FieldAliasGroupCount => _FieldAliasGroups.Count;

    #endregion

    #region Index Group Access

    /// <inheritdoc/>
    public IndexGroupInfo? GetIndexGroup(IndexGroupId id)
    {
        if (!_IsValidIndex(id.Value, _NextIndexGroupId))
        {
            return null;
        }
        // Linear scan — only used during build phase, not performance-critical
        foreach (KeyValuePair<string, IndexGroupId> kvp in _IndexGroupMap)
        {
            if (kvp.Value == id)
            {
                return new IndexGroupInfo(kvp.Value, kvp.Key);
            }
        }
        return null;
    }

    /// <inheritdoc/>
    public IndexGroupId? GetIndexGroupId(string name)
    {
        if (_IndexGroupMap.TryGetValue(name, out IndexGroupId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<IndexGroupInfo> IndexGroups
    {
        get
        {
            // Build a snapshot from the current map
            IndexGroupInfo[] infos = new IndexGroupInfo[_IndexGroupMap.Count];
            int i = 0;
            foreach (KeyValuePair<string, IndexGroupId> kvp in _IndexGroupMap)
            {
                infos[i++] = new IndexGroupInfo(kvp.Value, kvp.Key);
            }
            return infos;
        }
    }

    /// <inheritdoc/>
    public int IndexGroupCount => _NextIndexGroupId;

    #endregion

    #region Protocol Table Access

    /// <inheritdoc/>
    public ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, _ProtocolTableInfos.Count))
        {
            return _ProtocolTableInfos[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    public ProtocolTableId? GetProtocolTableId(string name)
    {
        if (_ProtocolTableNameMap.TryGetValue(name, out ProtocolTableId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos => _ProtocolTableInfos.ToArray();

    /// <inheritdoc/>
    public int ProtocolTableCount => _ProtocolTableInfos.Count;

    #endregion

    #region Post-Parser Access

    /// <inheritdoc/>
    /// <remarks>Returns post-parsers in the same deterministic sort order as <see cref="IStack.PostParsers"/>:
    /// ascending by <see cref="PostParserInfo.Priority"/>, then ascending by <see cref="PostParserInfo.Id"/>
    /// (registration order) as a stable tie-breaker. The list is re-sorted after every
    /// <see cref="RegisterPostParser"/> call so the order is always up to date.</remarks>
    public ReadOnlyMemory<PostParserInfo> PostParsers => _PostParsers.ToArray();

    /// <inheritdoc/>
    public int PostParserCount => _PostParsers.Count;

    #endregion

    #region Heuristic Table Access

    /// <inheritdoc/>
    public HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, _HeuristicTableInfos.Count))
        {
            return _HeuristicTableInfos[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    public HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name)
    {
        if (_HeuristicTableNameMap.TryGetValue(name, out HeuristicProtocolTableId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos => _HeuristicTableInfos.ToArray();

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => _HeuristicTableInfos.Count;

    #endregion

    #region Settings Access

    /// <inheritdoc/>
    public IReadOnlySettingsManager Settings => _SettingsManager.ReadOnly;

    /// <inheritdoc/>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => ReadOnlyMemory<BuildDiagnostic>.Empty;

    #endregion

    #region IStackBuilder Implementation

    #endregion

    #region Validation Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    /// <summary>Throws <see cref="InvalidNameRegistrationException"/> when the name is not a valid dot-separated C-style identifier.</summary>
    private static void _ValidateName(string name)
    {
        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameRegistrationException.For(name);
        }
    }

    /// <summary>Throws <see cref="InvalidUiNameRegistrationException"/> when the UI name is empty or contains control characters.</summary>
    private static void _ValidateUiName(string uiName)
    {
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw InvalidUiNameRegistrationException.For(uiName);
        }
    }

    #endregion

    #region Protocol Registration

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol(IProtocol protocol)
    {
        string name = protocol.Name;
        _ValidateName(name);
        _ValidateUiName(protocol.UiName);
        if (_ProtocolNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        ProtocolId id = new(_Protocols.Count);
        ProtocolInfo info = new(id, name, protocol.UiName, protocol.Description);
        _Protocols.Add(info);
        _ProtocolInstances.Add(protocol);
        _ProtocolNameMap[name] = id;

        // Fire deferred callbacks
        if (_DeferredProtocol.Remove(name, out List<Action<ProtocolId>>? callbacks))
        {
            foreach (Action<ProtocolId> cb in callbacks)
            {
                cb(id);
            }
        }

        return id;
    }

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol<TProtocol>(
        TProtocol protocol,
        Action<IStackBuilder, ProtocolId, TProtocol> callback)
        where TProtocol : IProtocol
    {
        ProtocolId id = RegisterProtocol(protocol);
        callback(this, id, protocol);
        return id;
    }

    #endregion

    #region Field Registration

    /// <inheritdoc/>
    public FieldId RegisterField(
        ProtocolId protocolId, string name, string uiName, FieldType fieldType, string? description = null)
    {
        _ValidateName(name);
        _ValidateUiName(uiName);
        if (_FieldNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        FieldId id = new(_Fields.Count);
        FieldInfo info = new(id, protocolId, name, uiName, fieldType, description, null);
        _Fields.Add(info);
        _FieldNameMap[name] = id;

        if (_DeferredField.Remove(name, out List<Action<FieldId>>? callbacks))
        {
            foreach (Action<FieldId> cb in callbacks)
            {
                cb(id);
            }
        }

        return id;
    }

    #endregion

    #region Field Alias Group Registration

    /// <inheritdoc/>
    public FieldAliasGroupId RegisterFieldAliasGroup(
        ProtocolId protocolId, string name, string? description, FieldId[] fieldIds)
    {
        // Validate alias name; alias namespace is independent from field/table namespaces
        // so the only collision check at this point is against existing alias names.
        _ValidateName(name);
        if (_FieldAliasGroupNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        ArgumentNullException.ThrowIfNull(fieldIds);
        if (fieldIds.Length == 0)
        {
            throw new ArgumentException(
                $"Field alias group '{name}' requires at least one member field ID.",
                nameof(fieldIds));
        }

        // Defensive copy: callers must not influence stored membership after registration.
        FieldId[] members = new FieldId[fieldIds.Length];
        for (int i = 0; i < fieldIds.Length; i++)
        {
            FieldId memberId = fieldIds[i];

            // Member field must be a registered field on this builder.
            if (!_IsValidIndex(memberId.Value, _Fields.Count))
            {
                throw NotFoundRegistrationException.For(
                    $"Field alias group '{name}' references unknown field ID {memberId.Value}");
            }

            // Member field must belong to the protocol that owns this alias group.
            if (_Fields[memberId.Value].ProtocolId != protocolId)
            {
                throw new ArgumentException(
                    $"Field alias group '{name}' references field '{_Fields[memberId.Value].Name}' (ID {memberId.Value}) which belongs to protocol '{_Fields[memberId.Value].ProtocolId.Value}', not the owning protocol '{protocolId.Value}'.",
                    nameof(fieldIds));
            }

            // Duplicate member IDs are rejected; mixed FieldType values are intentionally allowed.
            for (int j = 0; j < i; j++)
            {
                if (members[j] == memberId)
                {
                    throw new ArgumentException(
                        $"Field alias group '{name}' contains duplicate member field ID {memberId.Value}.",
                        nameof(fieldIds));
                }
            }

            members[i] = memberId;
        }

        FieldAliasGroupId id = new(_FieldAliasGroups.Count);
        FieldAliasGroupInfo info = new(id, protocolId, name, description, members);
        _FieldAliasGroups.Add(info);
        _FieldAliasGroupNameMap[name] = id;
        _FieldAliasGroupsSnapshot = null; // Invalidate snapshot cache
        return id;
    }

    #endregion

    #region RegisterFieldInGroup Implementation

    /// <inheritdoc/>
    public FieldId RegisterFieldInGroup(
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string indexGroup,
        string? description = null)
    {
        // name and uiName are validated inside RegisterField
        _ValidateName(indexGroup);
        FieldId id = RegisterField(protocolId, name, uiName, fieldType, description);

        // Resolve or create index group
        if (!_IndexGroupMap.TryGetValue(indexGroup, out IndexGroupId groupId))
        {
            groupId = new IndexGroupId(_NextIndexGroupId++);
            _IndexGroupMap[indexGroup] = groupId;
        }
        _Fields[id.Value].IndexGroup = groupId;

        return id;
    }

    #endregion

    #region Index Group Registration

    /// <inheritdoc/>
    public IndexGroupId GetOrCreateIndexGroup(string name)
    {
        _ValidateName(name);
        if (!_IndexGroupMap.TryGetValue(name, out IndexGroupId groupId))
        {
            groupId = new IndexGroupId(_NextIndexGroupId++);
            _IndexGroupMap[name] = groupId;
        }
        return groupId;
    }

    #endregion

    #region Protocol Table Registration

    /// <inheritdoc/>
    public ProtocolTableId RegisterProtocolTable(
        string name, string uiName, ProtocolTableKeyType keyType, string? description = null)
    {
        _ValidateName(name);
        _ValidateUiName(uiName);
        if (_ProtocolTableNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        ProtocolTableId id = new(_ProtocolTableInfos.Count);
        ProtocolTableInfo info = new(id, name, uiName, keyType, description);
        _ProtocolTableInfos.Add(info);
        _ProtocolTables.Add(new ProtocolTable(info));
        _ProtocolTableNameMap[name] = id;

        if (_DeferredTable.Remove(name, out List<Action<ProtocolTableId>>? callbacks))
        {
            foreach (Action<ProtocolTableId> cb in callbacks)
            {
                cb(id);
            }
        }

        return id;
    }

    /// <inheritdoc/>
    public void RegisterParserInU64Table(ProtocolTableId tableId, ulong key, ProtocolId protocolId)
    {
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For($"Protocol table ID {tableId.Value} not found");
        }
        _ProtocolTables[tableId.Value].RegisterU64(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInU64TableByName(string tableName, ulong key, ProtocolId protocolId)
    {
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For($"Protocol table '{tableName}' not found");
        }
        RegisterParserInU64Table(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInStringTable(ProtocolTableId tableId, string key, ProtocolId protocolId)
    {
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For($"Protocol table ID {tableId.Value} not found");
        }
        _ProtocolTables[tableId.Value].RegisterString(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInStringTableByName(string tableName, string key, ProtocolId protocolId)
    {
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For($"Protocol table '{tableName}' not found");
        }
        RegisterParserInStringTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBytesTable(ProtocolTableId tableId, BytesKey key, ProtocolId protocolId)
    {
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For($"Protocol table ID {tableId.Value} not found");
        }
        _ProtocolTables[tableId.Value].RegisterBytes(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBytesTableByName(string tableName, BytesKey key, ProtocolId protocolId)
    {
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For($"Protocol table '{tableName}' not found");
        }
        RegisterParserInBytesTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBoolTable(ProtocolTableId tableId, bool key, ProtocolId protocolId)
    {
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For($"Protocol table ID {tableId.Value} not found");
        }
        _ProtocolTables[tableId.Value].RegisterBool(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBoolTableByName(string tableName, bool key, ProtocolId protocolId)
    {
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For($"Protocol table '{tableName}' not found");
        }
        RegisterParserInBoolTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInAnyTable(ProtocolTableId tableId, ProtocolId protocolId)
    {
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For($"Protocol table ID {tableId.Value} not found");
        }
        _ProtocolTables[tableId.Value].RegisterAny(protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInAnyTableByName(string tableName, ProtocolId protocolId)
    {
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For($"Protocol table '{tableName}' not found");
        }
        RegisterParserInAnyTable(id, protocolId);
    }

    #endregion

    #region Post-Parser Registration

    /// <inheritdoc/>
    public PostParserId RegisterPostParser(
        ProtocolId protocolId, int priority = 0, string? description = null)
    {
        PostParserId id = new(_PostParsers.Count);
        PostParserInfo info = new(id, priority, protocolId, description);
        _PostParsers.Add(info);
        // Re-sort after every registration to keep the list in the order defined by
        // IStack.PostParsers (Priority asc, then Id asc as tie-breaker). Registration
        // lists are small so the O(n log n) cost per call is negligible.
        _PostParsers.Sort(static (a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Id.Value.CompareTo(b.Id.Value);
        });
        return id;
    }

    #endregion

    #region Heuristic Table Registration

    /// <inheritdoc/>
    public HeuristicProtocolTableId RegisterHeuristicProtocolTable(
        ProtocolId owningProtocolId, string name, string uiName, string? description = null)
    {
        _ValidateName(name);
        _ValidateUiName(uiName);
        if (_HeuristicTableNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        HeuristicProtocolTableId id = new(_HeuristicTableInfos.Count);
        HeuristicProtocolTableInfo info = new(id, name, uiName, description, owningProtocolId);
        _HeuristicTableInfos.Add(info);
        _HeuristicTables.Add(new HeuristicProtocolTable(info));
        _HeuristicTableNameMap[name] = id;

        return id;
    }

    /// <inheritdoc/>
    public void RegisterHeuristicParser(HeuristicProtocolTableId tableId, IHeuristicParser parser)
    {
        if (!_IsValidIndex(tableId.Value, _HeuristicTables.Count))
        {
            throw NotFoundRegistrationException.For($"Heuristic table ID {tableId.Value} not found");
        }
        _HeuristicTables[tableId.Value].AddEntry(new HeuristicParserEntry(parser));
    }

    #endregion

    #region Settings Registration

    /// <inheritdoc/>
    public SettingsRegistrar SettingsRegistrar => new(_SettingsManager);

    #endregion

    #region Deferred Registration

    /// <inheritdoc/>
    public void WhenProtocolRegistered(string name, Action<ProtocolId> callback)
    {
        _ValidateName(name);
        if (_ProtocolNameMap.TryGetValue(name, out ProtocolId id))
        {
            callback(id);
            return;
        }
        if (!_DeferredProtocol.TryGetValue(name, out List<Action<ProtocolId>>? list))
        {
            list = [];
            _DeferredProtocol[name] = list;
        }
        list.Add(callback);
    }

    /// <inheritdoc/>
    public void WhenFieldRegistered(string name, Action<FieldId> callback)
    {
        _ValidateName(name);
        if (_FieldNameMap.TryGetValue(name, out FieldId id))
        {
            callback(id);
            return;
        }
        if (!_DeferredField.TryGetValue(name, out List<Action<FieldId>>? list))
        {
            list = [];
            _DeferredField[name] = list;
        }
        list.Add(callback);
    }

    /// <inheritdoc/>
    public void WhenProtocolTableRegistered(string name, Action<ProtocolTableId> callback)
    {
        _ValidateName(name);
        if (_ProtocolTableNameMap.TryGetValue(name, out ProtocolTableId id))
        {
            callback(id);
            return;
        }
        if (!_DeferredTable.TryGetValue(name, out List<Action<ProtocolTableId>>? list))
        {
            list = [];
            _DeferredTable[name] = list;
        }
        list.Add(callback);
    }

    /// <inheritdoc/>
    public FrameInterfaceRegistry FrameInterfaceRegistry => _FrameInterfaceRegistry;

    #endregion

    #region Stream Reassembly

    /// <inheritdoc/>
    public void RegisterStreamReassemblyConfig(ProtocolId protocolId, StreamReassemblyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!_ReassemblyConfigs.TryAdd(protocolId, config))
        {
            throw DuplicateNameRegistrationException.For(
                $"Stream reassembly config already registered for protocol ID {protocolId.Value}");
        }
    }

    /// <inheritdoc/>
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) =>
        _ReassemblyConfigs.GetValueOrDefault(protocolId);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromU64ProtocolTable(ProtocolTableId tableId, ulong key)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAllU64(key);
        }
        return [];
    }

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromStringProtocolTable(ProtocolTableId tableId, string key)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAllString(key);
        }
        return [];
    }

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBytesProtocolTable(ProtocolTableId tableId, BytesKey key)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAllBytes(key);
        }
        return [];
    }

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBoolProtocolTable(ProtocolTableId tableId, bool key)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAllBool(key);
        }
        return [];
    }

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromAnyProtocolTable(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAllAny();
        }
        return [];
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? GetU64TableEntries(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].IterU64Entries();
        }
        return null;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? GetStringTableEntries(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].IterStringEntries();
        }
        return null;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? GetBytesTableEntries(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].IterBytesEntries();
        }
        return null;
    }

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? GetBoolTableEntries(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].IterBoolEntries();
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolId>? GetAnyTableProtocolIds(ProtocolTableId tableId)
    {
        if (_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            return _ProtocolTables[tableId.Value].GetAnyProtocolIds();
        }
        return null;
    }

    /// <inheritdoc/>
    public ProtocolId? TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data)
    {
        if (_IsValidIndex(tableId.Value, _HeuristicTables.Count))
        {
            return _HeuristicTables[tableId.Value].TryMatch(data);
        }
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// During the build phase, parse delegates are not yet bound, so this method
    /// always returns <see langword="null"/>. It is only meaningful on the built
    /// <see cref="Stack"/>.
    /// </remarks>
    public ParseDelegate? ResolveParseDelegate(ProtocolId id) => null;

    /// <summary>
    /// Freezes the builder into an immutable <see cref="Stack"/>.
    /// Protocol startup exceptions from <see cref="IProtocol.OnStart(Stack)"/> are collected on
    /// the returned stack instead of being thrown. Callers should inspect
    /// <see cref="Stack.BuildDiagnostics"/> after build.
    /// </summary>
    public Stack Build()
    {
        // Collect unresolved deferred callbacks as structured warnings
        List<BuildDiagnostic> diagnostics = [];
        _CollectUnresolvedCallbacks(_DeferredProtocol, BuildCallbackWarningKind.Protocol, diagnostics);
        _CollectUnresolvedCallbacks(_DeferredField, BuildCallbackWarningKind.Field, diagnostics);
        _CollectUnresolvedCallbacks(_DeferredTable, BuildCallbackWarningKind.ProtocolTable, diagnostics);

        // Freeze into arrays
        ProtocolInfo[] protocols = [.. _Protocols];
        IProtocol[] protocolInstances = [.. _ProtocolInstances];
        FieldInfo[] fields = [.. _Fields];

        // Build pre-bound parse delegates: each delegate captures the concrete
        // method pointer at creation time, so subsequent invocations bypass
        // the IProtocol interface vtable dispatch entirely.
        ParseDelegate[] parseDelegates = new ParseDelegate[protocolInstances.Length];
        for (int i = 0; i < protocolInstances.Length; i++)
        {
            parseDelegates[i] = protocolInstances[i].Parse;
        }
        ProtocolTable[] tables = [.. _ProtocolTables];
        ProtocolTableInfo[] tableInfos = [.. _ProtocolTableInfos];
        HeuristicProtocolTable[] heuristicTables = [.. _HeuristicTables];
        HeuristicProtocolTableInfo[] heuristicTableInfos = [.. _HeuristicTableInfos];
        // Sort post-parsers deterministically: ascending by Priority, then ascending by Id
        // (registration order) as a stable tie-breaker. Sorting at build time means zero
        // overhead in the per-packet parse hot path.
        PostParserInfo[] postParsers = [.. _PostParsers];
        Array.Sort(postParsers, static (a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Id.Value.CompareTo(b.Id.Value);
        });

        // Freeze name maps using FrozenDictionary
        FrozenDictionary<string, ProtocolId> protocolNameMap = _ProtocolNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, FieldId> fieldNameMap = _FieldNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, ProtocolTableId> tableNameMap = _ProtocolTableNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, HeuristicProtocolTableId> heuristicTableNameMap = _HeuristicTableNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, IndexGroupId> indexGroupNameMap = _IndexGroupMap.ToFrozenDictionary(StringComparer.Ordinal);

        // Build index group info array sorted by ID for direct indexing
        IndexGroupInfo[] indexGroups = new IndexGroupInfo[_IndexGroupMap.Count];
        foreach (KeyValuePair<string, IndexGroupId> kvp in _IndexGroupMap)
        {
            indexGroups[kvp.Value.Value] = new IndexGroupInfo(kvp.Value, kvp.Key);
        }

        // Freeze reassembly configs
        FrozenDictionary<ProtocolId, StreamReassemblyConfig> reassemblyConfigs =
            _ReassemblyConfigs.ToFrozenDictionary();

        // Freeze field alias groups (independent namespace from canonical fields)
        FieldAliasGroupInfo[] fieldAliasGroups = [.. _FieldAliasGroups];
        FrozenDictionary<string, FieldAliasGroupId> fieldAliasGroupNameMap =
            _FieldAliasGroupNameMap.ToFrozenDictionary(StringComparer.Ordinal);

        // Auto-discover the frame protocol by name (if registered)
        ProtocolId frameProtocolId = protocolNameMap.GetValueOrDefault(_FrameProtocolName, ProtocolId.Invalid);

        Stack stack = new(
            protocols, protocolInstances, parseDelegates, protocolNameMap,
            fields, fieldNameMap,
            tables, tableInfos, tableNameMap,
            heuristicTables, heuristicTableInfos, heuristicTableNameMap,
            postParsers,
            _SettingsManager,
            _RootFieldId, _PacketErrorFieldId, _PacketChoiceFieldId,
            _PacketProtocolId,
            frameProtocolId,
            _NextIndexGroupId,
            indexGroups, indexGroupNameMap,
            _FrameInterfaceRegistry,
            IncludeExceptionStackTrace,
            reassemblyConfigs,
            fieldAliasGroups, fieldAliasGroupNameMap);

        for (int i = 0; i < protocolInstances.Length; i++)
        {
            try
            {
                protocolInstances[i].OnStart(stack);
            }
            catch (Exception startupException)
            {
                diagnostics.Add(new BuildStartupError(
                    protocols[i].Id,
                    protocols[i].Name,
                    protocols[i].UiName,
                    startupException));
            }
        }

        if (diagnostics.Count > 0)
        {
            stack.SetBuildDiagnostics([.. diagnostics]);
        }

        return stack;
    }

    /// <summary>Collects unresolved deferred callbacks as structured <see cref="BuildCallbackWarning"/> entries.</summary>
    private static void _CollectUnresolvedCallbacks<T>(
        Dictionary<string, List<Action<T>>> deferred,
        BuildCallbackWarningKind entityKind,
        List<BuildDiagnostic> diagnostics)
    {
        foreach (KeyValuePair<string, List<Action<T>>> kvp in deferred)
        {
            diagnostics.Add(new BuildCallbackWarning(
                entityKind,
                kvp.Key,
                kvp.Value.Count));
        }
    }
    #endregion
}
