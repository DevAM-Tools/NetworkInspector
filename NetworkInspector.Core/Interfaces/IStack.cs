// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Read-only access to the protocol stack registry.
/// <para>
/// Provides O(1) access to protocol, field, dispatch table, and setting metadata
/// by strongly-typed identifiers. Implemented by both <see cref="StackBuilder"/>
/// (during registration) and <see cref="Stack"/> (after freezing).
/// </para>
/// </summary>
public interface IStack
{
    #region Build Diagnostics

    /// <summary>
    /// Non-fatal diagnostics produced during <see cref="StackBuilder.Build"/>.
    /// The list combines two kinds of entries:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="BuildCallbackWarning"/> — a <c>When*Registered</c> deferred callback that
    ///     never fired because the referenced entity was never registered.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="BuildStartupError"/> — an exception thrown by a protocol's
    ///     <see cref="IProtocol.OnStart(Stack)"/> hook.
    ///   </description></item>
    /// </list>
    /// Callers should inspect this after <see cref="StackBuilder.Build"/> and decide whether
    /// the stack is safe to use.
    /// <para>
    /// <see cref="StackBuilder"/> returns an empty memory because <see cref="StackBuilder.Build"/>
    /// has not been called yet.
    /// </para>
    /// </summary>
    ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics
    {
        get;
    }

    /// <summary>
    /// The shared frame interface registry managing capture-interface registrations.
    /// </summary>
    FrameInterfaceRegistry FrameInterfaceRegistry { get; }

    /// <summary>
    /// When <see langword="true"/>, parser exception error messages include the full exception
    /// stack trace. Defaults to <see langword="false"/> to keep error messages concise in production.
    /// </summary>
    bool IncludeExceptionStackTrace { get; }

    #endregion

    #region Protocol Access

    /// <summary>Gets protocol info by ID. Returns <c>null</c> if the ID is out of range.</summary>
    ProtocolInfo? GetProtocol(ProtocolId id);

    /// <summary>Looks up a protocol ID by name. Returns <c>null</c> if not found.</summary>
    ProtocolId? GetProtocolId(string name);

    /// <summary>All registered protocols.</summary>
    ReadOnlyMemory<ProtocolInfo> Protocols
    {
        get;
    }

    /// <summary>Number of registered protocols.</summary>
    int ProtocolCount
    {
        get;
    }

    #endregion

    #region Field Access

    /// <summary>Gets field info by ID. Returns <c>null</c> if the ID is out of range.</summary>
    FieldInfo? GetField(FieldId id);

    /// <summary>
    /// Looks up a canonical field ID by name. Returns <c>null</c> if not found.
    /// <para>
    /// Field alias names (e.g., <c>"eth.addr"</c>, <c>"ip.addr"</c>, <c>"udp.port"</c>) are
    /// <b>never</b> resolved by this method by design; the canonical field namespace and the
    /// alias namespace are independent. Use <see cref="GetFieldAliasGroupId(string)"/> to
    /// resolve alias names. This separation keeps indexing and per-packet
    /// field-lookup paths on canonical fields only and never accidentally observes an alias
    /// fallback.
    /// </para>
    /// </summary>
    FieldId? GetFieldId(string name);

    /// <summary>All registered fields.</summary>
    ReadOnlyMemory<FieldInfo> Fields
    {
        get;
    }

    /// <summary>Number of registered fields.</summary>
    int FieldCount
    {
        get;
    }

    /// <summary>Gets the index group assigned to a field. Returns <see cref="IndexGroupId.Invalid"/> if none.</summary>
    IndexGroupId GetFieldIndexGroup(FieldId fieldId);

    #endregion

    #region Field Alias Group Access

    /// <summary>
    /// Gets field alias group info by ID. Returns <c>null</c> if the ID is out of range.
    /// <para>
    /// Alias groups expose any-match semantics for protocol fields (e.g., <c>"eth.addr"</c>
    /// resolves to <c>{ eth.dst, eth.src }</c>) as metadata only; canonical lookup via
    /// <see cref="GetFieldId(string)"/> is unaffected and never resolves alias names.
    /// </para>
    /// </summary>
    FieldAliasGroupInfo? GetFieldAliasGroup(FieldAliasGroupId id);

    /// <summary>
    /// Looks up a field alias group ID by name. Returns <c>null</c> if not found.
    /// </summary>
    FieldAliasGroupId? GetFieldAliasGroupId(string name);

    /// <summary>All registered field alias groups.</summary>
    ReadOnlyMemory<FieldAliasGroupInfo> FieldAliasGroups
    {
        get;
    }

    /// <summary>Number of registered field alias groups.</summary>
    int FieldAliasGroupCount
    {
        get;
    }

    #endregion

    #region Index Group Access

