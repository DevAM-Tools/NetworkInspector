// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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
    private const string FrameProtocolName = "frame";

    /// <summary>
    /// When <see langword="true"/>, parser exception error messages include the full exception stack trace.
    /// Defaults to <see langword="false"/> to keep error messages concise in production.
    /// </summary>
    /// <remarks>
    /// Use object initializer syntax (<c>new StackBuilder(sm, reg) { IncludeExceptionStackTrace = true }</c>)
    /// to set this flag. The value is captured by <see cref="Build"/>; mutating it after build is impossible
    /// because the property is <see langword="init"/>-only.
    /// </remarks>
    public bool IncludeExceptionStackTrace { get; init; } = false;

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

        _PacketProtocolId = RegisterProtocol(
            new PacketProtocol(),
            static (builder, id, proto) => proto.RegisterWith(builder, id));

        // Root field is owned by RootProtocol (the dummy exists so that root has an owning protocol).
        _RootFieldId = RegisterField(rootProtocolId, "root", "Root", FieldType.None);

        // Error and choice fields are owned by PacketProtocol.
        _PacketErrorFieldId = RegisterField(_PacketProtocolId, "packet.error", "Error", FieldType.String);
        _PacketChoiceFieldId = RegisterField(
            _PacketProtocolId, "packet.choice", "Choice", FieldType.String,
            "Groups alternative parse results from ambiguous protocol dispatch");
    }

    #region IStack Implementation

    #endregion

    #region Protocol Access

    /// <inheritdoc/>
    public ProtocolInfo? GetProtocol(ProtocolId id) =>
        IsValidIndex(id.Value, _Protocols.Count) ? _Protocols[id.Value] : null;

    /// <inheritdoc/>
    public ProtocolId? GetProtocolId(string name) =>
        _ProtocolNameMap.TryGetValue(name, out ProtocolId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolInfo> Protocols => _Protocols.ToArray();

    /// <inheritdoc/>
    public int ProtocolCount => _Protocols.Count;

    #endregion

    #region Field Access

    /// <inheritdoc/>
    public FieldInfo? GetField(FieldId id) =>
        IsValidIndex(id.Value, _Fields.Count) ? _Fields[id.Value] : null;

    /// <inheritdoc/>
    public FieldId? GetFieldId(string name) =>
        _FieldNameMap.TryGetValue(name, out FieldId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldInfo> Fields => _Fields.ToArray();

    /// <inheritdoc/>
    public int FieldCount => _Fields.Count;

    /// <inheritdoc/>
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId)
    {
        if (IsValidIndex(fieldId.Value, _Fields.Count))
        {
            return _Fields[fieldId.Value].IndexGroup ?? IndexGroupId.Invalid;
        }
        return IndexGroupId.Invalid;
    }

    #endregion

    #region Index Group Access

    /// <inheritdoc/>
    public IndexGroupInfo? GetIndexGroup(IndexGroupId id)
    {
        if (!IsValidIndex(id.Value, _NextIndexGroupId))
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
    public IndexGroupId? GetIndexGroupId(string name) =>
        _IndexGroupMap.TryGetValue(name, out IndexGroupId id) ? id : null;

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
    public ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id) =>
        IsValidIndex(id.Value, _ProtocolTableInfos.Count) ? _ProtocolTableInfos[id.Value] : null;

    /// <inheritdoc/>
    public ProtocolTableId? GetProtocolTableId(string name) =>
        _ProtocolTableNameMap.TryGetValue(name, out ProtocolTableId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos => _ProtocolTableInfos.ToArray();

    /// <inheritdoc/>
    public int ProtocolTableCount => _ProtocolTableInfos.Count;

    #endregion

    #region Post-Parser Access

    /// <inheritdoc/>
    public ReadOnlyMemory<PostParserInfo> PostParsers => _PostParsers.ToArray();

    /// <inheritdoc/>
    public int PostParserCount => _PostParsers.Count;

    #endregion

    #region Heuristic Table Access

    /// <inheritdoc/>
    public HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id) =>
        IsValidIndex(id.Value, _HeuristicTableInfos.Count) ? _HeuristicTableInfos[id.Value] : null;

    /// <inheritdoc/>
    public HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name) =>
        _HeuristicTableNameMap.TryGetValue(name, out HeuristicProtocolTableId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos => _HeuristicTableInfos.ToArray();

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => _HeuristicTableInfos.Count;

    #endregion

    #region Settings Access

    /// <inheritdoc/>
    public IReadOnlySettingsManager Settings => _SettingsManager;

    /// <inheritdoc/>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => ReadOnlyMemory<BuildDiagnostic>.Empty;

    #endregion

    #region IStackBuilder Implementation

    #endregion

    #region Validation Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    /// <summary>Throws <see cref="InvalidNameRegistrationException"/> when the name is not a valid dot-separated C-style identifier.</summary>
    private static void ValidateName(string name)
    {
        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameRegistrationException.For(name);
        }
    }

    /// <summary>Throws <see cref="InvalidUiNameRegistrationException"/> when the UI name is empty or contains control characters.</summary>
    private static void ValidateUiName(string uiName)
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
        ValidateName(name);
        ValidateUiName(protocol.UiName);
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
        ValidateName(name);
        ValidateUiName(uiName);
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

    /// <inheritdoc/>
    public FieldId RegisterFieldInGroup(
        ProtocolId protocolId, string name, string uiName, FieldType fieldType,
        string indexGroup, string? description = null)
    {
        // name and uiName are validated inside RegisterField
        ValidateName(indexGroup);
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
        ValidateName(name);
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
        ValidateName(name);
        ValidateUiName(uiName);
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
        if (!IsValidIndex(tableId.Value, _ProtocolTables.Count))
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
        if (!IsValidIndex(tableId.Value, _ProtocolTables.Count))
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
        if (!IsValidIndex(tableId.Value, _ProtocolTables.Count))
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
        if (!IsValidIndex(tableId.Value, _ProtocolTables.Count))
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
        if (!IsValidIndex(tableId.Value, _ProtocolTables.Count))
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
        return id;
    }

    #endregion

    #region Heuristic Table Registration

    /// <inheritdoc/>
    public HeuristicProtocolTableId RegisterHeuristicProtocolTable(
        ProtocolId owningProtocolId, string name, string uiName, string? description = null)
    {
        ValidateName(name);
        ValidateUiName(uiName);
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
        if (!IsValidIndex(tableId.Value, _HeuristicTables.Count))
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
        ValidateName(name);
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
        ValidateName(name);
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
        ValidateName(name);
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

    /// <summary>The shared frame interface registry.</summary>
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

    /// <summary>
    /// Freezes the builder into an immutable <see cref="Stack"/>.
    /// Protocol startup exceptions from <see cref="IProtocol.OnStart(Stack)"/> are collected on
    /// the returned stack instead of being thrown. Callers should inspect
    /// <see cref="Stack.BuildDiagnostics"/> after build.
    /// </summary>
    public Stack Build()
    {
        // Register system-level settings (always available regardless of protocol stack)
        SettingsRegistrar.RegisterStringSetting(
            PacketIndex.ValueCacheFieldsSetting,
            "Value Cache Fields",
            "index",
            string.Empty,
            "Comma-separated list of fields to cache for fast columnar access. "
            + "Format: 'field1:mode,field2' where mode is optional (native, compact_float, "
            + "compact_int8/16/32, compact_uint8/16/32). Example: 'tcp.srcport:compact_uint16,ip.src'");

        // Collect unresolved deferred callbacks as structured warnings
        List<BuildDiagnostic> diagnostics = [];
        CollectUnresolvedCallbacks(_DeferredProtocol, BuildCallbackWarningKind.Protocol, diagnostics);
        CollectUnresolvedCallbacks(_DeferredField, BuildCallbackWarningKind.Field, diagnostics);
        CollectUnresolvedCallbacks(_DeferredTable, BuildCallbackWarningKind.ProtocolTable, diagnostics);

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
        PostParserInfo[] postParsers = [.. _PostParsers];

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

        // Auto-discover the frame protocol by name (if registered)
        ProtocolId frameProtocolId = protocolNameMap.GetValueOrDefault(FrameProtocolName, ProtocolId.Invalid);

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
            reassemblyConfigs);

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
    private static void CollectUnresolvedCallbacks<T>(
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