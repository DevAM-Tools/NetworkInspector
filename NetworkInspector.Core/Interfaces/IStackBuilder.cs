// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// Build-phase interface for registering protocols, fields, dispatch tables, and settings.
/// <para>
/// All registration methods throw <see cref="RegistrationException"/> on failure
/// (e.g., duplicate names, missing tables). Errors are checked before state mutation,
/// so the builder is never left in an inconsistent state.
/// After registration is complete, call <see cref="StackBuilder.Build"/> to freeze
/// the builder into an immutable <see cref="Stack"/>.
/// </para>
/// </summary>
public interface IStackBuilder : IStack
{
    #region Protocol Registration

    /// <summary>Registers a protocol parser. Returns its unique <see cref="ProtocolId"/>.</summary>
    /// <param name="protocol">The protocol implementation to register.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when the protocol name is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when the protocol UI name is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the protocol name is already registered.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when the protocol name is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when the protocol UI name is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the protocol name is already registered.</exception>
    ProtocolId RegisterProtocol<TProtocol>(TProtocol protocol, Action<IStackBuilder, ProtocolId, TProtocol> callback)
        where TProtocol : IProtocol;

    #endregion

    #region Field Registration

    /// <summary>Registers a field definition without an index group.</summary>
    /// <param name="protocolId">The owning protocol (use <see cref="ProtocolId.Invalid"/> for built-in fields).</param>
    /// <param name="name">Machine-readable field name (e.g., "eth.dst"). Must be unique.</param>
    /// <param name="uiName">Human-readable display name (e.g., "Destination").</param>
    /// <param name="fieldType">The value type this field carries.</param>
    /// <param name="description">Optional description text.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the field name is already registered.</exception>
    FieldId RegisterField(
        ProtocolId protocolId,
        string name,
        string uiName,
        FieldType fieldType,
        string? description = null);

    /// <summary>
    /// Registers a field definition with an index group for efficient cross-packet indexing.
    /// Fields that always appear together should share the same group name.
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="indexGroup"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the field name is already registered.</exception>
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
    /// alias name remains invisible to <see cref="IStack.GetFieldId(string)"/>. Alias names
    /// occupy a namespace independent from field names and protocol-table names; an alias name
    /// can therefore coexist with a protocol-table name of the same string (e.g., <c>"udp.port"</c>)
    /// without collision.
    /// </para>
    /// <para>
    /// Member fields may carry heterogeneous <see cref="FieldType"/> values; the registry
    /// performs no same-type validation. Order of <paramref name="fieldIds"/> is preserved
    /// in <see cref="FieldAliasGroupInfo.Members"/>.
    /// </para>
    /// </summary>
    /// <param name="protocolId">The protocol that owns this alias group.</param>
    /// <param name="name">Machine-readable alias name (e.g., "eth.addr"). Must be unique inside the alias namespace.</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="fieldIds">
    /// The canonical member field IDs the alias resolves to. Must be non-empty and free of duplicates.
    /// Every ID must refer to a field already registered on this builder.
    /// </param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when an alias group with the same name is already registered.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="fieldIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fieldIds"/> is empty or contains duplicate field IDs.</exception>
    /// <exception cref="NotFoundRegistrationException">Thrown when any element of <paramref name="fieldIds"/> references a field ID not registered on this builder.</exception>
    /// <exception cref="ArgumentException">Thrown when any element of <paramref name="fieldIds"/> belongs to a different protocol than <paramref name="protocolId"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    IndexGroupId GetOrCreateIndexGroup(string name);

    #endregion

    #region Protocol Table Registration

    /// <summary>Registers a protocol dispatch table (used by parsers to dispatch to sub-protocols).</summary>
    /// <param name="name">Machine-readable table name (e.g., "eth.type"). Must be unique.</param>
    /// <param name="uiName">Human-readable display name.</param>
    /// <param name="keyType">The key type used for dispatch lookups.</param>
    /// <param name="description">Optional description text.</param>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the table name is already registered.</exception>
    ProtocolTableId RegisterProtocolTable(
        string name,
        string uiName,
        ProtocolTableKeyType keyType,
        string? description = null);

    /// <summary>Registers a protocol parser in a U64 dispatch table by table ID.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    void RegisterParserInU64Table(ProtocolTableId tableId, ulong key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a U64 dispatch table by table name.</summary>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    void RegisterParserInU64TableByName(string tableName, ulong key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a string dispatch table by table ID.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    void RegisterParserInStringTable(ProtocolTableId tableId, string key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a string dispatch table by table name.</summary>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    void RegisterParserInStringTableByName(string tableName, string key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bytes dispatch table by table ID.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    void RegisterParserInBytesTable(ProtocolTableId tableId, BytesKey key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bytes dispatch table by table name.</summary>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    void RegisterParserInBytesTableByName(string tableName, BytesKey key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bool dispatch table by table ID.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    void RegisterParserInBoolTable(ProtocolTableId tableId, bool key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a bool dispatch table by table name.</summary>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
    void RegisterParserInBoolTableByName(string tableName, bool key, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a catch-all dispatch table by table ID.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
    void RegisterParserInAnyTable(ProtocolTableId tableId, ProtocolId protocolId);

    /// <summary>Registers a protocol parser in a catch-all dispatch table by table name.</summary>
    /// <exception cref="RegistrationException">Thrown when the table name is not found.</exception>
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
    /// <b>Sort order:</b> Post-parsers are sorted once at build time: ascending by
    /// <paramref name="priority"/>, then ascending by registration order as a stable tie-breaker.
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="DuplicateNameRegistrationException">Thrown when the table name is already registered.</exception>
    HeuristicProtocolTableId RegisterHeuristicProtocolTable(
        ProtocolId owningProtocolId,
        string name,
        string uiName,
        string? description = null);

    /// <summary>Registers a heuristic parser in a heuristic dispatch table.</summary>
    /// <exception cref="RegistrationException">Thrown when the table ID is not found.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    void WhenProtocolRegistered(string name, Action<ProtocolId> callback);

    /// <summary>
    /// Registers a callback to be invoked when a field with the given name is registered.
    /// If already registered, the callback fires immediately.
    /// </summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
    void WhenFieldRegistered(string name, Action<FieldId> callback);

    /// <summary>
    /// Registers a callback to be invoked when a protocol table with the given name is registered.
    /// If already registered, the callback fires immediately.
    /// </summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated C-style identifier.</exception>
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
    void RegisterStreamReassemblyConfig(ProtocolId protocolId, StreamReassemblyConfig config);
    #endregion
}
