// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Immutable protocol stack registry. Created by <see cref="StackBuilder.Build"/>.
/// <para>
/// Provides O(1) protocol/field/table lookup by strongly-typed ID
/// (direct array indexing) and O(1) name-to-ID resolution via
/// frozen dictionaries.
/// </para>
/// <para>
/// After build, callers should inspect <see cref="BuildDiagnostics"/> for any protocol
/// startup exceptions captured from <see cref="NetworkInspector.Core.Protocols.IProtocol.OnStart(Stack)"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> after <see cref="StackBuilder.Build"/> returns, all read-only members
/// (lookups, enumeration, <see cref="BuildDiagnostics"/>, <see cref="FrameInterfaceRegistry"/>) are
/// safe to call concurrently from any number of threads. Both <see cref="Shutdown"/> and
/// <see cref="Dispose"/> are thread-safe, race-free, and idempotent: a shared atomic shutdown
/// latch (<see cref="Interlocked.Exchange(ref int, int)"/>) guarantees that each protocol's
/// <see cref="NetworkInspector.Core.Protocols.IProtocol.OnShutdown"/> is invoked exactly once
/// regardless of whether the caller calls <see cref="Shutdown"/> explicitly before
/// <see cref="Dispose"/> or relies entirely on <see cref="Dispose"/> for teardown.
/// </para>
/// </summary>
public sealed class Stack : IStack, IDisposable
{
    #region Fields

    // Frozen arrays — direct indexing by ID value
    private readonly IProtocol[] _ProtocolInstances;
    private readonly ParseDelegate[] _ParseDelegates;
    private readonly ProtocolTable[] _ProtocolTables;
    private readonly HeuristicProtocolTable[] _HeuristicTables;
    private readonly SettingsManager _SettingsManager;

    // Frozen name→ID maps (FrozenDictionary for O(1) string lookups)
    private readonly FrozenDictionary<string, ProtocolId> _ProtocolNameMap;
    private readonly FrozenDictionary<string, FieldId> _FieldNameMap;
    private readonly FrozenDictionary<string, ProtocolTableId> _ProtocolTableNameMap;
    private readonly FrozenDictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap;
    private readonly FrozenDictionary<string, IndexGroupId> _IndexGroupNameMap;
    private readonly FrozenDictionary<string, FieldAliasGroupId> _FieldAliasGroupNameMap;

    // Built-in IDs
    /// <summary>Frozen reassembly configs keyed by protocol ID.</summary>
    private readonly FrozenDictionary<ProtocolId, StreamReassemblyConfig> _ReassemblyConfigs;

    /// <summary>
    /// All non-fatal diagnostics collected during <see cref="StackBuilder.Build"/>.
    /// Published once by <see cref="SetBuildDiagnostics"/> at the end of Build, then
    /// read concurrently by inspectors. Declared <see langword="volatile"/> so the array
    /// replacement is a release store and the property getter an acquire load.
    /// Kept as a field: auto-properties cannot be <see langword="volatile"/>, and
    /// <see cref="BuildDiagnostics"/> exposes <see cref="ReadOnlyMemory{T}"/> rather than the array.
    /// </summary>
    private volatile BuildDiagnostic[] _BuildDiagnostics = [];

    /// <summary>
    /// Exceptions captured during the implicit <see cref="Shutdown"/> performed by
    /// <see cref="Dispose"/>. Empty when no shutdown error occurred or when callers ran
    /// <see cref="Shutdown"/> explicitly. Same publication pattern as
    /// <see cref="_BuildDiagnostics"/>.
    /// </summary>
    private volatile Exception[] _ShutdownDiagnostics = [];

    /// <summary>
    /// Atomic shutdown latch (0 = live, 1 = shut down). Mutated only via
    /// <see cref="Interlocked.Exchange(ref int, int)"/> so <see cref="IProtocol.OnShutdown"/>
    /// runs at most once. Must remain a field: <c>Interlocked</c> needs <c>ref</c>.
    /// </summary>
    private volatile int _ShutdownFlag;

    /// <summary>
    /// Atomic dispose latch (0 = live, 1 = disposed). Mutated only via
    /// <see cref="Interlocked.Exchange(ref int, int)"/>. Must remain a field.
    /// </summary>
    private volatile int _DisposedFlag;

    /// <summary>
    /// Highest packet id whose first parse on this stack has completed; <c>-1</c> until id 0 is parsed.
    /// First parses must be dense <c>0, 1, 2, …</c>. A later parse of an already-completed id is a
    /// replay. Jumping over an id (for example 0 then 5) is a caller contract violation.
    /// <see cref="PacketId.Value"/> is an <see cref="int"/>, so this stays <see langword="volatile"/>
    /// <see cref="int"/> (plain volatile read/write). First-parse of a given id is serialized by the
    /// caller; this field only publishes the completed watermark to re-parse threads.
    /// </summary>
    private volatile int _ParseWatermark = -1;

