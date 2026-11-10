// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Build-phase interface for registering protocols, fields, dispatch tables, and settings.
/// <para>
/// Registration failures such as duplicate names or missing tables throw a <see cref="RegistrationException"/>
/// (argument errors such as a null or empty array throw the usual <see cref="ArgumentException"/> types). Errors are
/// checked before state mutation, so a rejected registration leaves no partial entry behind and the builder is never
/// left in an inconsistent state. A subscriber (<c>When*Registered</c>) or a registration callback runs after the commit;
/// when it throws, the entry it was called for stays registered. Every registration member throws
/// <see cref="InvalidOperationException"/> after <see cref="StackBuilder.Build"/>.
/// <b>Thread-safety:</b> not thread-safe; see <see cref="IStack"/>.
/// After registration is complete, call <see cref="StackBuilder.Build"/> to freeze
/// the builder into an immutable <see cref="Stack"/>.
/// </para>
/// </summary>
public interface IStackBuilder : IStack
{
    #region Protocol Registration

    /// <summary>
    /// Registers a protocol parser. Returns its unique <see cref="ProtocolId"/>.
    /// <para>
    /// Protocol, field and field alias group names share one namespace for conflicts: the first registration keeps a name
    /// and a later registration of any of the three kinds is refused. The only exception is the container field of a
    /// protocol, which may carry the name of the protocol that owns it.
    /// </para>
    /// </summary>
    /// <param name="protocol">The protocol implementation to register.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when the protocol name is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when the protocol UI name is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">
    /// Thrown when the protocol name is already held by a protocol, a field or a field alias group; the exception names the kind
    /// of the holder.
    /// </exception>
    /// <returns>The id of the registered protocol.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    ProtocolId RegisterProtocol(IProtocol protocol);

    /// <summary>
    /// Registers a protocol parser with a post-registration callback for additional setup
    /// (e.g., registering sub-fields, dispatch table entries).
    /// </summary>
    /// <typeparam name="TProtocol">The concrete protocol type.</typeparam>
    /// <param name="protocol">The protocol implementation to register.</param>
    /// <param name="callback">
    /// Callback invoked after successful registration. Receives the builder, the assigned
    /// protocol ID, and the protocol instance for further setup.
    /// </param>
    /// <returns>The id of the registered protocol.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when the protocol name is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when the protocol UI name is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">
    /// Thrown when the protocol name is already held by a protocol, a field or a field alias group.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    ProtocolId RegisterProtocol<TProtocol>(TProtocol protocol, Action<IStackBuilder, ProtocolId, TProtocol> callback)
        where TProtocol : IProtocol;

    #endregion

    #region Field Registration

