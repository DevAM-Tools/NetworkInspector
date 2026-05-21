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
    private readonly ProtocolInfo[] _Protocols;
    private readonly IProtocol[] _ProtocolInstances;
    private readonly ParseDelegate[] _ParseDelegates;
    private readonly FieldInfo[] _Fields;
    private readonly ProtocolTable[] _ProtocolTables;
    private readonly ProtocolTableInfo[] _ProtocolTableInfos;
    private readonly HeuristicProtocolTable[] _HeuristicTables;
    private readonly HeuristicProtocolTableInfo[] _HeuristicTableInfos;
    private readonly PostParserInfo[] _PostParsers;
    private readonly IndexGroupInfo[] _IndexGroups;
    private readonly SettingsManager _SettingsManager;

    // Frozen name→ID maps (FrozenDictionary for O(1) string lookups)
    private readonly FrozenDictionary<string, ProtocolId> _ProtocolNameMap;
    private readonly FrozenDictionary<string, FieldId> _FieldNameMap;
    private readonly FrozenDictionary<string, ProtocolTableId> _ProtocolTableNameMap;
    private readonly FrozenDictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap;
    private readonly FrozenDictionary<string, IndexGroupId> _IndexGroupNameMap;

    // Built-in IDs
    private readonly FieldId _RootFieldId;
    private readonly FieldId _PacketErrorFieldId;
    private readonly FieldId _PacketChoiceFieldId;
    private readonly ProtocolId _PacketProtocolId;
    private readonly ProtocolId _FrameProtocolId;
    private readonly int _IndexGroupCount;

    /// <summary>Lock-free frame interface registry (shared with StackBuilder).</summary>
    private readonly FrameInterfaceRegistry _FrameInterfaceRegistry;

    /// <summary>When true, exception error messages include the full stack trace.</summary>
    private readonly bool _IncludeExceptionStackTrace;

    /// <summary>Frozen reassembly configs keyed by protocol ID.</summary>
    private readonly FrozenDictionary<ProtocolId, StreamReassemblyConfig> _ReassemblyConfigs;

    /// <summary>
    /// All non-fatal diagnostics collected during <see cref="StackBuilder.Build"/>.
    /// Published once by <see cref="SetBuildDiagnostics"/> at the end of Build, then
    /// read concurrently by inspectors. Access is via <see cref="System.Threading.Volatile"/>
    /// Read / Write to enforce the publication fence on every site.
    /// </summary>
    private BuildDiagnostic[] _BuildDiagnostics = [];

    /// <summary>
    /// Exceptions captured during the implicit <see cref="Shutdown"/> performed by
    /// <see cref="Dispose"/>. Empty when no shutdown error occurred or when callers ran
    /// <see cref="Shutdown"/> explicitly. Published with <see cref="Volatile.Write{T}(ref T, T)"/>
    /// and read with <see cref="System.Threading.Volatile"/>.
    /// </summary>
    private Exception[] _ShutdownDiagnostics = [];

    /// <summary>
    /// Atomic shutdown latch (0 = live, 1 = shut down). Set by <see cref="Shutdown"/> and checked
    /// by <see cref="Dispose"/> so protocol <c>OnShutdown</c> runs at most once regardless of
    /// whether the caller invokes <see cref="Shutdown"/> explicitly or relies on <see cref="Dispose"/>.
    /// Accessed exclusively via <see cref="Interlocked.Exchange(ref int, int)"/>.
    /// </summary>
    private int _ShutdownFlag;

    /// <summary>
    /// Atomic dispose latch (0 = live, 1 = disposed). Used with <see cref="Interlocked.Exchange(ref int, int)"/>
    /// inside <see cref="Dispose"/> so concurrent dispose attempts are race-free.
    /// </summary>
    private int _DisposedFlag;

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
        int indexGroupCount,
        IndexGroupInfo[] indexGroups,
        FrozenDictionary<string, IndexGroupId> indexGroupNameMap,
        FrameInterfaceRegistry frameInterfaceRegistry,
        bool includeExceptionStackTrace,
        FrozenDictionary<ProtocolId, StreamReassemblyConfig> reassemblyConfigs)
    {
        _Protocols = protocols;
        _ProtocolInstances = protocolInstances;
        _ParseDelegates = parseDelegates;
        _ProtocolNameMap = protocolNameMap;
        _Fields = fields;
        _FieldNameMap = fieldNameMap;
        _ProtocolTables = protocolTables;
        _ProtocolTableInfos = protocolTableInfos;
        _ProtocolTableNameMap = protocolTableNameMap;
        _HeuristicTables = heuristicTables;
        _HeuristicTableInfos = heuristicTableInfos;
        _HeuristicTableNameMap = heuristicTableNameMap;
        _PostParsers = postParsers;
        _SettingsManager = settingsManager;
        _RootFieldId = rootFieldId;
        _PacketErrorFieldId = packetErrorFieldId;
        _PacketChoiceFieldId = packetChoiceFieldId;
        _PacketProtocolId = packetProtocolId;
        _FrameProtocolId = frameProtocolId;
        _IndexGroupCount = indexGroupCount;
        _IndexGroups = indexGroups;
        _IndexGroupNameMap = indexGroupNameMap;
        _FrameInterfaceRegistry = frameInterfaceRegistry;
        _IncludeExceptionStackTrace = includeExceptionStackTrace;
        _ReassemblyConfigs = reassemblyConfigs;
    }

    #endregion

    #region Index Validation

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    #endregion

    #region Built-in IDs

    /// <summary>The root field ID (always index 0 in every packet).</summary>
    internal FieldId RootFieldId => _RootFieldId;
    /// <summary>Field ID for error annotations.</summary>
    internal FieldId PacketErrorFieldId => _PacketErrorFieldId;
    /// <summary>Field ID for the packet.choice container used when multiple protocols match a dispatch key.</summary>
    internal FieldId PacketChoiceFieldId => _PacketChoiceFieldId;
    /// <summary>The top-level protocol for packet dispatch.</summary>
    internal ProtocolId PacketProtocolId => _PacketProtocolId;
    /// <summary>The frame protocol auto-discovered by name "frame" during build. Used as default dispatch target by PacketProtocol.</summary>
    internal ProtocolId FrameProtocolId => _FrameProtocolId;

    /// <summary>The shared frame interface registry.</summary>
    public FrameInterfaceRegistry FrameInterfaceRegistry => _FrameInterfaceRegistry;

    /// <summary>
    /// All non-fatal diagnostics collected during <see cref="StackBuilder.Build"/>.
    /// Combines <see cref="BuildCallbackWarning"/> entries for unresolved deferred callbacks
    /// and <see cref="BuildStartupError"/> entries for protocol startup exceptions.
    /// An empty memory means the stack was built without any issues.
    /// </summary>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => Volatile.Read(ref _BuildDiagnostics);

    /// <summary>
    /// Exceptions captured during the implicit <see cref="Shutdown"/> that runs from
    /// <see cref="Dispose"/>. <see cref="IDisposable.Dispose"/> must not throw (CA1065),
    /// so shutdown errors that escape protocol <see cref="NetworkInspector.Core.Protocols.IProtocol.OnShutdown"/>
    /// implementations are captured here for inspection by the disposer. Empty when
    /// <see cref="Dispose"/> has not yet been called, when shutdown completed cleanly, or
    /// when callers ran <see cref="Shutdown"/> explicitly before disposal (in which case the
    /// thrown <see cref="AggregateException"/> is the diagnostic surface).
    /// </summary>
    public ReadOnlyMemory<Exception> ShutdownDiagnostics => Volatile.Read(ref _ShutdownDiagnostics);

    /// <summary>
    /// When <see langword="true"/>, parser exception error messages include the full exception stack trace.
    /// Configure via <see cref="StackBuilder.IncludeExceptionStackTrace"/>.
    /// </summary>
    public bool IncludeExceptionStackTrace => _IncludeExceptionStackTrace;

    /// <summary>Sets all build diagnostics collected during <see cref="StackBuilder.Build"/>.</summary>
    internal void SetBuildDiagnostics(BuildDiagnostic[] diagnostics) =>
        Volatile.Write(ref _BuildDiagnostics, diagnostics);

    #endregion

    #region Protocol Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolInfo? GetProtocol(ProtocolId id) =>
        IsValidIndex(id.Value, _Protocols.Length) ? _Protocols[id.Value] : null;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolId? GetProtocolId(string name) =>
        _ProtocolNameMap.TryGetValue(name, out ProtocolId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolInfo> Protocols => _Protocols;

    /// <inheritdoc/>
    public int ProtocolCount => _Protocols.Length;

    #endregion

    #region Field Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldInfo? GetField(FieldId id) =>
        IsValidIndex(id.Value, _Fields.Length) ? _Fields[id.Value] : null;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FieldId? GetFieldId(string name) =>
        _FieldNameMap.TryGetValue(name, out FieldId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldInfo> Fields => _Fields;

    /// <inheritdoc/>
    public int FieldCount => _Fields.Length;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId)
    {
        if (IsValidIndex(fieldId.Value, _Fields.Length))
        {
            return _Fields[fieldId.Value].IndexGroup ?? IndexGroupId.Invalid;
        }
        return IndexGroupId.Invalid;
    }

    #endregion

    #region Index Group Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupInfo? GetIndexGroup(IndexGroupId id) =>
        IsValidIndex(id.Value, _IndexGroups.Length) ? _IndexGroups[id.Value] : null;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IndexGroupId? GetIndexGroupId(string name) =>
        _IndexGroupNameMap.TryGetValue(name, out IndexGroupId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<IndexGroupInfo> IndexGroups => _IndexGroups;

    /// <inheritdoc/>
    public int IndexGroupCount => _IndexGroupCount;

    #endregion

    #region Protocol Table Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id) =>
        IsValidIndex(id.Value, _ProtocolTableInfos.Length) ? _ProtocolTableInfos[id.Value] : null;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProtocolTableId? GetProtocolTableId(string name) =>
        _ProtocolTableNameMap.TryGetValue(name, out ProtocolTableId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos => _ProtocolTableInfos;

    /// <inheritdoc/>
    public int ProtocolTableCount => _ProtocolTableInfos.Length;

    #endregion

    #region Post-Parser Access

    /// <inheritdoc/>
    public ReadOnlyMemory<PostParserInfo> PostParsers => _PostParsers;

    /// <inheritdoc/>
    public int PostParserCount => _PostParsers.Length;

    #endregion

    #region Heuristic Table Access

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id) =>
        IsValidIndex(id.Value, _HeuristicTableInfos.Length) ? _HeuristicTableInfos[id.Value] : null;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name) =>
        _HeuristicTableNameMap.TryGetValue(name, out HeuristicProtocolTableId id) ? id : null;

    /// <inheritdoc/>
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos => _HeuristicTableInfos;

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => _HeuristicTableInfos.Length;

    #endregion

    #region Settings Access

    /// <inheritdoc/>
    public IReadOnlySettingsManager Settings => _SettingsManager;

    #endregion

    #region Stream Reassembly

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) =>
        _ReassemblyConfigs.GetValueOrDefault(protocolId);

    #endregion

    #region Dispatch Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolTable? GetProtocolTable(ProtocolTableId id) =>
        IsValidIndex(id.Value, _ProtocolTables.Length) ? _ProtocolTables[id.Value] : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal HeuristicProtocolTable? GetHeuristicProtocolTable(HeuristicProtocolTableId id) =>
        IsValidIndex(id.Value, _HeuristicTables.Length) ? _HeuristicTables[id.Value] : null;

    #endregion

    #region Table Entry Iterators

    /// <summary>
    /// Iterates all registered u64 key → protocol-ID entries in the given dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not a u64-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches. Each returned <see cref="ReadOnlyMemory{T}"/> contains the ordered list
    /// of <see cref="ProtocolId"/> values registered for that key (usually one entry).
    /// </para>
    /// </summary>
    public IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? GetU64TableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterU64Entries();

    /// <summary>
    /// Iterates all registered string key → protocol-ID entries in the given dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not a string-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    public IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? GetStringTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterStringEntries();

    /// <summary>
    /// Iterates all registered bytes key → protocol-ID entries in the given dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not a bytes-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    public IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? GetBytesTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterBytesEntries();

    /// <summary>
    /// Iterates the bool keys (<c>false</c>, <c>true</c>) of the given dispatch table,
    /// returning only keys with at least one registered protocol.
    /// Returns <see langword="null"/> if the table does not exist or is not a bool-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    public IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? GetBoolTableEntries(
        ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.IterBoolEntries();

    /// <summary>
    /// Returns all protocol IDs registered in the given Any-keyed dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not an Any-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    public ReadOnlyMemory<ProtocolId>? GetAnyTableProtocolIds(ProtocolTableId tableId)
        => GetProtocolTable(tableId)?.GetAnyProtocolIds();

    #endregion

    #region Protocol Dispatch

    /// <summary>
    /// Calls a protocol's Parse method via a pre-bound delegate. Direct array
    /// indexing plus delegate invocation — no interface vtable dispatch.
    /// Sets <see cref="ParseContext.SelfProtocolId"/> to <paramref name="protocolId"/> on the
    /// context before invoking the delegate so that every protocol can always identify itself
    /// without storing a separate field.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult CallProtocol(
        ProtocolId protocolId, in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (!IsValidIndex(protocolId.Value, _ParseDelegates.Length))
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
    internal IProtocol? ResolveProtocol(ProtocolId id) =>
        IsValidIndex(id.Value, _ProtocolInstances.Length) ? _ProtocolInstances[id.Value] : null;

    /// <summary>
    /// Resolves a <see cref="ProtocolId"/> to its pre-bound <see cref="ParseDelegate"/>.
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build dispatch caches
    /// that store delegates for direct invocation without interface vtable dispatch.
    /// <para>
    /// <b>Do not call per packet.</b> The returned delegate is stable for the lifetime of
    /// the stack.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseDelegate? ResolveParseDelegate(ProtocolId id) =>
        IsValidIndex(id.Value, _ParseDelegates.Length) ? _ParseDelegates[id.Value] : null;

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
            Volatile.Write(ref _ShutdownDiagnostics, inner);
        }
        catch (Exception ex)
        {
            // Defensive fallback: any non-AggregateException out of Shutdown is captured
            // identically. Catching base Exception is required by CA1065.
            Volatile.Write(ref _ShutdownDiagnostics, [ex]);
        }

        _SettingsManager.DisposeResources();
    }

    #endregion
}
