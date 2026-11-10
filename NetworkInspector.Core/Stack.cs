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
/// latch (<see cref="Interlocked.CompareExchange(ref int, int, int)"/>) guarantees that each protocol's
/// <see cref="NetworkInspector.Core.Protocols.IProtocol.OnShutdown"/> is invoked exactly once
/// regardless of whether the caller calls <see cref="Shutdown"/> explicitly before
/// <see cref="Dispose"/> or relies entirely on <see cref="Dispose"/> for teardown. <see cref="Dispose"/> returns only after a
/// <see cref="Shutdown"/> that another thread started has finished, so the settings manager outlives every
/// <see cref="NetworkInspector.Core.Protocols.IProtocol.OnShutdown"/> call.
/// </para>
/// <para>
/// Using the stack after <see cref="Dispose"/> is not supported; parses that are still running must finish first.
/// </para>
/// </summary>
public sealed class Stack : IStack, IDisposable
{
    #region Configuration and state

    /// <summary>
    /// Protocol instances, indexed by <see cref="ProtocolId"/>. Built once by <see cref="StackBuilder.Build"/> and never
    /// modified afterwards. Every id is the dense index of its entry, so a lookup is a bounds check plus an array load (no
    /// hashing). The matching Info arrays are the <see cref="ReadOnlyMemory{T}"/> properties.
    /// </summary>
    private readonly IProtocol[] _ProtocolInstances;

    /// <summary>Parse delegates bound to the concrete <c>Parse</c> methods, indexed by <see cref="ProtocolId"/>.</summary>
    private readonly ParseDelegate[] _ParseDelegates;

    /// <summary>Protocol dispatch tables, indexed by <see cref="ProtocolTableId"/>. The table objects are shared with the builder.</summary>
    private readonly ProtocolTable[] _ProtocolTables;

    /// <summary>Heuristic dispatch tables, indexed by <see cref="HeuristicProtocolTableId"/>.</summary>
    private readonly HeuristicProtocolTable[] _HeuristicTables;

    /// <summary>
    /// The same manager the <see cref="StackBuilder"/> used while protocols registered their settings. The stack takes over
    /// its lifetime: <see cref="Dispose"/> releases the manager's locks.
    /// </summary>
    private readonly SettingsManager _SettingsManager;

    /// <summary>
    /// Name-to-id map of the protocols, frozen at build time. The six name maps serve lookup by name in setup code; parsers
    /// resolve a name to an id once (for example in <c>OnStart</c>) and use ids afterwards. <see cref="FrozenDictionary{TKey, TValue}"/>
    /// is read-only and therefore safe for concurrent readers without locking, and its lookups are faster than a
    /// <see cref="Dictionary{TKey, TValue}"/>'s.
    /// </summary>
    private readonly FrozenDictionary<string, ProtocolId> _ProtocolNameMap;

    /// <summary>Name-to-id map of the fields; see <see cref="_ProtocolNameMap"/>.</summary>
    private readonly FrozenDictionary<string, FieldId> _FieldNameMap;

    /// <summary>Name-to-id map of the protocol tables; see <see cref="_ProtocolNameMap"/>.</summary>
    private readonly FrozenDictionary<string, ProtocolTableId> _ProtocolTableNameMap;

    /// <summary>Name-to-id map of the heuristic protocol tables; see <see cref="_ProtocolNameMap"/>.</summary>
    private readonly FrozenDictionary<string, HeuristicProtocolTableId> _HeuristicTableNameMap;

    /// <summary>Name-to-id map of the index groups; see <see cref="_ProtocolNameMap"/>.</summary>
    private readonly FrozenDictionary<string, IndexGroupId> _IndexGroupNameMap;

    /// <summary>Name-to-id map of the field alias groups; see <see cref="_ProtocolNameMap"/>.</summary>
    private readonly FrozenDictionary<string, FieldAliasGroupId> _FieldAliasGroupNameMap;

    /// <summary>Frozen stream reassembly configs keyed by protocol ID; a protocol without an entry has no reassembly config.</summary>
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
    /// Shutdown state: 0 not started, 1 running, 2 finished. Written with a compare-exchange (0 → 1) by the one thread that
    /// runs the shutdown, so <see cref="IProtocol.OnShutdown"/> runs at most once, and with a volatile store (→ 2) when it is
    /// done; read by <see cref="Dispose"/> to wait for a shutdown that another thread started. Must remain a field:
    /// <c>Interlocked</c> needs <c>ref</c>.
    /// </summary>
    private volatile int _ShutdownFlag;

