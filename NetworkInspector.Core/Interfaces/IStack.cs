// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

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
    ///     <see cref="NetworkInspector.Core.Protocols.IProtocol.OnStart(Stack)"/> hook.
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

    /// <summary>Looks up a field ID by name. Returns <c>null</c> if not found.</summary>
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

    /// <summary>All registered post-parsers, sorted by priority.</summary>
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
    /// Read-only view of the settings manager.
    /// Provides access to all registered settings, groups, and typed accessors.
    /// </summary>
    IReadOnlySettingsManager Settings
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
}
