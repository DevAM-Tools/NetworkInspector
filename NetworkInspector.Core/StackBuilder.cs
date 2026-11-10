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
    #region Configuration and state

    /// <summary>Registered protocol infos; the list index is the <see cref="ProtocolId"/>.</summary>
    private readonly List<ProtocolInfo> _Protocols = [];

    /// <summary>Registered protocol instances; same index as <see cref="_Protocols"/>.</summary>
    private readonly List<IProtocol> _ProtocolInstances = [];

    /// <summary>Protocol name to id. Ordinal; frozen into the built stack.</summary>
    private readonly Dictionary<string, ProtocolId> _ProtocolNameMap = new(StringComparer.Ordinal);

    /// <summary>Registered field infos; the list index is the <see cref="FieldId"/>.</summary>
    private readonly List<FieldInfo> _Fields = [];

    /// <summary>Field name to id. Ordinal; frozen into the built stack.</summary>
    private readonly Dictionary<string, FieldId> _FieldNameMap = new(StringComparer.Ordinal);

    /// <summary>Registered field alias groups; the list index is the <see cref="FieldAliasGroupId"/>.</summary>
    private readonly List<FieldAliasGroupInfo> _FieldAliasGroups = [];

    /// <summary>
    /// Field alias group name to id. The map is the group's own, but the name still shares the conflict namespace of protocols
    /// and fields (see <see cref="RegisterProtocol(IProtocol)"/>).
    /// </summary>
    private readonly Dictionary<string, FieldAliasGroupId> _FieldAliasGroupNameMap = new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshot cache of <see cref="FieldAliasGroups"/>. All <c>_*Snapshot</c> fields serve the build-phase property getters
    /// (<see cref="Protocols"/>, <see cref="Fields"/>, <see cref="FieldAliasGroups"/>, <see cref="PostParsers"/>, ...).
    /// <see cref="IStack"/> exposes these collections as <see cref="ReadOnlyMemory{T}"/>, which cannot wrap a
    /// <see cref="List{T}"/>, so a getter copies the list into an array. The copy is cached and dropped (set to null) by
    /// every registration that changes the list, so protocols that read a collection repeatedly while registering do not pay
    /// an O(n) copy per read, and a returned memory stays a stable view of the registrations made up to that moment (later
    /// registrations create a new array instead of altering it). Single-threaded like the rest of the builder; no
    /// synchronization.
    /// </summary>
    private FieldAliasGroupInfo[]? _FieldAliasGroupsSnapshot;

    /// <summary>Snapshot cache of <see cref="Protocols"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private ProtocolInfo[]? _ProtocolsSnapshot;

    /// <summary>Snapshot cache of <see cref="Fields"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private FieldInfo[]? _FieldsSnapshot;

    /// <summary>Snapshot cache of <see cref="PostParsers"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private PostParserInfo[]? _PostParsersSnapshot;

    /// <summary>Snapshot cache of <see cref="ProtocolTableInfos"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private ProtocolTableInfo[]? _ProtocolTableInfosSnapshot;

    /// <summary>Snapshot cache of <see cref="HeuristicProtocolTableInfos"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private HeuristicProtocolTableInfo[]? _HeuristicTableInfosSnapshot;

    /// <summary>Snapshot cache of <see cref="IndexGroups"/>; see <see cref="_FieldAliasGroupsSnapshot"/>.</summary>
    private IndexGroupInfo[]? _IndexGroupsSnapshot;

    /// <summary>Registered dispatch tables; same index as <see cref="_ProtocolTableInfos"/>. The built stack shares these objects.</summary>
    private readonly List<ProtocolTable> _ProtocolTables = [];

    /// <summary>Registered protocol table infos; the list index is the <see cref="ProtocolTableId"/>.</summary>
    private readonly List<ProtocolTableInfo> _ProtocolTableInfos = [];

    /// <summary>Protocol table name to id. Ordinal; frozen into the built stack.</summary>
    private readonly Dictionary<string, ProtocolTableId> _ProtocolTableNameMap = new(StringComparer.Ordinal);

    /// <summary>Registered heuristic tables; same index as <see cref="_HeuristicTableInfos"/>. The built stack shares these objects.</summary>
    private readonly List<HeuristicProtocolTable> _HeuristicTables = [];

    /// <summary>Registered heuristic table infos; the list index is the <see cref="HeuristicProtocolTableId"/>.</summary>
    private readonly List<HeuristicProtocolTableInfo> _HeuristicTableInfos = [];

    /// <summary>Heuristic table name to id. Ordinal; frozen into the built stack.</summary>
    private readonly Dictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap = new(StringComparer.Ordinal);

    /// <summary>Registered post-parsers, kept sorted by priority and then by id.</summary>
    private readonly List<PostParserInfo> _PostParsers = [];

    /// <summary>The settings manager protocols register their settings with; handed on to the built stack.</summary>
    private readonly SettingsManager _SettingsManager;

    /// <summary>
    /// Set by <see cref="Build"/>; a second Build and every registration after it are refused (<see cref="_ThrowIfBuilt"/>).
    /// Single-threaded like the rest of the builder.
    /// </summary>
    private bool _Built;

    /// <summary>The live holders of the limits of Core, created from the settings the builder registered them in.</summary>
    private readonly CoreLimitSet _Limits;

    /// <summary>Stream reassembly configs keyed by protocol id.</summary>
    private readonly Dictionary<ProtocolId, StreamReassemblyConfig> _ReassemblyConfigs = [];

    /// <summary>
    /// Index group name to id. Unlike the other entities there is no list: an index group is just a name with a dense id, so
    /// the next id is a counter (<see cref="_NextIndexGroupId"/>) and the id-to-name direction is a linear scan of the map
    /// (build phase only).
    /// </summary>
    private readonly Dictionary<string, IndexGroupId> _IndexGroupMap = new(StringComparer.Ordinal);

    /// <summary>The id the next new index group receives; equals the number of groups.</summary>
    private int _NextIndexGroupId;

    /// <summary>
    /// Callbacks waiting for a protocol name that is not registered yet, in subscription order. An entry is removed when its
    /// name is registered and its callbacks have run, so what is left at <see cref="Build"/> time is exactly the set of
    /// callbacks that never fired (reported as warnings). <see cref="_DeferredField"/> and <see cref="_DeferredTable"/> do the
    /// same for fields and protocol tables.
    /// </summary>
    private readonly Dictionary<string, List<Action<ProtocolId>>> _DeferredProtocol = new(StringComparer.Ordinal);

    /// <summary>Callbacks waiting for a field name; see <see cref="_DeferredProtocol"/>.</summary>
    private readonly Dictionary<string, List<Action<FieldId>>> _DeferredField = new(StringComparer.Ordinal);

    /// <summary>Callbacks waiting for a protocol table name; see <see cref="_DeferredProtocol"/>.</summary>
    private readonly Dictionary<string, List<Action<ProtocolTableId>>> _DeferredTable = new(StringComparer.Ordinal);

    /// <summary>The id of the root field, owned by the built-in root protocol; set by the constructor.</summary>
    private readonly FieldId _RootFieldId = FieldId.Invalid;

    /// <summary>The id of the <c>packet.error</c> field, owned by the packet protocol; set by the constructor.</summary>
    private readonly FieldId _PacketErrorFieldId = FieldId.Invalid;

    /// <summary>The id of the <c>packet.choice</c> field, owned by the packet protocol; set by the constructor.</summary>
    private readonly FieldId _PacketChoiceFieldId = FieldId.Invalid;

    /// <summary>The id of the built-in packet protocol, the top-level parse entry point; set by the constructor.</summary>
    private readonly ProtocolId _PacketProtocolId = ProtocolId.Invalid;

    /// <summary>
    /// Protocol name used to auto-discover the frame protocol during <see cref="Build"/>.
    /// Must match <c>FrameProtocol.ProtocolName</c> from <c>NetworkInspector.Protocols</c>. The name is repeated here because
    /// that project depends on this one, so the constant cannot be referenced from Core. A mismatch is not silent: Build
    /// then reports <see cref="BuildCallbackWarningKind.MissingFrameProtocol"/>.
    /// </summary>
    private const string _FrameProtocolName = "frame";

    /// <inheritdoc/>
    /// <remarks>
    /// Use object initializer syntax (<c>new StackBuilder(sm, reg) { IncludeExceptionStackTrace = true }</c>)
    /// to set this flag. The value is captured by <see cref="Build"/>; mutating it after build is impossible
    /// because the property is <see langword="init"/>-only. When <see langword="true"/>, the error text that
    /// a packet records for an exception thrown by a protocol during parsing also contains the exception's stack trace.
    /// </remarks>
    public bool IncludeExceptionStackTrace { get; init; }

    /// <inheritdoc/>
    /// <remarks>The registry is shared with the built <see cref="Stack"/>.</remarks>
    public FrameInterfaceRegistry FrameInterfaceRegistry { get; }

    /// <summary>Creates a new stack builder with externally provided dependencies and registers built-in root and error fields.</summary>
    /// <param name="settingsManager">
    /// The settings manager instance for managing protocol settings. It is handed on to the built
    /// <see cref="Stack"/>, whose <see cref="Stack.Dispose"/> releases it.
    /// </param>
    /// <param name="frameInterfaceRegistry">The frame interface registry for managing capture interfaces (shared with the built stack).</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="settingsManager"/> or <paramref name="frameInterfaceRegistry"/> is <see langword="null"/>.
    /// </exception>
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

        // The limits of Core are settings, registered before any protocol registers its own: a user changes them with the
        // settings file, `ni settings set`, or `--set`, and the live holders below follow the values. Limits that are read when
        // an owner is built (the reassembly limits: the stack starts the reassemblers and a connection resolves its
        // configuration) are SessionReset; the others are read at every decision and are Immediate. A stored value that is not
        // valid is reported by the settings manager as a load problem; the limit then keeps its default.
        SettingsRegistrar registrar = SettingsRegistrar;
        foreach (LimitDefinition definition in CoreLimits.All)
        {
            SettingActivation activation = definition.Group == "reassembly"
                ? SettingActivation.SessionReset
                : SettingActivation.Immediate;
            _ = registrar.RegisterLimit(definition, activation);
        }

        _Limits = CoreLimitSet.FromSettings(_SettingsManager.ReadOnly);

        // The registry may be shared by several stacks (one registry per host): the stack that is built last decides the limit
        // of the registry, which is the limit its own settings hold.
        FrameInterfaceRegistry.BindInterfaceLimit(_Limits.MaxInterfaces);
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

    #region Settings and Diagnostics Access

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView Settings => _SettingsManager.ReadOnly;

    /// <inheritdoc/>
    /// <remarks>Always empty: diagnostics are produced by <see cref="Build"/> and live on the built <see cref="Stack"/>.</remarks>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => ReadOnlyMemory<BuildDiagnostic>.Empty;

    #endregion

    #region Guards and Validation

    /// <summary>
    /// Whether <paramref name="idValue"/> is a valid index into a collection of <paramref name="count"/> entries. The
    /// unsigned comparison covers both bounds in one branch: a negative value (such as the <c>Invalid</c> sentinel, -1)
    /// becomes a huge unsigned number and fails the same test as a value past the end.
    /// </summary>
    /// <param name="idValue">The id number to test.</param>
    /// <param name="count">The number of entries of the indexed collection.</param>
    /// <returns><see langword="true"/> when <c>0 &lt;= idValue &lt; count</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    /// <summary>
    /// Throws when <paramref name="nextIndex"/> exceeds <see cref="ArrayIndexIdRange.MaxValue"/>. IDs are dense array indexes
    /// of an entity list, so the number of entries is bounded by the largest array the runtime can allocate; the guard turns
    /// that overflow into a clear error before an ID is issued. Constant by nature (a runtime fact, not a host setting).
    /// </summary>
    /// <param name="nextIndex">The index the next entry would receive; the current number of entries.</param>
    /// <param name="entityName">The kind of entity, named in the error message (for example "protocol").</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _GuardIndexAllocation(int nextIndex, string entityName) =>
        ArrayIndexIdRange.ThrowIfInvalidNextIndex(nextIndex, entityName);

    /// <summary>
    /// Refuses a registration after <see cref="Build"/>. The built stack shares the dispatch tables with this builder and copied
    /// every other list, so a late registration would either mutate a table that parse threads read (a data race) or be ignored.
    /// The guard catches sequential misuse; the builder stays single-threaded by contract (see the type doc).
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was already called.</exception>
    private void _ThrowIfBuilt()
    {
        if (_Built)
        {
            throw new InvalidOperationException(
                "The StackBuilder was already built; a registration now would change or miss the live Stack. Register before Build, or create a new builder.");
        }
    }

    /// <summary>Refuses a protocol id that this builder did not issue.</summary>
    /// <param name="protocolId">The id a registration refers to.</param>
    /// <param name="what">The registration, as the start of the message (for example "Post-parser").</param>
    /// <exception cref="NotFoundRegistrationException">The id is invalid or not registered with this builder.</exception>
    private void _ThrowIfUnknownProtocol(ProtocolId protocolId, string what)
    {
        if (!_IsValidIndex(protocolId.Value, _Protocols.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"{what} references unknown protocol ID {protocolId.Value}"));
        }
    }

    /// <summary>Throws <see cref="InvalidNameRegistrationException"/> when the name is not a valid dot-separated C-style identifier.</summary>
    /// <param name="name">The protocol, field, group, or table name to check.</param>
    /// <exception cref="InvalidNameRegistrationException"><paramref name="name"/> is not a valid name.</exception>
    private static void _ValidateName(string name)
    {
        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameRegistrationException.For(name);
        }
    }

    /// <summary>Throws <see cref="InvalidUiNameRegistrationException"/> when the UI name is empty or contains control characters.</summary>
    /// <param name="uiName">The display name to check.</param>
    /// <exception cref="InvalidUiNameRegistrationException"><paramref name="uiName"/> is empty or has control characters.</exception>
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
        _ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(protocol);

        // Validate everything before touching any list, so a rejected registration leaves the builder unchanged. The ID is
        // the dense list index, which is what lets the built Stack resolve it by array indexing.
        string name = protocol.Name;
        _ValidateName(name);
        _ValidateUiName(protocol.UiName);

        // One name, one registration: protocols, fields and aliases share one namespace for conflicts, and the first
        // registration keeps the name. Rejected: three independent namespaces (the filter resolves protocol → field → alias, so
        // the later of two holders silently lost or won depending on its kind). A field cannot be this protocol's own container
        // yet, because a field needs the protocol id, so any field with this name belongs to someone else.
        if (_ProtocolNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Protocol);
        }

        if (_FieldNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Field);
        }

        if (_FieldAliasGroupNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.FieldAliasGroup);
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
        // Both arguments are checked before the registration: a null callback found afterwards would leave the protocol
        // registered although the call failed. The typed overload hands the concrete protocol instance back to the callback,
        // so the caller needs no cast and (with a static lambda) no closure to register the protocol's fields and tables
        // right after its ID exists.
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(callback);
        ProtocolId id = RegisterProtocol(protocol);
        callback(this, id, protocol);
        return id;
    }

    #endregion

    #region Field Registration

    /// <inheritdoc/>
    public FieldId RegisterField(
        ProtocolId protocolId, string name, string uiName, FieldType fieldType, string? description = null) =>
        _RegisterFieldCore(protocolId, name, uiName, fieldType, description, indexGroup: null);

    /// <summary>
    /// Registers a field, with its index group when <paramref name="indexGroup"/> is given. Validates everything first, commits
    /// the field together with its group, and only then runs the subscribers, so no subscriber sees a field without its group.
    /// </summary>
    /// <param name="protocolId">The owning protocol, or <see cref="ProtocolId.Invalid"/> for "no owner".</param>
    /// <param name="name">The unique dotted field name.</param>
    /// <param name="uiName">The display name.</param>
    /// <param name="fieldType">The value type of the field.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="indexGroup">The index group name, or <see langword="null"/> for a field without a group.</param>
    /// <returns>The id of the new field.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> was already called.</exception>
    /// <exception cref="NotFoundRegistrationException">The owning protocol is not registered.</exception>
    /// <exception cref="DuplicateNameRegistrationException">The name is already taken in the shared namespace.</exception>
    private FieldId _RegisterFieldCore(
        ProtocolId protocolId, string name, string uiName, FieldType fieldType, string? description, string? indexGroup)
    {
        _ThrowIfBuilt();

        // Field names share one namespace across all protocols (by convention prefixed with the protocol name, for example
        // "packet.error"), and the field ID is the dense list index. Validation comes first so a rejected registration
        // changes nothing. The group name is validated here too, so a bad group name cannot leave behind a registered field
        // that has no group.
        _ValidateName(name);
        _ValidateUiName(uiName);
        if (indexGroup is not null)
        {
            _ValidateName(indexGroup);
        }

        // The owning protocol must exist, or be the documented "no owner" sentinel ProtocolId.Invalid (IStackBuilder). Any other
        // unknown ID (for example one that came from another builder) would be stored as it is and later break everything
        // that resolves a field's protocol, such as the alias group membership check.
        if (protocolId != ProtocolId.Invalid && !_IsValidIndex(protocolId.Value, _Protocols.Count))
        {
            throw NotFoundRegistrationException.For(
                Invariant($"Field '{name}' references unknown protocol ID {protocolId.Value}"));
        }

        if (_FieldNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Field);
        }

        if (_FieldAliasGroupNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.FieldAliasGroup);
        }

        // The container-field convention: a protocol's own field may carry the protocol's name (protocol "someip", field
        // "someip"); the filter then resolves the name to the protocol, which is the intent. Any other owner would make the
        // field unreachable, so it is refused.
        if (_ProtocolNameMap.TryGetValue(name, out ProtocolId holder) && holder != protocolId)
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Protocol);
        }

        // Commit. The group is resolved (or created) before the FieldInfo exists, so the stored info is complete from the start
        // and no subscriber below can observe a field without its group.
        _GuardIndexAllocation(_Fields.Count, "field");
        IndexGroupId? groupId = null;
        if (indexGroup is not null)
        {
            groupId = GetOrCreateIndexGroup(indexGroup);
        }

        FieldId id = new(_Fields.Count);
        FieldInfo info = new(id, protocolId, name, uiName, fieldType, description, groupId);
        _Fields.Add(info);
        _FieldNameMap[name] = id;
        _FieldsSnapshot = null;

        // Subscribers run after the state is committed; an AggregateException from a subscriber leaves the field registered.
        _InvokeDeferredCallbacks(_DeferredField, name, id, "Field");

        return id;
    }

    #endregion

    #region Field Alias Group Registration

    /// <inheritdoc/>
    public FieldAliasGroupId RegisterFieldAliasGroup(
        ProtocolId protocolId, string name, string? description, FieldId[] fieldIds)
    {
        _ThrowIfBuilt();
        // An alias shares the conflict namespace of protocols and fields (see RegisterProtocol); protocol tables keep their own.
        _ValidateName(name);
        if (_FieldAliasGroupNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.FieldAliasGroup);
        }

        if (_ProtocolNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Protocol);
        }

        if (_FieldNameMap.ContainsKey(name))
        {
            throw DuplicateNameRegistrationException.For(name, RegistrationHolderKind.Field);
        }

        ArgumentNullException.ThrowIfNull(fieldIds);
        if (fieldIds.Length == 0)
        {
            throw new ArgumentException(
                Invariant($"Field alias group '{name}' requires at least one member field ID."),
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
                    Invariant($"Field alias group '{name}' references unknown field ID {memberId.Value}"));
            }

            // Member field must belong to the protocol that owns this alias group.
            if (_Fields[memberId.Value].ProtocolId != protocolId)
            {
                FieldInfo member = _Fields[memberId.Value];
                throw new ArgumentException(
                    string.Concat(
                        Invariant($"Field alias group '{name}' references field '{member.Name}' (ID {memberId.Value}) "),
                        Invariant($"which belongs to protocol '{member.ProtocolId.Value}', not the owning protocol '{protocolId.Value}'.")),
                    nameof(fieldIds));
            }

            // Duplicate member IDs are rejected; mixed FieldType values are intentionally allowed. The pairwise scan is
            // O(n^2) but runs once per registration over a small member list, so a set would only add allocation.
            for (int j = 0; j < i; j++)
            {
                if (members[j] == memberId)
                {
                    throw new ArgumentException(
                        Invariant($"Field alias group '{name}' contains duplicate member field ID {memberId.Value}."),
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

    #region Field Registration in Index Group

    /// <inheritdoc/>
    public FieldId RegisterFieldInGroup(
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string indexGroup,
        string? description = null)
    {
        // Groups are created on first use and shared by name. Fields that always appear together are registered into one group
        // so that they share a single presence bitmap in the cross-packet index. The group is resolved inside the core
        // registration, before the subscribers of the field run.
        ArgumentNullException.ThrowIfNull(indexGroup);
        return _RegisterFieldCore(protocolId, name, uiName, fieldType, description, indexGroup);
    }

    #endregion

    #region Index Group Registration

    /// <inheritdoc/>
    public IndexGroupId GetOrCreateIndexGroup(string name)
    {
        _ThrowIfBuilt();
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
        _ThrowIfBuilt();
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
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Parser registration");
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table ID {tableId.Value} not found"));
        }
        _ProtocolTables[tableId.Value].RegisterU64(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInU64TableByName(string tableName, ulong key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table '{tableName}' not found"));
        }
        RegisterParserInU64Table(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInStringTable(ProtocolTableId tableId, string key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Parser registration");
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table ID {tableId.Value} not found"));
        }
        _ProtocolTables[tableId.Value].RegisterString(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInStringTableByName(string tableName, string key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table '{tableName}' not found"));
        }
        RegisterParserInStringTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBytesTable(ProtocolTableId tableId, BytesKey key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Parser registration");
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table ID {tableId.Value} not found"));
        }
        _ProtocolTables[tableId.Value].RegisterBytes(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBytesTableByName(string tableName, BytesKey key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table '{tableName}' not found"));
        }
        RegisterParserInBytesTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBoolTable(ProtocolTableId tableId, bool key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Parser registration");
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table ID {tableId.Value} not found"));
        }
        _ProtocolTables[tableId.Value].RegisterBool(key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInBoolTableByName(string tableName, bool key, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table '{tableName}' not found"));
        }
        RegisterParserInBoolTable(id, key, protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInAnyTable(ProtocolTableId tableId, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Parser registration");
        if (!_IsValidIndex(tableId.Value, _ProtocolTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table ID {tableId.Value} not found"));
        }
        _ProtocolTables[tableId.Value].RegisterAny(protocolId);
    }

    /// <inheritdoc/>
    public void RegisterParserInAnyTableByName(string tableName, ProtocolId protocolId)
    {
        _ThrowIfBuilt();
        if (!_ProtocolTableNameMap.TryGetValue(tableName, out ProtocolTableId id))
        {
            throw NotFoundRegistrationException.For(Invariant($"Protocol table '{tableName}' not found"));
        }
        RegisterParserInAnyTable(id, protocolId);
    }

    #endregion

    #region Post-Parser Registration

    /// <inheritdoc/>
    public PostParserId RegisterPostParser(
        ProtocolId protocolId, int priority = 0, string? description = null)
    {
        _ThrowIfBuilt();
        _ThrowIfUnknownProtocol(protocolId, "Post-parser");
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
            return cmp != 0
                ? cmp
                : a.Id.Value.CompareTo(b.Id.Value);
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
        _ThrowIfBuilt();
        _ValidateName(name);
        _ValidateUiName(uiName);

        // Same rule as RegisterField: ProtocolId.Invalid is the documented "no owner"; any other unknown id is refused.
        if (owningProtocolId != ProtocolId.Invalid)
        {
            _ThrowIfUnknownProtocol(owningProtocolId, Invariant($"Heuristic table '{name}'"));
        }

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
        _ThrowIfBuilt();
        if (!_IsValidIndex(tableId.Value, _HeuristicTables.Count))
        {
            throw NotFoundRegistrationException.For(Invariant($"Heuristic table ID {tableId.Value} not found"));
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
        _ThrowIfBuilt();
        _ValidateName(name);
        ArgumentNullException.ThrowIfNull(callback);
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
        _ThrowIfBuilt();
        _ValidateName(name);
        ArgumentNullException.ThrowIfNull(callback);
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
        _ThrowIfBuilt();
        _ValidateName(name);
        ArgumentNullException.ThrowIfNull(callback);
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

    /// <summary>
    /// Invokes deferred registration callbacks for <paramref name="name"/>, collecting every
    /// exception so all subscribers run before surfacing an <see cref="AggregateException"/>.
    /// The entity is already registered when this runs; an exception reports failing subscribers but does not undo the
    /// registration.
    /// </summary>
    /// <typeparam name="T">The id type the callbacks receive.</typeparam>
    /// <param name="deferred">The waiting callbacks by name; the entry of <paramref name="name"/> is removed.</param>
    /// <param name="name">The name that was just registered.</param>
    /// <param name="id">The id issued for <paramref name="name"/>, passed to every callback.</param>
    /// <param name="callbackKind">The entity kind for the error message (for example "Protocol").</param>
    /// <exception cref="AggregateException">One or more callbacks threw; every callback ran first.</exception>
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
                Invariant($"One or more When{callbackKind}Registered callbacks failed for '{name}'."),
                errors);
        }
    }

    #endregion

    #region Stream Reassembly Config

    /// <inheritdoc/>
    public void RegisterStreamReassemblyConfig(ProtocolId protocolId, StreamReassemblyConfig config)
    {
        _ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(config);
        _ThrowIfUnknownProtocol(protocolId, "Stream reassembly config");

        // A PDU larger than the buffer can never complete: the buffer refuses its bytes first. Refuse the pair here, once.
        if (config.MaxBufferSize < config.MaxPduSize)
        {
            throw new ArgumentException(
                Invariant($"MaxBufferSize ({config.MaxBufferSize}) must be at least MaxPduSize ({config.MaxPduSize})."), nameof(config));
        }

        if (!_ReassemblyConfigs.TryAdd(protocolId, config))
        {
            // The holder of the duplicate is the protocol; its name is what the exception type reports.
            throw DuplicateNameRegistrationException.For(_Protocols[protocolId.Value].Name);
        }
    }

    /// <inheritdoc/>
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) =>
        _ReassemblyConfigs.GetValueOrDefault(protocolId);

    #endregion

    #region Dispatch Table Queries

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
    public bool TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data, out ProtocolId protocolId)
    {
        if (_IsValidIndex(tableId.Value, _HeuristicTables.Count))
        {
            return _HeuristicTables[tableId.Value].TryMatch(data, out protocolId);
        }

        protocolId = ProtocolId.Invalid;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// During the build phase, parse delegates are not yet bound, so this method
    /// always returns <see langword="null"/>. It is only meaningful on the built
    /// <see cref="Stack"/>.
    /// </remarks>
    public ParseDelegate? ResolveParseDelegate(ProtocolId id) => null;

    #endregion

    #region Build

    /// <summary>
    /// Freezes the builder into an immutable <see cref="Stack"/>.
    /// Protocol startup exceptions from <see cref="IProtocol.OnStart(Stack)"/> are collected on
    /// the returned stack instead of being thrown. Callers should inspect
    /// <see cref="Stack.BuildDiagnostics"/> after build.
    /// <para>
    /// Call it once per builder. The returned stack shares the protocol instances, the (mutable) dispatch tables, the
    /// settings manager, and the frame interface registry with this builder, and a second call would run
    /// <see cref="IProtocol.OnStart(Stack)"/> again on the same protocol instances. Every registration member throws
    /// <see cref="InvalidOperationException"/> after Build, because a late registration would mutate a dispatch table that
    /// parse threads read. A second call throws <see cref="InvalidOperationException"/> and starts nothing.
    /// </para>
    /// </summary>
    /// <returns>
    /// The started stack. Startup failures and unresolved deferred callbacks are on <see cref="Stack.BuildDiagnostics"/>.
    /// </returns>
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

        // The arrays are fresh copies, but the table objects inside them are the builder's own (mutable) instances: every
        // registration member refuses to run after Build (_ThrowIfBuilt), so nothing writes into them while the stack reads.
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
            return cmp != 0
                ? cmp
                : a.Id.Value.CompareTo(b.Id.Value);
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
            fieldAliasGroups, fieldAliasGroupNameMap,
            _Limits);

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
    /// <typeparam name="T">The id type the callbacks would have received.</typeparam>
    /// <param name="deferred">The callbacks that are still waiting, by name.</param>
    /// <param name="entityKind">The kind of entity named in each warning.</param>
    /// <param name="diagnostics">The list that receives one warning per waiting name.</param>
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