    #endregion

    #region Constructors

    /// <summary>Creates a finalized, immutable protocol stack from builder-prepared data.</summary>
    internal Stack(
        ProtocolInfo[] protocols,
        IProtocol[] protocolInstances,
        ParseDelegate[] parseDelegates,
        FrozenDictionary<string, ProtocolId> protocolNameMap,
        FieldInfo[] fields,
        FrozenDictionary<string, FieldId> fieldNameMap,
        ProtocolTable[] protocolTables,
        ProtocolTableInfo[] protocolTableInfos,
        FrozenDictionary<string, ProtocolTableId> protocolTableNameMap,
        HeuristicProtocolTable[] heuristicTables,
        HeuristicProtocolTableInfo[] heuristicTableInfos,
        FrozenDictionary<string, HeuristicProtocolTableId> heuristicTableNameMap,
        PostParserInfo[] postParsers,
        SettingsManager settingsManager,
        FieldId rootFieldId,
        FieldId packetErrorFieldId,
        FieldId packetChoiceFieldId,
        ProtocolId packetProtocolId,
        ProtocolId frameProtocolId,
        IndexGroupInfo[] indexGroups,
        FrozenDictionary<string, IndexGroupId> indexGroupNameMap,
        FrameInterfaceRegistry frameInterfaceRegistry,
        bool includeExceptionStackTrace,
        FrozenDictionary<ProtocolId, StreamReassemblyConfig> reassemblyConfigs,
        FieldAliasGroupInfo[] fieldAliasGroups,
        FrozenDictionary<string, FieldAliasGroupId> fieldAliasGroupNameMap)
    {
        Protocols = protocols;
        _ProtocolInstances = protocolInstances;
        _ParseDelegates = parseDelegates;
        _ProtocolNameMap = protocolNameMap;
        Fields = fields;
        _FieldNameMap = fieldNameMap;
        _ProtocolTables = protocolTables;
        ProtocolTableInfos = protocolTableInfos;
        _ProtocolTableNameMap = protocolTableNameMap;
        _HeuristicTables = heuristicTables;
        HeuristicProtocolTableInfos = heuristicTableInfos;
        _HeuristicTableNameMap = heuristicTableNameMap;
        PostParsers = postParsers;
        _SettingsManager = settingsManager;
        RootFieldId = rootFieldId;
        PacketErrorFieldId = packetErrorFieldId;
        PacketChoiceFieldId = packetChoiceFieldId;
        PacketProtocolId = packetProtocolId;
        FrameProtocolId = frameProtocolId;
        IndexGroups = indexGroups;
        _IndexGroupNameMap = indexGroupNameMap;
        FrameInterfaceRegistry = frameInterfaceRegistry;
        FieldAliasGroups = fieldAliasGroups;
        _FieldAliasGroupNameMap = fieldAliasGroupNameMap;
        IncludeExceptionStackTrace = includeExceptionStackTrace;
        _ReassemblyConfigs = reassemblyConfigs;
    }

    #endregion

    #region Index Validation

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    #endregion

    #region Built-in IDs

    /// <summary>The root field ID (always index 0 in every packet).</summary>
    internal FieldId RootFieldId { get; }
    /// <summary>Field ID for error annotations.</summary>
    internal FieldId PacketErrorFieldId { get; }
    /// <summary>Field ID for the packet.choice container used when multiple protocols match a dispatch key.</summary>
    internal FieldId PacketChoiceFieldId { get; }
    /// <summary>The top-level protocol for packet dispatch.</summary>
    internal ProtocolId PacketProtocolId { get; }
    /// <summary>The frame protocol auto-discovered by name "frame" during build. Used as default dispatch target by PacketProtocol.</summary>
    internal ProtocolId FrameProtocolId { get; }

    /// <inheritdoc/>
    public FrameInterfaceRegistry FrameInterfaceRegistry { get; }

    /// <summary>
    /// All non-fatal diagnostics collected during <see cref="StackBuilder.Build"/>.
    /// Combines <see cref="BuildCallbackWarning"/> entries for unresolved deferred callbacks
    /// and <see cref="BuildStartupError"/> entries for protocol startup exceptions.
    /// An empty memory means the stack was built without any issues.
    /// </summary>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => _BuildDiagnostics;

