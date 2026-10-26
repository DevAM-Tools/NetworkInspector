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
    #region Fields

    // Registration storage
    private readonly List<ProtocolInfo> _Protocols = [];
    private readonly List<IProtocol> _ProtocolInstances = [];
    private readonly Dictionary<string, ProtocolId> _ProtocolNameMap = new(StringComparer.Ordinal);

    private readonly List<FieldInfo> _Fields = [];
    private readonly Dictionary<string, FieldId> _FieldNameMap = new(StringComparer.Ordinal);

    // Field alias groups (independent namespace; never resolves through _FieldNameMap)
    private readonly List<FieldAliasGroupInfo> _FieldAliasGroups = [];
    private readonly Dictionary<string, FieldAliasGroupId> _FieldAliasGroupNameMap = new(StringComparer.Ordinal);

    // Snapshot caches for the build-phase property getters (Protocols, Fields, FieldAliasGroups, PostParsers, ...). IStack
    // exposes these collections as ReadOnlyMemory<T>, which cannot wrap a List<T>, so a getter copies the list into an array.
    // The copy is cached and dropped (set to null) by every registration that changes the list, so protocols that read a
    // collection repeatedly while registering do not pay an O(n) copy per read, and a returned memory stays a stable view
    // of the registrations made up to that moment (later registrations create a new array instead of altering it).
    // Single-threaded like the rest of the builder; no synchronization.
    private FieldAliasGroupInfo[]? _FieldAliasGroupsSnapshot;
    private ProtocolInfo[]? _ProtocolsSnapshot;
    private FieldInfo[]? _FieldsSnapshot;
    private PostParserInfo[]? _PostParsersSnapshot;
    private ProtocolTableInfo[]? _ProtocolTableInfosSnapshot;
    private HeuristicProtocolTableInfo[]? _HeuristicTableInfosSnapshot;
    private IndexGroupInfo[]? _IndexGroupsSnapshot;

    private readonly List<ProtocolTable> _ProtocolTables = [];
    private readonly List<ProtocolTableInfo> _ProtocolTableInfos = [];
    private readonly Dictionary<string, ProtocolTableId> _ProtocolTableNameMap = new(StringComparer.Ordinal);

    private readonly List<HeuristicProtocolTable> _HeuristicTables = [];
    private readonly List<HeuristicProtocolTableInfo> _HeuristicTableInfos = [];
    private readonly Dictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap = new(StringComparer.Ordinal);

    private readonly List<PostParserInfo> _PostParsers = [];
    private readonly SettingsManager _SettingsManager;

    // Set by Build; a second Build is refused. Single-threaded like the rest of the builder.
    private bool _Built;

    // Stream reassembly configs (keyed by protocol ID)
    private readonly Dictionary<ProtocolId, StreamReassemblyConfig> _ReassemblyConfigs = [];

    // Index groups. Unlike the other entities there is no list: an index group is just a name with a dense ID, so the next ID
    // is a counter and the ID-to-name direction is a linear scan of the map (build phase only).
    private readonly Dictionary<string, IndexGroupId> _IndexGroupMap = new(StringComparer.Ordinal);
    private int _NextIndexGroupId;

    // Frame interface registry (shared with Stack after Build)
    /// <inheritdoc/>
    public FrameInterfaceRegistry FrameInterfaceRegistry { get; }

    // Deferred callbacks (WhenXRegistered): callbacks waiting for a name that is not registered yet, in subscription order.
    // An entry is removed when its name is registered and its callbacks have run, so what is left at Build time is exactly
    // the set of callbacks that never fired (reported as warnings).
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
    /// Must match <c>FrameProtocol.ProtocolName</c> from <c>NetworkInspector.Protocols</c>. The name is repeated here because
    /// that project depends on this one, so the constant cannot be referenced from Core. A mismatch is not silent: Build
    /// then reports <see cref="BuildCallbackWarningKind.MissingFrameProtocol"/>.
    /// </summary>
    private const string _FrameProtocolName = "frame";

    #endregion

    #region Constructors

    /// <inheritdoc/>
    /// <remarks>
    /// Use object initializer syntax (<c>new StackBuilder(sm, reg) { IncludeExceptionStackTrace = true }</c>)
    /// to set this flag. The value is captured by <see cref="Build"/>; mutating it after build is impossible
    /// because the property is <see langword="init"/>-only. When <see langword="true"/>, the error text that
    /// a packet records for an exception thrown by a protocol during parsing also contains the exception's stack trace.
    /// </remarks>
    public bool IncludeExceptionStackTrace { get; init; }

    /// <summary>Creates a new stack builder with externally provided dependencies and registers built-in root and error fields.</summary>
    /// <param name="settingsManager">
    /// The settings manager instance for managing protocol settings. It is handed on to the built
    /// <see cref="Stack"/>, whose <see cref="Stack.Dispose"/> releases it.
    /// </param>
    /// <param name="frameInterfaceRegistry">The frame interface registry for managing capture interfaces (shared with the built stack).</param>
    /// <exception cref="ArgumentNullException"><paramref name="settingsManager"/> or <paramref name="frameInterfaceRegistry"/> is <see langword="null"/>.</exception>
    public StackBuilder(SettingsManager settingsManager, FrameInterfaceRegistry frameInterfaceRegistry)
    {
        // Both are dereferenced later (the registration of the built-in protocols, the settings of every protocol, Build); a
        // null here would otherwise surface as a NullReferenceException far from the cause, or only after Build.
        ArgumentNullException.ThrowIfNull(settingsManager);
        ArgumentNullException.ThrowIfNull(frameInterfaceRegistry);

        _SettingsManager = settingsManager;
        FrameInterfaceRegistry = frameInterfaceRegistry;

        // Register built-in protocols:
        // RootProtocol is an empty dummy that owns the root field.
        // PacketProtocol is the top-level parse entry point (appends packet metadata, dispatches to frame).
        // They are registered in the constructor, before any caller can register anything, so every stack has them and no
        // later registration can take their names ("root", "packet.error", "packet.choice").
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

    #endregion

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
    public ReadOnlyMemory<ProtocolInfo> Protocols
    {
        get
        {
            _ProtocolsSnapshot ??= [.. _Protocols];
            return _ProtocolsSnapshot;
        }
    }

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
    public ReadOnlyMemory<FieldInfo> Fields
    {
        get
        {
            _FieldsSnapshot ??= [.. _Fields];
            return _FieldsSnapshot;
        }
    }

    /// <inheritdoc/>
    public int FieldCount => _Fields.Count;

    /// <inheritdoc/>
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId)
    {
        if (_IsValidIndex(fieldId.Value, _Fields.Count))
        {
            IndexGroupId? indexGroup = _Fields[fieldId.Value].IndexGroup;
            if (indexGroup is not null)
            {
                return indexGroup.Value;
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
            if (_IndexGroupsSnapshot is null)
            {
                IndexGroupInfo[] infos = new IndexGroupInfo[_IndexGroupMap.Count];
                int i = 0;
                foreach (KeyValuePair<string, IndexGroupId> kvp in _IndexGroupMap)
                {
                    infos[i++] = new IndexGroupInfo(kvp.Value, kvp.Key);
                }
                _IndexGroupsSnapshot = infos;
            }
            return _IndexGroupsSnapshot;
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
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos
    {
        get
        {
            _ProtocolTableInfosSnapshot ??= [.. _ProtocolTableInfos];
            return _ProtocolTableInfosSnapshot;
        }
    }

    /// <inheritdoc/>
    public int ProtocolTableCount => _ProtocolTableInfos.Count;

    #endregion

    #region Post-Parser Access

    /// <inheritdoc/>
    /// <remarks>Returns post-parsers in the same deterministic sort order as <see cref="IStack.PostParsers"/>:
    /// ascending by <see cref="PostParserInfo.Priority"/>, then ascending by <see cref="PostParserInfo.Id"/>
    /// (registration order) as a stable tie-breaker. The list is re-sorted after every
    /// <see cref="RegisterPostParser"/> call so the order is always up to date.</remarks>
    public ReadOnlyMemory<PostParserInfo> PostParsers
    {
        get
        {
            _PostParsersSnapshot ??= [.. _PostParsers];
            return _PostParsersSnapshot;
        }
    }

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
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos
    {
        get
        {
            _HeuristicTableInfosSnapshot ??= [.. _HeuristicTableInfos];
            return _HeuristicTableInfosSnapshot;
        }
    }

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => _HeuristicTableInfos.Count;

    #endregion

    #region Settings Access

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView Settings => _SettingsManager.ReadOnly;

    /// <inheritdoc/>
    /// <remarks>Always empty: diagnostics are produced by <see cref="Build"/> and live on the built <see cref="Stack"/>.</remarks>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => ReadOnlyMemory<BuildDiagnostic>.Empty;

    #endregion

    #region Validation Helpers

    /// <summary>
    /// Whether <paramref name="idValue"/> is a valid index into a collection of <paramref name="count"/> entries. The
    /// unsigned comparison covers both bounds in one branch: a negative value (such as the <c>Invalid</c> sentinel, -1)
    /// becomes a huge unsigned number and fails the same test as a value past the end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    /// <summary>
    /// Throws when <paramref name="nextIndex"/> exceeds <see cref="ArrayIndexIdRange.MaxValue"/>. IDs are dense array indexes
    /// of an entity list, so the number of entries is bounded by the largest array the runtime can allocate; the guard turns
    /// that overflow into a clear error before an ID is issued. Constant by nature (a runtime fact, not a host setting).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _GuardIndexAllocation(int nextIndex, string entityName) =>
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(nextIndex, entityName);

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

    /// <summary>
    /// Invokes deferred registration callbacks for <paramref name="name"/>, collecting every
    /// exception so all subscribers run before surfacing an <see cref="AggregateException"/>.
    /// The entity is already registered when this runs; an exception reports failing subscribers but does not undo the
    /// registration.
    /// </summary>
    private static void _InvokeDeferredCallbacks<T>(
        Dictionary<string, List<Action<T>>> deferred,
        string name,
        T id,
        string callbackKind)
    {
        // Removing the entry first means each callback list fires at most once, and that a callback which subscribes to the
        // same name again (the name is now registered) is answered immediately by WhenXRegistered instead of being
        // appended to the list that is being iterated.
        if (!deferred.Remove(name, out List<Action<T>>? callbacks))
        {
            return;
        }

        // One failing subscriber (typically a plugin) must not stop the others from learning the ID, so exceptions are
        // collected and thrown together after all callbacks ran. The list is allocated only when something failed.
        List<Exception>? errors = null;
        foreach (Action<T> cb in callbacks)
        {
            try
            {
                cb(id);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException(
                $"One or more When{callbackKind}Registered callbacks failed for '{name}'.",
                errors);
        }
    }

    #endregion

    #region Protocol Registration

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol(IProtocol protocol)
    {
        // Validate everything before touching any list, so a rejected registration leaves the builder unchanged. The ID is
        // the dense list index, which is what lets the built Stack resolve it by array indexing.
        string name = protocol.Name;
        _ValidateName(name);
        _ValidateUiName(protocol.UiName);
        if (_ProtocolNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        // Commit: the info list, the instance list (same index), and the name map move together; the cached snapshot is
        // dropped because it no longer matches the list.
        _GuardIndexAllocation(_Protocols.Count, "protocol");
        ProtocolId id = new(_Protocols.Count);
        ProtocolInfo info = new(id, name, protocol.UiName, protocol.Description);
        _Protocols.Add(info);
        _ProtocolInstances.Add(protocol);
        _ProtocolNameMap[name] = id;
        _ProtocolsSnapshot = null;

        // Subscribers run after the state is committed, so they can already look the protocol up. An AggregateException from
        // a subscriber leaves the protocol registered.
        _InvokeDeferredCallbacks(_DeferredProtocol, name, id, "Protocol");

        return id;
    }

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol<TProtocol>(
        TProtocol protocol,
        Action<IStackBuilder, ProtocolId, TProtocol> callback)
        where TProtocol : IProtocol
    {
        // The typed overload hands the concrete protocol instance back to the callback, so the caller needs no cast and
        // (with a static lambda) no closure to register the protocol's fields and tables right after its ID exists.
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
        // Field names share one namespace across all protocols (by convention prefixed with the protocol name, for example
        // "packet.error"), and the field ID is the dense list index. Validation comes first so a rejected registration
        // changes nothing.
        _ValidateName(name);
        _ValidateUiName(uiName);

        // The owning protocol must exist, or be the documented "no owner" sentinel ProtocolId.Invalid (IStackBuilder). Any other
        // unknown ID (for example one that came from another builder) would be stored as it is and later break everything
        // that resolves a field's protocol, such as the alias group membership check.
        if (protocolId != ProtocolId.Invalid && !_IsValidIndex(protocolId.Value, _Protocols.Count))
        {
            throw NotFoundRegistrationException.For(
                $"Field '{name}' references unknown protocol ID {protocolId.Value}");
        }

        if (_FieldNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name);
        }

        // The index group is still null here; RegisterFieldInGroup assigns it afterwards on the stored FieldInfo.
        _GuardIndexAllocation(_Fields.Count, "field");
        FieldId id = new(_Fields.Count);
        FieldInfo info = new(id, protocolId, name, uiName, fieldType, description, null);
        _Fields.Add(info);
        _FieldNameMap[name] = id;
        _FieldsSnapshot = null;

        _InvokeDeferredCallbacks(_DeferredField, name, id, "Field");

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

        // Defensive copy: callers must not influence stored membership after registration. Members are validated while they
        // are copied, and the alias group is added to the lists only after every member passed, so a rejected group leaves
        // no partial registration.
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

            // Duplicate member IDs are rejected; mixed FieldType values are intentionally allowed. The pairwise scan is
            // O(n^2) but runs once per registration over a small member list, so a set would only add allocation.
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

        _GuardIndexAllocation(_FieldAliasGroups.Count, "field alias group");
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
        // name and uiName are validated inside RegisterField. The group name is validated first, so that a bad group name
        // cannot leave behind a registered field that has no group.
        _ValidateName(indexGroup);
        FieldId id = RegisterField(protocolId, name, uiName, fieldType, description);

        // Resolve or create index group. Groups are created on first use and shared by name. Fields that always appear
        // together are registered into one group so that they share a single presence bitmap in the cross-packet index.
        if (!_IndexGroupMap.TryGetValue(indexGroup, out IndexGroupId groupId))
        {
            _GuardIndexAllocation(_NextIndexGroupId, "index group");
            groupId = new IndexGroupId(_NextIndexGroupId++);
            _IndexGroupMap[indexGroup] = groupId;
            _IndexGroupsSnapshot = null;
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
            _GuardIndexAllocation(_NextIndexGroupId, "index group");
            groupId = new IndexGroupId(_NextIndexGroupId++);
            _IndexGroupMap[name] = groupId;
            _IndexGroupsSnapshot = null;
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

        // The info list and the table list advance together, so one ID indexes both (Stack finds the table by array index).
        _GuardIndexAllocation(_ProtocolTableInfos.Count, "protocol table");
        ProtocolTableId id = new(_ProtocolTableInfos.Count);
        ProtocolTableInfo info = new(id, name, uiName, keyType, description);
        _ProtocolTableInfos.Add(info);
        _ProtocolTables.Add(new ProtocolTable(info));
        _ProtocolTableNameMap[name] = id;
        _ProtocolTableInfosSnapshot = null;

        _InvokeDeferredCallbacks(_DeferredTable, name, id, "ProtocolTable");

        return id;
    }

    // The RegisterParserIn* members share one shape: an unknown table ID or name is a registration bug and throws
    // NotFoundRegistrationException; the ByName variants resolve the name and delegate to the ID variant. The table itself
    // rejects a key of the wrong type for U64, String, and Bytes tables (an InvalidOperationException from ProtocolTable)
    // and ignores a protocol that is already registered under the same key. Several protocols under one key are allowed;
    // they are kept in registration order and the dispatcher treats such a key as ambiguous (packet.choice).

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
        _GuardIndexAllocation(_PostParsers.Count, "post-parser");
        PostParserId id = new(_PostParsers.Count);
        PostParserInfo info = new(id, priority, protocolId, description);
        _PostParsers.Add(info);
        // Re-sort after every registration to keep the list in the order defined by
        // IStack.PostParsers (Priority asc, then Id asc as tie-breaker). Registration
        // lists are small so the O(n log n) cost per call is negligible. List<T>.Sort is not a stable sort, which is
        // why the Id tie-breaker is part of the comparison: it makes the order total and therefore deterministic. The Id
        // is the registration number, not the list position, so it stays unique after the list is reordered.
        _PostParsers.Sort(static (a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Id.Value.CompareTo(b.Id.Value);
        });
        _PostParsersSnapshot = null;
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

        _GuardIndexAllocation(_HeuristicTableInfos.Count, "heuristic protocol table");
        HeuristicProtocolTableId id = new(_HeuristicTableInfos.Count);
        HeuristicProtocolTableInfo info = new(id, name, uiName, description, owningProtocolId);
        // As with protocol tables, the info list and the table list advance together, so one ID indexes both.
        _HeuristicTableInfos.Add(info);
        _HeuristicTables.Add(new HeuristicProtocolTable(info));
        _HeuristicTableNameMap[name] = id;
        _HeuristicTableInfosSnapshot = null;

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
    /// <remarks>
    /// Returns a new <c>SettingsRegistrar</c> value on every access. It is a <c>ref struct</c> over the shared
    /// manager, so nothing is allocated and a protocol cannot keep a reference to the manager through it.
    /// </remarks>
    public SettingsRegistrar SettingsRegistrar => new(_SettingsManager);

    #endregion

    #region Deferred Registration

    // The WhenXRegistered members let a protocol depend on an entity of another protocol without caring about registration
    // order. If the name is already registered the callback runs
    // right away; otherwise it is queued under the name and runs when the entity is registered (see
    // _InvokeDeferredCallbacks). A callback that is still queued at Build time is reported as a warning, not an error,
    // because the other protocol may legitimately be absent. The name is validated so a typo with invalid characters fails
    // at once instead of silently never firing.

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
    /// <para>
    /// Call it once per builder. The returned stack shares the protocol instances, the (mutable) dispatch tables, the
    /// settings manager, and the frame interface registry with this builder, and a second call would run
    /// <see cref="IProtocol.OnStart(Stack)"/> again on the same protocol instances. The builder must not be used for
    /// registration after Build. A second call throws <see cref="InvalidOperationException"/> and starts nothing.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Build was already called on this builder.</exception>
    public Stack Build()
    {
        // Refuse a second build before anything is touched: it would give two stacks that share the mutable dispatch tables and
        // the protocol instances, and OnStart would run twice on the same instances. The mark is set before the work so that a
        // build that fails midway cannot be repeated on a half-used builder either.
        if (_Built)
        {
            throw new InvalidOperationException("Build was already called on this StackBuilder; create a new builder for another stack.");
        }

        _Built = true;

        // Collect unresolved deferred callbacks as structured warnings
        List<BuildDiagnostic> diagnostics = [];
        _CollectUnresolvedCallbacks(_DeferredProtocol, BuildCallbackWarningKind.Protocol, diagnostics);
        _CollectUnresolvedCallbacks(_DeferredField, BuildCallbackWarningKind.Field, diagnostics);
        _CollectUnresolvedCallbacks(_DeferredTable, BuildCallbackWarningKind.ProtocolTable, diagnostics);

        // Freeze into arrays
        ProtocolInfo[] protocols = [.. _Protocols];
        IProtocol[] protocolInstances = [.. _ProtocolInstances];
        FieldInfo[] fields = [.. _Fields];

        // Concrete Parse method pointers — each targets IProtocol.Parse. Creating a delegate from an interface-typed
        // reference resolves the implementing method once, here, so Stack.CallProtocol later invokes the implementation
        // through the delegate without an interface dispatch per call. The delegates are stored at the protocol's ID.
        ParseDelegate[] parseDelegates = new ParseDelegate[protocolInstances.Length];
        for (int i = 0; i < protocolInstances.Length; i++)
        {
            parseDelegates[i] = protocolInstances[i].Parse;
        }

        // The arrays are fresh copies, but the table objects inside them are the builder's own (mutable) instances: the
        // builder must not register into them after Build.
        ProtocolTable[] tables = [.. _ProtocolTables];
        ProtocolTableInfo[] tableInfos = [.. _ProtocolTableInfos];
        HeuristicProtocolTable[] heuristicTables = [.. _HeuristicTables];
        HeuristicProtocolTableInfo[] heuristicTableInfos = [.. _HeuristicTableInfos];

        // Sort post-parsers deterministically: ascending by Priority, then ascending by Id
        // (registration order) as a stable tie-breaker. Sorting at build time means zero
        // overhead in the per-packet parse hot path. RegisterPostParser already keeps the list in this order, so this sort
        // changes nothing today; it keeps the stack's order independent of how the list was filled.
        PostParserInfo[] postParsers = [.. _PostParsers];
        Array.Sort(postParsers, static (a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Id.Value.CompareTo(b.Id.Value);
        });

        // Freeze name maps using FrozenDictionary: one-time build cost for faster lookups, and read-only data that any
        // number of threads can read without locking. Ordinal comparison matches the builder's maps and the name rules.
        FrozenDictionary<string, ProtocolId> protocolNameMap = _ProtocolNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, FieldId> fieldNameMap = _FieldNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, ProtocolTableId> tableNameMap = _ProtocolTableNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, HeuristicProtocolTableId> heuristicTableNameMap = _HeuristicTableNameMap.ToFrozenDictionary(StringComparer.Ordinal);
        FrozenDictionary<string, IndexGroupId> indexGroupNameMap = _IndexGroupMap.ToFrozenDictionary(StringComparer.Ordinal);

        // Build index group info array sorted by ID for direct indexing. The map enumerates in no useful order, so each entry
        // is placed at its own ID; IDs are dense (0 .. count - 1), so every slot is filled exactly once.
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
        if (!frameProtocolId.IsValid)
        {
            diagnostics.Add(new BuildCallbackWarning(
                BuildCallbackWarningKind.MissingFrameProtocol,
                _FrameProtocolName,
                0));
        }

        // The stack exists before OnStart runs so that protocols can resolve IDs, tables, and settings in OnStart. Apart from
        // those OnStart calls it is not shared yet, so the diagnostics can still be attached after the loop without
        // racing a reader.
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
            indexGroups, indexGroupNameMap,
            FrameInterfaceRegistry,
            IncludeExceptionStackTrace,
            reassemblyConfigs,
            fieldAliasGroups, fieldAliasGroupNameMap);

        // Start protocols in registration order. A failing OnStart (protocols may come from plugins) is recorded as a
        // diagnostic and the build continues: one broken protocol must not take down the whole stack, and the host decides
        // from BuildDiagnostics whether the failure matters. The cost is that a started stack may have a protocol whose
        // OnStart did not complete.
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