    /// <summary>
    /// Managed id of the thread that runs <see cref="Shutdown"/>; 0 before. Lets a re-entrant <see cref="Dispose"/> (called
    /// from an <see cref="IProtocol.OnShutdown"/> on the shutdown thread itself) skip the wait for its own thread.
    /// </summary>
    private volatile int _ShutdownThreadId;

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
    /// <para>
    /// Why a watermark: stateful protocols (connection trackers, reassembly, fragment buffers) mutate their state only
    /// during the ordered first parse and record a compact effect per packet; a re-parse replays that effect and, on any
    /// number of threads, must not mutate shared state again. The stack therefore has to tell a first parse from a replay.
    /// Dense ids make one integer enough instead of a set of parsed ids. Because only one thread performs the next first
    /// parse, the update is a plain volatile store (no compare-exchange, no maximum): the store is the release that makes the
    /// completed first parse visible to replay threads that read the field. The cache and the index use the same verdict
    /// (they record on a first parse only).
    /// </para>
    /// </summary>
    private volatile int _ParseWatermark = -1;

    /// <summary>
    /// Creates a finalized, immutable protocol stack from builder-prepared data. Called only by <see cref="StackBuilder.Build"/>.
    /// All arrays and frozen maps are adopted as they are (no defensive copy, and the <c>ReadOnlyMemory</c> properties wrap
    /// the arrays without copying): <see cref="StackBuilder.Build"/> creates them fresh for this stack and keeps no reference
    /// to the arrays, so nothing mutates them afterwards. The elements are shared with the builder, though: the
    /// <c>ProtocolTable</c> and <c>HeuristicProtocolTable</c> objects stay mutable, so the builder must not register anything
    /// after <see cref="StackBuilder.Build"/>. The arguments are not validated; the builder guarantees that the arrays are
    /// indexed by the IDs it handed out and that the maps cover the same entries.
    /// </summary>
    /// <param name="protocols">Protocol infos, indexed by protocol id.</param>
    /// <param name="protocolInstances">Protocol instances, indexed by protocol id.</param>
    /// <param name="parseDelegates">Bound parse delegates, indexed by protocol id.</param>
    /// <param name="protocolNameMap">Protocol name to id.</param>
    /// <param name="fields">Field infos, indexed by field id.</param>
    /// <param name="fieldNameMap">Field name to id.</param>
    /// <param name="protocolTables">Protocol dispatch tables, indexed by table id.</param>
    /// <param name="protocolTableInfos">Protocol table infos, indexed by table id.</param>
    /// <param name="protocolTableNameMap">Protocol table name to id.</param>
    /// <param name="heuristicTables">Heuristic dispatch tables, indexed by table id.</param>
    /// <param name="heuristicTableInfos">Heuristic table infos, indexed by table id.</param>
    /// <param name="heuristicTableNameMap">Heuristic table name to id.</param>
    /// <param name="postParsers">Post-parser infos in execution order.</param>
    /// <param name="settingsManager">The settings manager whose lifetime the stack takes over.</param>
    /// <param name="rootFieldId">The root field id.</param>
    /// <param name="packetErrorFieldId">The field id of error annotations.</param>
    /// <param name="packetChoiceFieldId">The field id of the packet.choice container.</param>
    /// <param name="packetProtocolId">The top-level packet protocol.</param>
    /// <param name="frameProtocolId">The frame protocol found by name during build.</param>
    /// <param name="indexGroups">Index group infos, indexed by group id.</param>
    /// <param name="indexGroupNameMap">Index group name to id.</param>
    /// <param name="frameInterfaceRegistry">The registry that frames of this stack must share.</param>
    /// <param name="includeExceptionStackTrace">Whether exception stack traces are written into error texts.</param>
    /// <param name="reassemblyConfigs">Stream reassembly configs by protocol id.</param>
    /// <param name="fieldAliasGroups">Field alias group infos, indexed by group id.</param>
    /// <param name="fieldAliasGroupNameMap">Field alias group name to id.</param>
    /// <param name="limits">The live holders of the limits of Core, created from the settings of this stack.</param>
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
        FrozenDictionary<string, FieldAliasGroupId> fieldAliasGroupNameMap,
        CoreLimitSet limits)
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
        Limits = limits;
    }

    #endregion

    #region Index validation

    /// <summary>
    /// Whether <paramref name="idValue"/> is a valid index into a collection of <paramref name="count"/> entries. The
    /// unsigned comparison covers both bounds in one branch: a negative value (such as the <c>Invalid</c> sentinel, -1) becomes
    /// a huge unsigned number and fails the same test as a value past the end. Inlined because every ID lookup uses it.
    /// </summary>
    /// <param name="idValue">The id number to test.</param>
    /// <param name="count">The number of entries of the indexed collection.</param>
    /// <returns><see langword="true"/> when <c>0 &lt;= idValue &lt; count</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsValidIndex(int idValue, int count) => (uint)idValue < (uint)count;

    #endregion

    #region Built-in ids and build settings

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

    /// <inheritdoc/>
    public bool IncludeExceptionStackTrace { get; }

    #endregion

    #region Diagnostics

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

    /// <summary>Publishes the build diagnostics once, at the end of <see cref="StackBuilder.Build"/>.</summary>
    /// <param name="diagnostics">All diagnostics of the build; adopted without a copy and never modified afterwards.</param>
    internal void SetBuildDiagnostics(BuildDiagnostic[] diagnostics) =>
        _BuildDiagnostics = diagnostics;

    #endregion

    #region Parse sequence

    /// <summary>
    /// Classifies a
    /// <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>
    /// call as a first parse or a replay and
    /// rejects jumps. First parses on a stack must use dense ids <c>0, 1, 2, …</c>.
    /// Returns <see langword="true"/> when <paramref name="id"/> was already first-parsed
    /// (<c>id ≤ watermark</c>) and must replay; <see langword="false"/> when this is the next
    /// first parse (<c>id == watermark + 1</c>).
    /// </summary>
    /// <param name="id">The packet id of the parse about to start; must be valid.</param>
    /// <returns><see langword="true"/> for a replay of an already first-parsed id; <see langword="false"/> for the next first parse.</returns>
    /// <remarks>
    /// <see cref="Packet.TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/> and its recycle
    /// overload check <see cref="PacketId.IsValid"/> and <see cref="WouldJump"/> first and report
    /// <see cref="ParseFailure.InvalidPacketId"/> / <see cref="ParseFailure.ParseIdGap"/> without throwing. The exceptions
    /// below are the backstop for an internal caller that bypassed those checks. The read of the watermark and the later
    /// <see cref="CompleteFirstParse"/> are not one atomic step: the caller serializes first parses.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="id"/> is <see cref="PacketId.Invalid"/> or otherwise not
    /// <see cref="PacketId.IsValid"/>, and when <paramref name="id"/> is greater than the next
    /// expected first-parse id.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ObserveParse(PacketId id)
    {
        // An invalid id (the -1 sentinel) is neither a first parse nor a replay; without this check it would satisfy the
        // "value <= current" test below, because the watermark never drops below -1, and be classified as a replay.
        if (!id.IsValid)
        {
            throw new InvalidOperationException(
                "Packet id is invalid. First-parse ids are 0, 1, 2, … PacketId.Invalid is not a replay.");
        }

        // One volatile read of the watermark; all three outcomes derive from this single snapshot so a concurrent
        // CompleteFirstParse cannot make the verdict inconsistent within one call.
        int value = id.Value;
        int current = _ParseWatermark;

        // Already first-parsed: a replay, allowed from any thread.
        if (value <= current)
        {
            return true;
        }

        // The next dense id: the one legal first parse.
        if (value == current + 1)
        {
            return false;
        }

        // A jump. The throw sits in a separate non-inlined method so this inlined fast path stays small; the return after
        // it is unreachable at run time and only satisfies the compiler, which cannot see that the helper never returns.
        _ThrowParseIdGap(value, current);
        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="id"/> would throw from <see cref="ObserveParse"/> as a
    /// first-parse gap. No watermark mutation. Used by <c>Packet.TryParse</c> so a recycle target
    /// stays unchanged when the caller skipped an id (the check runs before the packet is prepared for reuse), and so that a
    /// gap is reported as <see cref="ParseFailure.ParseIdGap"/> instead of an exception. The caller checks
    /// <see cref="PacketId.IsValid"/> first; this method does not.
    /// </summary>
    /// <param name="id">The packet id of the parse about to start.</param>
    /// <returns><see langword="true"/> when <paramref name="id"/> is a first parse that skips an id.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WouldJump(PacketId id)
    {
        int value = id.Value;
        int current = _ParseWatermark;
        return value > current && value != current + 1;
    }

    /// <summary>
    /// Marks <paramref name="id"/> as having completed its first parse. Must be called from
    /// <see langword="finally"/> after a non-replay <see cref="ObserveParse"/> so even a failed
    /// parse closes the id — a later call with the same id is then a replay. This is a plain volatile store, not a
    /// compare-exchange: the first parse of an id is serialized by the caller (see <c>_ParseWatermark</c>), so no second
    /// writer can race with it, and because <see cref="ObserveParse"/> admitted exactly the id after the watermark, the store
    /// only moves the watermark forward by one.
    /// </summary>
    /// <param name="id">The id whose first parse has ended, successfully or not.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompleteFirstParse(PacketId id) =>
        _ParseWatermark = id.Value;

    /// <summary>
    /// Cold-path helper: first-parse ids on a stack must be dense starting at 0. Kept out of line
    /// (<see cref="MethodImplOptions.NoInlining"/>) so the message formatting and the throw do not bloat the inlined
    /// <see cref="ObserveParse"/>.
    /// </summary>
    /// <param name="actual">The rejected packet id value.</param>
    /// <param name="current">The watermark that was read.</param>
    /// <exception cref="InvalidOperationException">Always.</exception>
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

    // The ID-based accessors in this class (protocol, field, alias group, index group, table, heuristic table) share one
    // shape: an unsigned bounds check, then a direct array read, and null or Invalid for an unknown ID instead of an
    // exception. They are AggressiveInlining because each is a few instructions that can be called per packet or per
    // field, where a call would cost more than the body. The name-based accessors go through a FrozenDictionary and are
    // meant for setup code.

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

    /// <summary>
    /// The live holders of the limits of Core (<see cref="CoreLimits"/>) for this stack: they follow the settings of the stack, so a
    /// change of a limit setting is visible to the next read. Owners read a limit once per decision (<see cref="Limit.Ceiling"/>).
    /// </summary>
    public CoreLimitSet Limits { get; }

    #endregion

    #region Stream reassembly config

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) =>
        _ReassemblyConfigs.GetValueOrDefault(protocolId);

    #endregion

    #region Dispatch Helpers

    // The protocol tables are the per-packet dispatch path (a key read from a parsed field selects the next protocol). Looking
    // a table up by dense ID with an unsigned bounds check, and returning a span over the table's own list storage, avoids a
    // dictionary lookup for the table and any allocation per dispatch; an unknown table ID yields "no match" instead of an
    // exception. The spans stay valid only while nobody registers into the table: registration ends with
    // StackBuilder.Build, and the stack exposes no way to register afterwards. The table objects themselves are shared with
    // the builder (Build copies only the array of references), so the builder must not be used for registration after Build.

    /// <summary>The dispatch table with the given ID, or <see langword="null"/> for an ID this stack did not issue.</summary>
    /// <param name="id">The table id.</param>
    /// <returns>The table, or <see langword="null"/> for an unknown id.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolTable? GetProtocolTable(ProtocolTableId id)
    {
        if (_IsValidIndex(id.Value, _ProtocolTables.Length))
        {
            return _ProtocolTables[id.Value];
        }
        return null;
    }

    /// <summary>The heuristic dispatch table with the given ID, or <see langword="null"/> for an ID this stack did not issue.</summary>
    /// <param name="id">The heuristic table id.</param>
    /// <returns>The table, or <see langword="null"/> for an unknown id.</returns>
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
    /// <paramref name="data"/> and reports the <see cref="ProtocolId"/> of the first match.
    /// <para>
    /// Intended for per-packet use in protocols that need to cache the detected protocol ID
    /// on connection state before dispatching — for example TCP's per-connection heuristic
    /// detection. For single-shot heuristic dispatch use
    /// <see cref="MutField.TryCallHeuristicProtocol"/>.
    /// </para>
    /// </summary>
    /// <param name="tableId">The heuristic table to run; an unknown id is a miss, not an error.</param>
    /// <param name="data">The bytes the heuristic parsers inspect.</param>
    /// <param name="protocolId">
    /// The protocol of the first matching parser; <see cref="ProtocolId.Invalid"/> when the method returns <see langword="false"/>.
    /// </param>
    /// <returns><see langword="true"/> when the table exists and a parser matched.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data, out ProtocolId protocolId)
    {
        HeuristicProtocolTable? table = GetHeuristicProtocolTable(tableId);
        if (table is null)
        {
            protocolId = ProtocolId.Invalid;
            return false;
        }

        return table.TryMatch(data, out protocolId);
    }

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
    /// <param name="protocolId">The protocol to run; an unknown id yields a <see cref="ParseError"/> result.</param>
    /// <param name="parentField">The field under which the protocol appends its fields.</param>
    /// <param name="data">The bytes the protocol parses.</param>
    /// <param name="context">The parse context of the caller; the callee receives a copy that names itself as the self protocol.</param>
    /// <returns>The result of the protocol, or an error result for an unknown id.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ParseResult CallProtocol(
        ProtocolId protocolId, in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // An unknown ID is an expected failure (a table can name a protocol that was never registered), so it becomes a
        // ParseError result instead of an exception; the message allocation happens only on this failure path.
        if (!_IsValidIndex(protocolId.Value, _ParseDelegates.Length))
        {
            return ParseError.Custom(
                "stack",
                string.Create(CultureInfo.InvariantCulture, $"Invalid protocol ID: {protocolId.Value}"));
        }

        // The context is passed by 'in' and copied only here, to tell the callee which protocol it is (<c>SelfProtocolId</c>). The
        // delegate was bound to the concrete Parse method in Build, so this call is a delegate invocation on a directly
        // indexed array element, not an interface dispatch.
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
    /// <param name="id">The protocol id.</param>
    /// <returns>The instance, or <see langword="null"/> for an id this stack did not issue.</returns>
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
    /// <param name="id">The protocol id.</param>
    /// <returns>The delegate, or <see langword="null"/> for an id this stack did not issue.</returns>
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
    /// calls return immediately without invoking any protocol and without waiting for a shutdown that another thread is
    /// still running; <see cref="Dispose"/> does wait for it. The first caller receives any
    /// thrown <see cref="AggregateException"/>; subsequent callers receive nothing.
    /// </para>
    /// </summary>
    /// <exception cref="AggregateException">Thrown when one or more protocols threw during shutdown.</exception>
    public void Shutdown()
    {
        // Once-gate: ensure OnShutdown runs at most once across concurrent Shutdown/Dispose calls. The compare-exchange is a
        // single atomic read-modify-write, so exactly one caller moves 0 -> 1 and runs the loop below; any concurrent or later
        // caller returns at once (it does not wait for the first one to finish).
        if (Interlocked.CompareExchange(ref _ShutdownFlag, 1, 0) != 0)
        {
            return;
        }

        _ShutdownThreadId = Environment.CurrentManagedThreadId;
        try
        {
            // Protocols are shut down in registration order, the order OnStart ran in. A protocol may come from a plugin, so any
            // exception is possible: it is caught per protocol (a top-level boundary for third-party code) so that one failing
            // protocol cannot keep the others from releasing their resources. The list is allocated only on the first failure.
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
        finally
        {
            // Release store: Dispose on another thread may now release what OnShutdown could still have used.
            _ShutdownFlag = 2;
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
        // Atomic gate — makes Dispose single-shot: two concurrent Dispose callers must not both capture shutdown errors into
        // _ShutdownDiagnostics or both release the settings manager. (Shutdown and SettingsManager.Dispose are idempotent on
        // their own; this gate keeps the whole sequence below one-time and race-free.) Interlocked.Exchange returns the
        // previous value, so exactly one caller proceeds.
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
            // caller can inspect them without violating CA1065. Copied into a plain array because the field is a volatile
            // Exception[]: one reference store publishes the complete list to readers of ShutdownDiagnostics.
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

        // Shutdown() returns at once when another thread is already inside it. That thread's OnShutdown calls may still read
        // settings, so wait for it before the settings manager goes away. Threads rarely meet here (a host shuts down once), so
        // a spin wait is the cheapest correct primitive. A protocol that disposes the stack from its own OnShutdown is on the
        // shutdown thread itself: waiting would never end, so it is skipped, and that protocol accepts that later OnShutdown
        // calls see a disposed settings manager.
        if (_ShutdownThreadId != Environment.CurrentManagedThreadId)
        {
            SpinWait shutdownWait = default;
            while (_ShutdownFlag != 2)
            {
                shutdownWait.SpinOnce();
            }
        }

        _SettingsManager.Dispose();
    }

    #endregion
}