    /// <summary>
    /// Exceptions captured during the implicit <see cref="Shutdown"/> that runs from
    /// <see cref="Dispose"/>. <see cref="IDisposable.Dispose"/> must not throw (CA1065),
    /// so shutdown errors that escape protocol <see cref="NetworkInspector.Core.Protocols.IProtocol.OnShutdown"/>
    /// implementations are captured here for inspection by the disposer. Empty when
    /// <see cref="Dispose"/> has not yet been called, when shutdown completed cleanly, or
    /// when callers ran <see cref="Shutdown"/> explicitly before disposal (in which case the
    /// thrown <see cref="AggregateException"/> is the diagnostic surface).
    /// </summary>
    public ReadOnlyMemory<Exception> ShutdownDiagnostics => _ShutdownDiagnostics;

    /// <inheritdoc/>
    public bool IncludeExceptionStackTrace { get; }

    /// <summary>Sets all build diagnostics collected during <see cref="StackBuilder.Build"/>.</summary>
    internal void SetBuildDiagnostics(BuildDiagnostic[] diagnostics) =>
        _BuildDiagnostics = diagnostics;

    #endregion

    #region Parse sequence

    /// <summary>
    /// Classifies this <see cref="Packet.ParseFrame(PacketId, Stack, Frame)"/> call as a first parse or a replay and
    /// rejects jumps. First parses on a stack must use dense ids <c>0, 1, 2, …</c>.
    /// Returns <see langword="true"/> when <paramref name="id"/> was already first-parsed
    /// (<c>id ≤ watermark</c>) and must replay; <see langword="false"/> when this is the next
    /// first parse (<c>id == watermark + 1</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="id"/> is greater than the next expected first-parse id.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ObserveParse(PacketId id)
    {
        int value = id.Value;
        int current = _ParseWatermark;
        if (value <= current)
        {
            return true;
        }

        if (value == current + 1)
        {
            return false;
        }

        _ThrowParseIdGap(value, current);
        return false;
    }

    /// <summary>
    /// Marks <paramref name="id"/> as having completed its first parse. Must be called from
    /// <see langword="finally"/> after a non-replay <see cref="ObserveParse"/> so even a failed
    /// parse closes the id — a later call with the same id is then a replay.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompleteFirstParse(PacketId id) =>
        _ParseWatermark = id.Value;