    /// <summary>
    /// Registers a field definition without an index group. The name shares the conflict namespace of protocols and field
    /// alias groups (see <see cref="RegisterProtocol(IProtocol)"/>); the owning protocol's own container field may carry the
    /// protocol's name.
    /// </summary>
    /// <param name="protocolId">
    /// The owning protocol (use <see cref="ProtocolId.Invalid"/> for built-in fields). Any other ID must have been returned by
    /// this builder's protocol registration.
    /// </param>
    /// <param name="name">Machine-readable field name (e.g., "eth.dst"). Must be unique.</param>
    /// <param name="uiName">Human-readable display name (e.g., "Destination").</param>
    /// <param name="fieldType">The value type this field carries.</param>
    /// <param name="description">Optional description text.</param>
    /// <returns>The id of the registered field.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">
    /// Thrown when the field name is already held by a field, a field alias group, or a protocol other than the owner.
    /// </exception>
    /// <exception cref="NotFoundRegistrationException">
    /// Thrown when <paramref name="protocolId"/> is neither <see cref="ProtocolId.Invalid"/> nor a registered protocol.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    FieldId RegisterField(
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string? description = null);

    /// <summary>
    /// Registers a field definition with an index group for efficient cross-packet indexing.
    /// Fields that always appear together should share the same group name.
    /// <para>
    /// The group name is validated before the field is registered, so a bad group name cannot leave behind a
    /// registered field without its group.
    /// </para>
    /// </summary>
    /// <param name="protocolId">The owning protocol.</param>
    /// <param name="name">Machine-readable field name. Must be unique.</param>
    /// <param name="uiName">Human-readable display name.</param>
    /// <param name="fieldType">The value type this field carries.</param>
    /// <param name="indexGroup">
    /// Name of the index group. Fields sharing a group name are tracked by a single
    /// bitmap in <see cref="PacketIndex"/>, reducing memory overhead.
    /// </param>
    /// <param name="description">Optional description text.</param>
    /// <returns>The id of the registered field.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="indexGroup"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">
    /// Thrown when the field name is already held by a field, a field alias group, or a protocol other than the owner.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    FieldId RegisterFieldInGroup(
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string indexGroup,
        string? description = null);

    #endregion

    #region Field Alias Group Registration

    /// <summary>
    /// Registers a field alias group that exposes an "any-match" name for a set of canonical
    /// member fields. Returns the assigned <see cref="FieldAliasGroupId"/>.
    /// <para>
    /// Alias groups are metadata-only: the parsing hot path does not consult them, and the
    /// alias name remains invisible to <see cref="IStack.GetFieldId(string)"/>. An alias name shares the conflict namespace
    /// of protocol names and field names: it is refused when a protocol or a field holds the name, and a protocol or field
    /// registered later under an alias name is refused (the first registration keeps the name). Protocol-table names are a
    /// separate namespace; an alias name can coexist with a protocol-table name of the same string (e.g., <c>"udp.port"</c>)
    /// without collision.
    /// </para>
    /// <para>
    /// Member fields may carry heterogeneous <see cref="FieldType"/> values; the registry
    /// performs no same-type validation. Order of <paramref name="fieldIds"/> is preserved
    /// in <see cref="FieldAliasGroupInfo.Members"/>. The array is copied, so later changes by the caller do not
    /// affect the group, and the registration is all-or-nothing: every member is checked before the group is added.
    /// </para>
    /// </summary>
    /// <param name="protocolId">The protocol that owns this alias group.</param>
    /// <param name="name">Machine-readable alias name (e.g., "eth.addr"). Must not be held by a protocol, a field or another alias.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="fieldIds">
    /// The canonical member field IDs the alias resolves to. Must be non-empty and free of duplicates.
    /// Every ID must refer to a field already registered on this builder.
    /// </param>
    /// <returns>The id of the registered alias group.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="DuplicateNameRegistrationException">
    /// Thrown when the name is already held by an alias group, a protocol or a field; the exception names the kind of the holder.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fieldIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fieldIds"/> is empty or contains duplicate field IDs.</exception>
    /// <exception cref="NotFoundRegistrationException">
    /// Thrown when any element of <paramref name="fieldIds"/> references a field ID not registered on this builder.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when any element of <paramref name="fieldIds"/> belongs to a different protocol than <paramref name="protocolId"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    FieldAliasGroupId RegisterFieldAliasGroup(
        ProtocolId protocolId,
        string name,
        string? description,
        FieldId[] fieldIds);

    #endregion

    #region Index Group Registration

    /// <summary>
    /// Gets or creates an index group by name. Protocols use this during registration
    /// to obtain <see cref="IndexGroupId"/> values they later pass to
    /// <see cref="ParseContext.RecordGroupPresence"/> during parsing.
    /// </summary>
    /// <param name="name">The index group name. Groups with the same name share a single bitmap.</param>
    /// <returns>The id of the existing or newly created group.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    IndexGroupId GetOrCreateIndexGroup(string name);

    #endregion

    #region Protocol Table Registration

    /// <summary>Registers a protocol dispatch table (used by parsers to dispatch to sub-protocols).</summary>
    /// <param name="name">Machine-readable table name (e.g., "eth.type"). Must be unique.</param>
    /// <param name="uiName">Human-readable display name.</param>
    /// <param name="keyType">The key type used for dispatch lookups.</param>
    /// <param name="description">Optional description text.</param>
    /// <returns>The id of the registered table.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the table name is already registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    ProtocolTableId RegisterProtocolTable(
        string name,
        string uiName,
        ProtocolTableKeyType keyType,
        string? description = null);

    /// <summary>Registers a protocol parser in a U64 dispatch table by table ID.</summary>
    /// <param name="tableId">The u64-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInU64Table(ProtocolTableId tableId, ulong key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a U64 dispatch table by table name.</summary>
    /// <param name="tableName">The registered name of the u64-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInU64TableByName(string tableName, ulong key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a string dispatch table by table ID.</summary>
    /// <param name="tableId">The string-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInStringTable(ProtocolTableId tableId, string key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a string dispatch table by table name.</summary>
    /// <param name="tableName">The registered name of the string-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInStringTableByName(string tableName, string key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bytes dispatch table by table ID.</summary>
    /// <param name="tableId">The bytes-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInBytesTable(ProtocolTableId tableId, BytesKey key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bytes dispatch table by table name.</summary>
    /// <param name="tableName">The registered name of the bytes-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInBytesTableByName(string tableName, BytesKey key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bool dispatch table by table ID.</summary>
    /// <param name="tableId">The bool-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInBoolTable(ProtocolTableId tableId, bool key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bool dispatch table by table name.</summary>
    /// <param name="tableName">The registered name of the bool-keyed table.</param>
    /// <param name="key">The key that selects the protocol.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInBoolTableByName(string tableName, bool key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a catch-all dispatch table by table ID.</summary>
    /// <param name="tableId">The catch-all table.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInAnyTable(ProtocolTableId tableId, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a catch-all dispatch table by table name.</summary>
    /// <param name="tableName">The registered name of the catch-all table.</param>
    /// <param name="protocolId">The protocol to dispatch to; registered on this builder.</param>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterParserInAnyTableByName(string tableName, ProtocolId protocolId);

    #endregion

    #region Post-Parser Registration

    /// <summary>
    /// Registers a post-parser that runs after the main protocol parse completes on every packet.
    /// <para>
    /// <b>Lifecycle:</b> Post-parsers execute after the main protocol dispatch, before
    /// <c>packet.info</c> is appended, and before the packet is sealed. They receive the packet
    /// root field as their parent, which means their fields appear as root-level siblings —
    /// identical to top-level protocol fields. In the indexed parse path, post-parsers run before
    /// <see cref="PacketIndex"/> <c>EndPacket</c>, so their index contributions
    /// are treated identically to normal parsers.
    /// </para>
    /// <para>
    /// <b>Sort order:</b> Post-parsers are kept in a deterministic order (and sorted again at build time, so the parse
    /// path never sorts): ascending by <paramref name="priority"/>, then ascending by registration order as a stable
    /// tie-breaker.
    /// A lower <paramref name="priority"/> value therefore executes earlier.
    /// </para>
    /// <para>
    /// <b>Error policy:</b> A <see cref="ParseResult"/> error or an exception thrown by a
    /// post-parser is recorded as a packet-level error visible in <c>packet.error</c>.
    /// The remaining post-parsers always continue executing regardless of earlier failures.
    /// Stack traces are included in the error message when
    /// <see cref="StackBuilder.IncludeExceptionStackTrace"/> is <see langword="true"/>.
    /// </para>
    /// <para>
    /// <b>STRIDE / security:</b> Post-parsers operate within the same trust boundary as normal
    /// parsers — they receive already-validated frame data. No new external input surface is
    /// opened. Errors are surfaced as visible packet errors (no silent failures). The execution
    /// loop is deterministic and finite (one iteration per registered post-parser).
    /// </para>
    /// <para>
    /// <b>Concurrency:</b> Post-parsers execute within the single-writer parse path.
    /// No new locks or mutable shared state are introduced.
    /// </para>
    /// </summary>
    /// <param name="protocolId">The protocol this post-parser is associated with.</param>
    /// <param name="priority">Execution priority. Lower values run first; default is 0. Equal-priority post-parsers run in registration order.</param>
    /// <param name="description">Optional description text.</param>
    /// <returns>The id of the registered post-parser; it reflects registration order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    PostParserId RegisterPostParser(
        ProtocolId protocolId,
        int priority = 0,
        string? description = null);

    #endregion

    #region Heuristic Table Registration

    /// <summary>Registers a heuristic protocol dispatch table for data-driven protocol identification.</summary>
    /// <param name="owningProtocolId">The protocol that owns this heuristic table.</param>
    /// <param name="name">Machine-readable table name. Must be unique.</param>
    /// <param name="uiName">Human-readable display name.</param>
    /// <param name="description">Optional description text.</param>
    /// <returns>The id of the registered heuristic table.</returns>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the table name is already registered.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    HeuristicProtocolTableId RegisterHeuristicProtocolTable(
        ProtocolId owningProtocolId,
        string name,
        string uiName,
        string? description = null);

    /// <summary>Registers a heuristic parser in a heuristic dispatch table.</summary>
    /// <param name="tableId">The heuristic table that receives the parser.</param>
    /// <param name="parser">The parser to add; it runs against the bytes handed to the table, in registration order.</param>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterHeuristicParser(HeuristicProtocolTableId tableId, IHeuristicParser parser);

    #endregion

    #region Settings Registration

    /// <summary>
    /// Returns a <see cref="SettingsRegistrar"/> facade for registering settings.
    /// <para>
    /// Because <see cref="SettingsRegistrar"/> is a <see langword="ref struct"/>,
    /// it cannot be stored as a field, preventing protocols from keeping a reference
    /// to the underlying <see cref="SettingsManager"/>.
    /// </para>
    /// </summary>
    SettingsRegistrar SettingsRegistrar
    {
        get;
    }

    #endregion

    #region Deferred Registration

    /// <summary>
    /// Registers a callback to be invoked when a protocol with the given name is registered.
    /// If already registered, the callback fires immediately.
    /// </summary>
    /// <param name="name">The protocol name to wait for.</param>
    /// <param name="callback">Receives the id of the protocol; runs after the commit of that registration.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void WhenProtocolRegistered(string name, Action<ProtocolId> callback);

    /// <summary>
    /// Registers a callback to be invoked when a field with the given name is registered.
    /// If already registered, the callback fires immediately.
    /// </summary>
    /// <param name="name">The field name to wait for.</param>
    /// <param name="callback">Receives the id of the field; runs after the commit of that registration.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void WhenFieldRegistered(string name, Action<FieldId> callback);

    /// <summary>
    /// Registers a callback to be invoked when a protocol table with the given name is registered.
    /// If already registered, the callback fires immediately.
    /// </summary>
    /// <param name="name">The protocol table name to wait for.</param>
    /// <param name="callback">Receives the id of the table; runs after the commit of that registration.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void WhenProtocolTableRegistered(string name, Action<ProtocolTableId> callback);

    #endregion

    #region Stream Reassembly Configuration

    /// <summary>
    /// Registers a stream reassembly configuration for a protocol.
    /// Called by application protocols (HTTP, TLS, DNS-over-TCP) during registration
    /// to tell transport protocols (TCP) how to reassemble their PDUs.
    /// </summary>
    /// <param name="protocolId">The application protocol that requires reassembly.</param>
    /// <param name="config">The reassembly configuration (boundary detector, buffer limits, etc.).</param>
    /// <exception cref="RegistrationException">Thrown when a config is already registered for the given protocol.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="StackBuilder.Build"/> was already called.</exception>
    void RegisterStreamReassemblyConfig(ProtocolId protocolId, StreamReassemblyConfig config);

    #endregion
}