    /// <summary>Gets index group info by ID. Returns <c>null</c> if the ID is out of range.</summary>
    IndexGroupInfo? GetIndexGroup(IndexGroupId id);

    /// <summary>Looks up an index group ID by name. Returns <c>null</c> if not found.</summary>
    IndexGroupId? GetIndexGroupId(string name);

    /// <summary>All registered index groups.</summary>
    ReadOnlyMemory<IndexGroupInfo> IndexGroups
    {
        get;
    }

    /// <summary>Number of registered index groups.</summary>
    int IndexGroupCount
    {
        get;
    }

    #endregion

    #region Protocol Table Access

    /// <summary>Gets protocol table info by ID. Returns <c>null</c> if the ID is out of range.</summary>
    ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id);

    /// <summary>Looks up a protocol table ID by name. Returns <c>null</c> if not found.</summary>
    ProtocolTableId? GetProtocolTableId(string name);

    /// <summary>All registered protocol tables.</summary>
    ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos
    {
        get;
    }

    /// <summary>Number of registered protocol tables.</summary>
    int ProtocolTableCount
    {
        get;
    }

    #endregion

    #region Post-Parser Access

    /// <summary>
    /// All registered post-parsers, sorted in the order they will be executed at runtime.
    /// <para>
    /// Sort key: <see cref="PostParserInfo.Priority"/> ascending (lower values first),
    /// then <see cref="PostParserInfo.Id"/> ascending (registration order) as a stable
    /// tie-breaker. The sort is performed once at build time in <see cref="StackBuilder.Build"/>;
    /// no runtime sorting occurs in the parse hot path.
    /// </para>
    /// <para>
    /// Execution lifecycle: post-parsers run after the main protocol dispatch on every packet,
    /// before <c>packet.info</c> is appended and before the packet is sealed. Each post-parser
    /// receives the packet root field as its parent, identical to how <see cref="PacketProtocol"/>
    /// calls top-level sub-protocols.
    /// </para>
    /// <para>
    /// Error policy: a <see cref="ParseResult"/> error or exception from any post-parser is
    /// recorded as a packet-level error; the remaining post-parsers always continue executing.
    /// </para>
    /// </summary>
    ReadOnlyMemory<PostParserInfo> PostParsers
    {
        get;
    }

    /// <summary>Number of registered post-parsers.</summary>
    int PostParserCount
    {
        get;
    }

    #endregion

    #region Heuristic Table Access

    /// <summary>Gets heuristic table info by ID. Returns <c>null</c> if the ID is out of range.</summary>
    HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id);

    /// <summary>Looks up a heuristic table ID by name. Returns <c>null</c> if not found.</summary>
    HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name);

    /// <summary>All registered heuristic protocol tables.</summary>
    ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos
    {
        get;
    }

    /// <summary>Number of registered heuristic protocol tables.</summary>
    int HeuristicProtocolTableCount
    {
        get;
    }

    #endregion

    #region Settings Access

    /// <summary>
    /// Zero-allocation read-only view of the settings manager.
    /// Keep the compile-time type as <see cref="ReadOnlySettingsManagerView"/> or pass it to a
    /// generic <c>where TSettings : IReadOnlySettingsManager</c> API. Assigning this value to
    /// <see cref="IReadOnlySettingsManager"/> boxes.
    /// </summary>
    ReadOnlySettingsManagerView Settings
    {
        get;
    }

    #endregion

    #region Stream Reassembly

    /// <summary>
    /// Gets the stream reassembly configuration for a protocol, if any.
    /// Returns <c>null</c> if the protocol has no reassembly config registered.
    /// </summary>
    /// <param name="protocolId">The protocol to query.</param>
    StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId);

    #endregion

    #region Dispatch Helpers

    /// <summary>
    /// Returns all <see cref="ProtocolId"/> values registered for <paramref name="key"/> in
    /// the specified u64-keyed dispatch table, or an empty span if the table does not exist,
    /// is not u64-keyed, or has no entry for that key.
    /// <para>
    /// Intended for per-packet "identify without dispatching" use cases — for example to check
    /// which protocols own a port before deciding whether stream reassembly applies.
    /// For hot-path dispatch, build delegate caches once in <see cref="IProtocol.OnStart"/> using
    /// the table-entry iterators (<see cref="Stack.GetU64TableEntries"/> etc.).
    /// </para>
    /// </summary>
    ReadOnlySpan<ProtocolId> GetProtocolsFromU64ProtocolTable(ProtocolTableId tableId, ulong key);

    /// <summary>
    /// Returns all <see cref="ProtocolId"/> values registered for <paramref name="key"/> in
    /// the specified string-keyed dispatch table, or an empty span if the table does not exist,
    /// is not string-keyed, or has no entry for that key.
    /// <para>
    /// Intended for per-packet "identify without dispatching" use cases.
    /// For hot-path dispatch, build delegate caches once in <see cref="IProtocol.OnStart"/> using
    /// the table-entry iterators (<see cref="Stack.GetStringTableEntries"/> etc.).
    /// </para>
    /// </summary>
    ReadOnlySpan<ProtocolId> GetProtocolsFromStringProtocolTable(ProtocolTableId tableId, string key);

    /// <summary>
    /// Returns all <see cref="ProtocolId"/> values registered for <paramref name="key"/> in
    /// the specified bytes-keyed dispatch table, or an empty span if the table does not exist,
    /// is not bytes-keyed, or has no entry for that key.
    /// <para>
    /// Intended for per-packet "identify without dispatching" use cases.
    /// For hot-path dispatch, build delegate caches once in <see cref="IProtocol.OnStart"/> using
    /// the table-entry iterators (<see cref="Stack.GetBytesTableEntries"/> etc.).
    /// </para>
    /// </summary>
    ReadOnlySpan<ProtocolId> GetProtocolsFromBytesProtocolTable(ProtocolTableId tableId, BytesKey key);

    /// <summary>
    /// Returns all <see cref="ProtocolId"/> values registered for <paramref name="key"/> in
    /// the specified bool-keyed dispatch table, or an empty span if the table does not exist,
    /// is not bool-keyed, or has no entry for that key.
    /// <para>
    /// Intended for per-packet "identify without dispatching" use cases.
    /// For hot-path dispatch, build delegate caches once in <see cref="IProtocol.OnStart"/> using
    /// the table-entry iterators (<see cref="Stack.GetBoolTableEntries"/> etc.).
    /// </para>
    /// </summary>
    ReadOnlySpan<ProtocolId> GetProtocolsFromBoolProtocolTable(ProtocolTableId tableId, bool key);

    /// <summary>
    /// Returns all <see cref="ProtocolId"/> values registered in the specified Any-keyed
    /// dispatch table, or an empty span if the table does not exist or is not Any-keyed.
    /// <para>
    /// Intended for per-packet "identify without dispatching" use cases.
    /// For hot-path dispatch, build delegate caches once in <see cref="IProtocol.OnStart"/> using
    /// <see cref="Stack.GetAnyTableProtocolIds"/>.
    /// </para>
    /// </summary>
    ReadOnlySpan<ProtocolId> GetProtocolsFromAnyProtocolTable(ProtocolTableId tableId);

    /// <summary>
    /// Runs all registered heuristic parsers in the given heuristic dispatch table against
    /// <paramref name="data"/> and returns the <see cref="ProtocolId"/> of the first match,
    /// or <see langword="null"/> if the table does not exist or no parser matches.
    /// <para>
    /// Intended for per-packet use in protocols that need to cache the detected protocol ID
    /// on connection state before dispatching.
    /// </para>
    /// </summary>
    ProtocolId? TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data);

    /// <summary>
    /// Resolves a <see cref="ProtocolId"/> to its concrete <see cref="ParseDelegate"/>.
    /// The returned delegate targets <see cref="IProtocol.Parse"/>. Prefer
    /// <see cref="MutField.CallProtocol"/> so invalid ids return <see cref="ParseError"/>
    /// instead of skipping the invoke.
    /// <para>
    /// <b>Do not call per packet.</b> The returned delegate is stable for the lifetime of
    /// the stack.
    /// </para>
    /// </summary>
    ParseDelegate? ResolveParseDelegate(ProtocolId id);

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
    IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? GetU64TableEntries(ProtocolTableId tableId);

    /// <summary>
    /// Iterates all registered string key → protocol-ID entries in the given dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not a string-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? GetStringTableEntries(ProtocolTableId tableId);

    /// <summary>
    /// Iterates all registered bytes key → protocol-ID entries in the given dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not a bytes-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? GetBytesTableEntries(ProtocolTableId tableId);

    /// <summary>
    /// Iterates the bool keys (<c>false</c>, <c>true</c>) of the given dispatch table,
    /// returning only keys with at least one registered protocol.
    /// Returns <see langword="null"/> if the table does not exist or is not a bool-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? GetBoolTableEntries(ProtocolTableId tableId);

    /// <summary>
    /// Returns all protocol IDs registered in the given Any-keyed dispatch table.
    /// Returns <see langword="null"/> if the table does not exist or is not an Any-keyed table.
    /// <para>
    /// Intended for one-time use in <see cref="IProtocol.OnStart"/> to build pre-computed
    /// dispatch caches.
    /// </para>
    /// </summary>
    ReadOnlyMemory<ProtocolId>? GetAnyTableProtocolIds(ProtocolTableId tableId);

    #endregion
}