    /// <summary>Cold-path helper: first-parse ids on a stack must be dense starting at 0.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _ThrowParseIdGap(int actual, int current)
    {
        int expected = current + 1;
        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "First parse packet ids on a Stack must be dense starting at 0 (next expected {0}, got {1}). " +
                "A jump leaves a hole that later parses would treat as a replay. Re-parse an already " +
                "first-parsed id, or parse the next id in sequence.",
                expected,
                actual));
    }

    #endregion

    #region Protocol Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolInfo? GetProtocol(ProtocolId id)
    {
        if (_IsValidIndex(id.Value, Protocols.Length))
        {
            return Protocols.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolId? GetProtocolId(string name)
    {
        if (_ProtocolNameMap.TryGetValue(name, out ProtocolId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolInfo> Protocols { get; }

    /// <inheritdoc/>
    public int ProtocolCount => Protocols.Length;

    #endregion

    #region Field Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldInfo? GetField(FieldId id)
    {
        if (_IsValidIndex(id.Value, Fields.Length))
        {
            return Fields.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldId? GetFieldId(string name)
    {
        if (_FieldNameMap.TryGetValue(name, out FieldId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldInfo> Fields { get; }

    /// <inheritdoc/>
    public int FieldCount => Fields.Length;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId)
    {
        if (_IsValidIndex(fieldId.Value, Fields.Length))
        {
            IndexGroupId? indexGroup = Fields.Span[fieldId.Value].IndexGroup;
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldAliasGroupInfo? GetFieldAliasGroup(FieldAliasGroupId id)
    {
        if (_IsValidIndex(id.Value, FieldAliasGroups.Length))
        {
            return FieldAliasGroups.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldAliasGroupId? GetFieldAliasGroupId(string name)
    {
        if (_FieldAliasGroupNameMap.TryGetValue(name, out FieldAliasGroupId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldAliasGroupInfo> FieldAliasGroups { get; }

    /// <inheritdoc/>
    public int FieldAliasGroupCount => FieldAliasGroups.Length;

    #endregion

    #region Index Group Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupInfo? GetIndexGroup(IndexGroupId id)
    {
        if (_IsValidIndex(id.Value, IndexGroups.Length))
        {
            return IndexGroups.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupId? GetIndexGroupId(string name)
    {
        if (_IndexGroupNameMap.TryGetValue(name, out IndexGroupId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<IndexGroupInfo> IndexGroups { get; }

    /// <inheritdoc/>
    public int IndexGroupCount => IndexGroups.Length;

    #endregion

    #region Protocol Table Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, ProtocolTableInfos.Length))
        {
            return ProtocolTableInfos.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolTableId? GetProtocolTableId(string name)
    {
        if (_ProtocolTableNameMap.TryGetValue(name, out ProtocolTableId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos { get; }

    /// <inheritdoc/>
    public int ProtocolTableCount => ProtocolTableInfos.Length;

    #endregion

    #region Post-Parser Access

    /// <inheritdoc/>
    public ReadOnlyMemory<PostParserInfo> PostParsers { get; }

    /// <inheritdoc/>
    public int PostParserCount => PostParsers.Length;

    #endregion

    #region Heuristic Table Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, HeuristicProtocolTableInfos.Length))
        {
            return HeuristicProtocolTableInfos.Span[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name)
    {
        if (_HeuristicTableNameMap.TryGetValue(name, out HeuristicProtocolTableId id))
        {
            return id;
        }
        return null;
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos { get; }

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => HeuristicProtocolTableInfos.Length;

    #endregion

    #region Settings Access

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView Settings => _SettingsManager.ReadOnly;

    #endregion

    #region Stream Reassembly

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) =>
        _ReassemblyConfigs.GetValueOrDefault(protocolId);

    #endregion

    #region Dispatch Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolTable? GetProtocolTable(ProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, _ProtocolTables.Length))
        {
            return _ProtocolTables[id.Value];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal HeuristicProtocolTable? GetHeuristicProtocolTable(HeuristicProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, _HeuristicTables.Length))
        {
            return _HeuristicTables[id.Value];
        }
        return null;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ProtocolId> GetProtocolsFromU64ProtocolTable(ProtocolTableId tableId, ulong key)
    {
        ProtocolTable? table = GetProtocolTable(tableId);
        if (table is not null)
        {
            return table.GetAllU64(key);
        }
        return [];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ProtocolId> GetProtocolsFromStringProtocolTable(ProtocolTableId tableId, string key)
    {
        ProtocolTable? table = GetProtocolTable(tableId);
        if (table is not null)
        {
            return table.GetAllString(key);
        }
        return [];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBytesProtocolTable(ProtocolTableId tableId, BytesKey key)
    {
        ProtocolTable? table = GetProtocolTable(tableId);
        if (table is not null)
        {
            return table.GetAllBytes(key);
        }
        return [];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBoolProtocolTable(ProtocolTableId tableId, bool key)
    {
        ProtocolTable? table = GetProtocolTable(tableId);
        if (table is not null)
        {
            return table.GetAllBool(key);
        }
        return [];
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ProtocolId> GetProtocolsFromAnyProtocolTable(ProtocolTableId tableId)
    {
        ProtocolTable? table = GetProtocolTable(tableId);
        if (table is not null)
        {
            return table.GetAllAny();
        }
        return [];
    }

    /// <summary>
    /// Runs all registered heuristic parsers in the given heuristic dispatch table against
    /// <paramref name="data"/> and returns the <see cref="ProtocolId"/> of the first match,
    /// or <see langword="null"/> if the table does not exist or no parser matches.
    /// <para>
    /// Intended for per-packet use in protocols that need to cache the detected protocol ID
    /// on connection state before dispatching — for example TCP's per-connection heuristic
    /// detection. For single-shot heuristic dispatch use
    /// <see cref="MutField.TryCallHeuristicProtocol"/>.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolId? TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data)
        => GetHeuristicProtocolTable(tableId)?.TryMatch(data);

    #endregion

    #region Table Entry Iterators

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? GetU64TableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterU64Entries();

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? GetStringTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterStringEntries();

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? GetBytesTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterBytesEntries();

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? GetBoolTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterBoolEntries();

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolId>? GetAnyTableProtocolIds(ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.GetAnyProtocolIds();

    #endregion

    #region Protocol Dispatch

    /// <summary>
    /// Calls a protocol's Parse method via a pre-bound delegate. Direct array
    /// indexing plus delegate invocation — no interface vtable dispatch.
    /// Validates the id and invokes the bound <see cref="IProtocol.Parse"/> after
    /// <see cref="ParseContext.WithSelfProtocol"/>. Prefer this (or
    /// <see cref="MutField.CallProtocol"/>) so invalid ids return
    /// <see cref="ParseError"/> instead of skipping the invoke.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult CallProtocol(
        ProtocolId protocolId, in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!_IsValidIndex(protocolId.Value, _ParseDelegates.Length))
        {
            return ParseError.Custom("stack", $"Invalid protocol ID: {protocolId.Value}");
        }

        ParseContext contextWithSelf = context.WithSelfProtocol(protocolId);
        return _ParseDelegates[protocolId.Value](in parentField, data, in contextWithSelf);
    }

    /// <summary>
    /// Resolves a <see cref="ProtocolId"/> to its <see cref="IProtocol"/> instance.
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build dispatch caches
    /// that store direct <see cref="IProtocol"/> references for zero-indirection dispatch.
    /// <para>
    /// <b>Do not call per packet.</b> The returned reference is stable for the lifetime of
    /// the stack.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal IProtocol? ResolveProtocol(ProtocolId id)
    {
        if (_IsValidIndex(id.Value, _ProtocolInstances.Length))
        {
            return _ProtocolInstances[id.Value];
        }
        return null;
    }

    /// <summary>
    /// Resolves a <see cref="ProtocolId"/> to its concrete <see cref="ParseDelegate"/>.
    /// Prefer <see cref="MutField.CallProtocol"/> so invalid ids return
    /// <see cref="ParseError"/> instead of skipping the invoke.
    /// <para>
    /// <b>Do not call per packet.</b> The returned delegate is stable for the lifetime of
    /// the stack.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ParseDelegate? ResolveParseDelegate(ProtocolId id)
    {
        if (_IsValidIndex(id.Value, _ParseDelegates.Length))
        {
            return _ParseDelegates[id.Value];
        }
        return null;
    }

    #endregion


    #region Lifecycle

    /// <summary>
    /// Notifies all protocols of shutdown.
    /// Every protocol's <see cref="IProtocol.OnShutdown"/> is called even if earlier
    /// protocols throw. All exceptions are collected and returned as an
    /// <see cref="AggregateException"/> so the caller can inspect them.
    /// <para>
    /// <b>Idempotent:</b> if called more than once (or after <see cref="Dispose"/>), subsequent
    /// calls return immediately without invoking any protocol. The first caller receives any
    /// thrown <see cref="AggregateException"/>; subsequent callers receive nothing.
    /// </para>
    /// </summary>
    /// <exception cref="AggregateException">Thrown when one or more protocols threw during shutdown.</exception>
    public void Shutdown()
    {
        // Once-gate: ensure OnShutdown runs at most once across concurrent Shutdown/Dispose calls.
        if (Interlocked.Exchange(ref _ShutdownFlag, 1) != 0)
        {
            return;
        }

        List<Exception>? exceptions = null;

        for (int i = 0; i < _ProtocolInstances.Length; i++)
        {
            try
            {
                _ProtocolInstances[i].OnShutdown(this);
            }
            catch (Exception ex)
            {
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        if (exceptions is not null)
        {
            throw new AggregateException("One or more protocols failed during shutdown.", exceptions);
        }
    }

    /// <summary>
    /// Disposes the stack, shutting down all protocols and releasing resources.
    /// Shutdown exceptions are caught (CA1065 forbids <see cref="IDisposable.Dispose"/> from
    /// throwing) and republished via <see cref="ShutdownDiagnostics"/> for inspection.
    /// Call <see cref="Shutdown"/> explicitly before <see cref="Dispose"/> to receive
    /// shutdown errors as a thrown <see cref="AggregateException"/> instead.
    /// </summary>
    public void Dispose()
    {
        // Atomic gate — two concurrent Dispose callers must not both run Shutdown
        // and dispose _SettingsManager twice (ObjectDisposedException on the inner
        // ReaderWriterLockSlim). Interlocked.Exchange returns the previous value.
        if (Interlocked.Exchange(ref _DisposedFlag, 1) != 0)
        {
            return;
        }

        // Shutdown must not prevent resource cleanup; capture errors per CA1065.
        try
        {
            Shutdown();
        }
        catch (AggregateException aggregate)
        {
            // Republish protocol shutdown errors via ShutdownDiagnostics so a Dispose
            // caller can inspect them without violating CA1065.
            Exception[] inner = new Exception[aggregate.InnerExceptions.Count];
            for (int i = 0; i < inner.Length; i++)
            {
                inner[i] = aggregate.InnerExceptions[i];
            }
            _ShutdownDiagnostics = inner;
        }
        catch (Exception ex)
        {
            // Defensive fallback: any non-AggregateException out of Shutdown is captured
            // identically. Catching base Exception is required by CA1065.
            _ShutdownDiagnostics = [ex];
        }

        _SettingsManager.DisposeResources();
    }

    #endregion
}
