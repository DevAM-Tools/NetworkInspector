// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

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

    /// <summary>Registers a post-parser that runs after the main protocol parse completes.</summary>
    /// <param name="protocolId">The protocol this post-parser is associated with.</param>
    /// <param name="priority">Execution priority (lower values run first).</param>
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