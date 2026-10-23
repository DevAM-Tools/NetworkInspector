// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using FieldInfo = NetworkInspector.Core.Infos.FieldInfo;

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Wraps the probe <see cref="IStackBuilder"/> that the registry uses to try a plugin's stack contributions at load time and
/// records what the contribution registered (protocols, heuristic parsers, post-parsers, reassembly configurations, and — by
/// diffing — settings) as named contributions of the plugin. Every other member forwards to the wrapped builder unchanged.
/// <para>
/// Thread safety: not thread-safe. It lives on the loading thread; a deferred callback (<c>WhenProtocolRegistered</c>) that
/// keeps this wrapper fires on the loading thread while another plugin is probed, and then records for the plugin that
/// registered the callback.
/// </para>
/// </summary>
internal sealed class RecordingStackBuilder : IStackBuilder
{
    #region Fields

    private readonly IStackBuilder _Inner;

    private readonly ContributionRecorder _Recorder;

    // Names of the settings that existed before this contribution ran; the registrar is a ref struct and cannot be wrapped,
    // so settings registered through it are found by comparing names afterwards.
    private readonly HashSet<string> _SettingsBefore;

    #endregion

    #region Lifecycle

    /// <summary>Creates a recording wrapper for one plugin's contribution.</summary>
    /// <param name="inner">The cumulative probe builder.</param>
    /// <param name="recorder">The recorder of the plugin being probed.</param>
    internal RecordingStackBuilder(IStackBuilder inner, ContributionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(recorder);
        _Inner = inner;
        _Recorder = recorder;
        _SettingsBefore = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReadOnlySettingView setting in inner.Settings.AllSettings)
        {
            _SettingsBefore.Add(setting.Name);
        }
    }

    #endregion

    #region Internal API

    /// <summary>Records the settings that were registered through the registrar since this wrapper was created.</summary>
    internal void RecordNewSettings()
    {
        foreach (ReadOnlySettingView setting in _Inner.Settings.AllSettings)
        {
            if (!_SettingsBefore.Contains(setting.Name))
            {
                _Recorder.Record(ContributionKind.Setting, setting.Name, setting.Description ?? setting.UiName);
            }
        }
    }

    #endregion

    #region Recording members

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol(IProtocol protocol)
    {
        ProtocolId id = _Inner.RegisterProtocol(protocol);
        _Recorder.Record(ContributionKind.Protocol, protocol.Name, protocol.Description ?? protocol.UiName);
        return id;
    }

    /// <inheritdoc/>
    public ProtocolId RegisterProtocol<TProtocol>(TProtocol protocol, Action<IStackBuilder, ProtocolId, TProtocol> callback)
        where TProtocol : IProtocol
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Same semantics as the Core builder — register, then call back — but the callback gets this recording wrapper, so
        // protocols that the callback registers are recorded too.
        ProtocolId id = RegisterProtocol(protocol);
        callback(this, id, protocol);
        return id;
    }

    /// <inheritdoc/>
    public PostParserId RegisterPostParser(ProtocolId protocolId, int priority = 0, string? description = null)
    {
        PostParserId id = _Inner.RegisterPostParser(protocolId, priority, description);
        string name = _Inner.GetProtocol(protocolId)?.Name ?? protocolId.ToString();
        _Recorder.Record(ContributionKind.PostParser, name, description ?? name);
        return id;
    }

    /// <inheritdoc/>
    public void RegisterHeuristicParser(HeuristicProtocolTableId tableId, IHeuristicParser parser)
    {
        _Inner.RegisterHeuristicParser(tableId, parser);
        _Recorder.Record(ContributionKind.Heuristic, parser.Name, parser.Description ?? parser.UiName);
    }

    /// <inheritdoc/>
    public void RegisterStreamReassemblyConfig(ProtocolId protocolId, StreamReassemblyConfig config)
    {
        _Inner.RegisterStreamReassemblyConfig(protocolId, config);
        string name = _Inner.GetProtocol(protocolId)?.Name ?? protocolId.ToString();
        _Recorder.Record(ContributionKind.Reassembly, name, "Stream reassembly configuration");
    }

    #endregion

    #region Forwarded registration members

    /// <inheritdoc/>
    public FieldId RegisterField(ProtocolId protocolId, string name, string uiName, FieldType fieldType, string? description = null) =>
        _Inner.RegisterField(protocolId, name, uiName, fieldType, description);

    /// <inheritdoc/>
    public FieldId RegisterFieldInGroup(ProtocolId protocolId, string name, string uiName, FieldType fieldType, string indexGroup,
        string? description = null) =>
        _Inner.RegisterFieldInGroup(protocolId, name, uiName, fieldType, indexGroup, description);

    /// <inheritdoc/>
    public FieldAliasGroupId RegisterFieldAliasGroup(ProtocolId protocolId, string name, string? description, FieldId[] fieldIds) =>
        _Inner.RegisterFieldAliasGroup(protocolId, name, description, fieldIds);

    /// <inheritdoc/>
    public IndexGroupId GetOrCreateIndexGroup(string name) => _Inner.GetOrCreateIndexGroup(name);

    /// <inheritdoc/>
    public ProtocolTableId RegisterProtocolTable(string name, string uiName, ProtocolTableKeyType keyType, string? description = null) =>
        _Inner.RegisterProtocolTable(name, uiName, keyType, description);

    /// <inheritdoc/>
    public void RegisterParserInU64Table(ProtocolTableId tableId, ulong key, ProtocolId protocolId) =>
        _Inner.RegisterParserInU64Table(tableId, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInU64TableByName(string tableName, ulong key, ProtocolId protocolId) =>
        _Inner.RegisterParserInU64TableByName(tableName, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInStringTable(ProtocolTableId tableId, string key, ProtocolId protocolId) =>
        _Inner.RegisterParserInStringTable(tableId, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInStringTableByName(string tableName, string key, ProtocolId protocolId) =>
        _Inner.RegisterParserInStringTableByName(tableName, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInBytesTable(ProtocolTableId tableId, BytesKey key, ProtocolId protocolId) =>
        _Inner.RegisterParserInBytesTable(tableId, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInBytesTableByName(string tableName, BytesKey key, ProtocolId protocolId) =>
        _Inner.RegisterParserInBytesTableByName(tableName, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInBoolTable(ProtocolTableId tableId, bool key, ProtocolId protocolId) =>
        _Inner.RegisterParserInBoolTable(tableId, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInBoolTableByName(string tableName, bool key, ProtocolId protocolId) =>
        _Inner.RegisterParserInBoolTableByName(tableName, key, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInAnyTable(ProtocolTableId tableId, ProtocolId protocolId) =>
        _Inner.RegisterParserInAnyTable(tableId, protocolId);

    /// <inheritdoc/>
    public void RegisterParserInAnyTableByName(string tableName, ProtocolId protocolId) =>
        _Inner.RegisterParserInAnyTableByName(tableName, protocolId);

    /// <inheritdoc/>
    public HeuristicProtocolTableId RegisterHeuristicProtocolTable(ProtocolId owningProtocolId, string name, string uiName,
        string? description = null) =>
        _Inner.RegisterHeuristicProtocolTable(owningProtocolId, name, uiName, description);

    /// <inheritdoc/>
    public SettingsRegistrar SettingsRegistrar => _Inner.SettingsRegistrar;

    /// <inheritdoc/>
    public void WhenProtocolRegistered(string name, Action<ProtocolId> callback) => _Inner.WhenProtocolRegistered(name, callback);

    /// <inheritdoc/>
    public void WhenFieldRegistered(string name, Action<FieldId> callback) => _Inner.WhenFieldRegistered(name, callback);

    /// <inheritdoc/>
    public void WhenProtocolTableRegistered(string name, Action<ProtocolTableId> callback) =>
        _Inner.WhenProtocolTableRegistered(name, callback);

    #endregion

    #region Forwarded lookup members

    /// <inheritdoc/>
    public ReadOnlyMemory<BuildDiagnostic> BuildDiagnostics => _Inner.BuildDiagnostics;

    /// <inheritdoc/>
    public FrameInterfaceRegistry FrameInterfaceRegistry => _Inner.FrameInterfaceRegistry;

    /// <inheritdoc/>
    public bool IncludeExceptionStackTrace => _Inner.IncludeExceptionStackTrace;

    /// <inheritdoc/>
    public ProtocolInfo? GetProtocol(ProtocolId id) => _Inner.GetProtocol(id);

    /// <inheritdoc/>
    public ProtocolId? GetProtocolId(string name) => _Inner.GetProtocolId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolInfo> Protocols => _Inner.Protocols;

    /// <inheritdoc/>
    public int ProtocolCount => _Inner.ProtocolCount;

    /// <inheritdoc/>
    public FieldInfo? GetField(FieldId id) => _Inner.GetField(id);

    /// <inheritdoc/>
    public FieldId? GetFieldId(string name) => _Inner.GetFieldId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldInfo> Fields => _Inner.Fields;

    /// <inheritdoc/>
    public int FieldCount => _Inner.FieldCount;

    /// <inheritdoc/>
    public IndexGroupId GetFieldIndexGroup(FieldId fieldId) => _Inner.GetFieldIndexGroup(fieldId);

    /// <inheritdoc/>
    public FieldAliasGroupInfo? GetFieldAliasGroup(FieldAliasGroupId id) => _Inner.GetFieldAliasGroup(id);

    /// <inheritdoc/>
    public FieldAliasGroupId? GetFieldAliasGroupId(string name) => _Inner.GetFieldAliasGroupId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<FieldAliasGroupInfo> FieldAliasGroups => _Inner.FieldAliasGroups;

    /// <inheritdoc/>
    public int FieldAliasGroupCount => _Inner.FieldAliasGroupCount;

    /// <inheritdoc/>
    public IndexGroupInfo? GetIndexGroup(IndexGroupId id) => _Inner.GetIndexGroup(id);

    /// <inheritdoc/>
    public IndexGroupId? GetIndexGroupId(string name) => _Inner.GetIndexGroupId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<IndexGroupInfo> IndexGroups => _Inner.IndexGroups;

    /// <inheritdoc/>
    public int IndexGroupCount => _Inner.IndexGroupCount;

    /// <inheritdoc/>
    public ProtocolTableInfo? GetProtocolTableInfo(ProtocolTableId id) => _Inner.GetProtocolTableInfo(id);

    /// <inheritdoc/>
    public ProtocolTableId? GetProtocolTableId(string name) => _Inner.GetProtocolTableId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolTableInfo> ProtocolTableInfos => _Inner.ProtocolTableInfos;

    /// <inheritdoc/>
    public int ProtocolTableCount => _Inner.ProtocolTableCount;

    /// <inheritdoc/>
    public ReadOnlyMemory<PostParserInfo> PostParsers => _Inner.PostParsers;

    /// <inheritdoc/>
    public int PostParserCount => _Inner.PostParserCount;

    /// <inheritdoc/>
    public HeuristicProtocolTableInfo? GetHeuristicProtocolTableInfo(HeuristicProtocolTableId id) =>
        _Inner.GetHeuristicProtocolTableInfo(id);

    /// <inheritdoc/>
    public HeuristicProtocolTableId? GetHeuristicProtocolTableId(string name) => _Inner.GetHeuristicProtocolTableId(name);

    /// <inheritdoc/>
    public ReadOnlyMemory<HeuristicProtocolTableInfo> HeuristicProtocolTableInfos => _Inner.HeuristicProtocolTableInfos;

    /// <inheritdoc/>
    public int HeuristicProtocolTableCount => _Inner.HeuristicProtocolTableCount;

    /// <inheritdoc/>
    public ReadOnlySettingsManagerView Settings => _Inner.Settings;

    /// <inheritdoc/>
    public StreamReassemblyConfig? GetStreamReassemblyConfig(ProtocolId protocolId) => _Inner.GetStreamReassemblyConfig(protocolId);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromU64ProtocolTable(ProtocolTableId tableId, ulong key) =>
        _Inner.GetProtocolsFromU64ProtocolTable(tableId, key);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromStringProtocolTable(ProtocolTableId tableId, string key) =>
        _Inner.GetProtocolsFromStringProtocolTable(tableId, key);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBytesProtocolTable(ProtocolTableId tableId, BytesKey key) =>
        _Inner.GetProtocolsFromBytesProtocolTable(tableId, key);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromBoolProtocolTable(ProtocolTableId tableId, bool key) =>
        _Inner.GetProtocolsFromBoolProtocolTable(tableId, key);

    /// <inheritdoc/>
    public ReadOnlySpan<ProtocolId> GetProtocolsFromAnyProtocolTable(ProtocolTableId tableId) =>
        _Inner.GetProtocolsFromAnyProtocolTable(tableId);

    /// <inheritdoc/>
    public ProtocolId? TryMatchHeuristic(HeuristicProtocolTableId tableId, ReadOnlyMemory<byte> data) =>
        _Inner.TryMatchHeuristic(tableId, data);

    /// <inheritdoc/>
    public ParseDelegate? ResolveParseDelegate(ProtocolId id) => _Inner.ResolveParseDelegate(id);

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? GetU64TableEntries(ProtocolTableId tableId) =>
        _Inner.GetU64TableEntries(tableId);

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? GetStringTableEntries(ProtocolTableId tableId) =>
        _Inner.GetStringTableEntries(tableId);

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? GetBytesTableEntries(ProtocolTableId tableId) =>
        _Inner.GetBytesTableEntries(tableId);

    /// <inheritdoc/>
    public IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? GetBoolTableEntries(ProtocolTableId tableId) =>
        _Inner.GetBoolTableEntries(tableId);

    /// <inheritdoc/>
    public ReadOnlyMemory<ProtocolId>? GetAnyTableProtocolIds(ProtocolTableId tableId) => _Inner.GetAnyTableProtocolIds(tableId);

    #endregion
}
