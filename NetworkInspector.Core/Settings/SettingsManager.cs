// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Central manager for application settings.
///
/// Provides thread-safe registration, access, persistence, and change notification.
/// Supports JSON-based persistence via <see cref="Load"/> and <see cref="Save"/>.
/// When no storage path is set, both <see cref="Load"/> and <see cref="Save"/>
/// throw <see cref="PersistenceSettingsException"/>.
/// <para>
/// Uses <see cref="ReaderWriterLockSlim"/> to allow concurrent readers while
/// serializing writers (registration, persistence, bulk mutations).
/// </para>
/// <para>
/// <b>Ownership and disposal:</b> <see cref="SettingsManager"/> implements
/// <see cref="IDisposable"/>. When passed to <see cref="StackBuilder"/>, ownership
/// transfers to the resulting <see cref="Stack"/>; callers must not dispose the
/// manager themselves after that point. When used standalone (e.g., in tests),
/// callers are responsible for calling <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> All public members are thread-safe. After <see cref="Dispose"/>
/// is called the instance must not be used from any thread.
/// </para>
/// </summary>
public sealed class SettingsManager : IDisposable
{
    /// <summary>Reusable JSON serializer options for saving settings.</summary>
    private static readonly JsonSerializerOptions _WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly string? _StoragePath;

    private readonly ReaderWriterLockSlim _Lock = new();
    private readonly Dictionary<string, Setting> _SettingsByName = new(StringComparer.Ordinal);
    private readonly List<Setting> _SettingsList = [];
    private readonly Dictionary<string, SettingGroup> _GroupsByName = new(StringComparer.Ordinal);
    private readonly List<string> _GroupsList = [];

    // Orphaned values: group name -> (setting name -> JSON node)
    // Values loaded from persistence before settings are registered are stored here
    // and automatically applied when the setting is later registered.
    private readonly Dictionary<string, Dictionary<string, JsonNode>> _OrphanedValues = [];

    // Pre-loaded typed values keyed by setting name. Populated via PreloadValue
    // before the corresponding setting is registered. Consumed in RegisterSetting and
    // applied to the registered setting at registration time. This is the in-process
    // counterpart to <see cref="_OrphanedValues"/> and avoids round-tripping through JSON
    // when a caller (e.g., a test harness) wants to override a setting before the protocol
    // that owns it has registered it.
    private readonly Dictionary<string, SettingValue> _PreloadedValues = new(StringComparer.Ordinal);

    /// <summary>Guards against double-dispose (0 = live, 1 = disposed). Written via <see cref="Interlocked"/>.</summary>
    private int _Disposed;

    private readonly ReadOnlySettingsView _ReadOnlyView;

    /// <summary>Creates a new settings manager without a storage path.</summary>
    public SettingsManager()
    {
        _StoragePath = null;
        _ReadOnlyView = new ReadOnlySettingsView(this);
    }

    /// <summary>Creates a new settings manager with a storage path for JSON persistence.</summary>
    public SettingsManager(string storagePath)
    {
        _StoragePath = storagePath;
        _ReadOnlyView = new ReadOnlySettingsView(this);
    }

    /// <summary>Gets the read-only view of this manager.</summary>
    public IReadOnlySettingsManager ReadOnly => _ReadOnlyView;

    /// <summary>Gets the storage path, or null if no storage path is configured.</summary>
    public string? StoragePath => _StoragePath;

    #region Counts

    /// <summary>Returns the number of registered settings.</summary>
    public int SettingCount
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return _SettingsList.Count;
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Returns the number of registered groups.</summary>
    public int GroupCount
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return _GroupsList.Count;
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Returns true if any settings have pending changes.</summary>
    public bool HasDirtySettings
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                foreach (Setting setting in _SettingsList)
                {
                    if (setting.IsDirty)
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    #endregion

    #region Group Registration

    /// <summary>
    /// Registers a group with explicit UI name and description.
    /// Groups can also be created implicitly when registering a setting with a new group name.
    /// </summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    /// <exception cref="DuplicateNameSettingsException">Thrown when a group with the same name already exists.</exception>
    public void RegisterGroup(
        string name, string uiName, string? description = null)
    {
        if (!NameValidation.IsValidGroupName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw InvalidNameSettingsException.ForUiName(uiName);
        }

        _Lock.EnterWriteLock();
        try
        {
            if (_GroupsByName.ContainsKey(name))
            {
                throw DuplicateNameSettingsException.ForGroup(name);
            }

            SettingGroup group = new(name, uiName, description);
            _GroupsByName[name] = group;
            _GroupsList.Add(name);
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    #endregion

    #region Setting Registration

    /// <summary>
    /// Registers a setting. If the group doesn't exist, it is created implicitly.
    /// If a persisted value exists (from <see cref="Load"/>), it is applied.
    /// </summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    /// <exception cref="DuplicateNameSettingsException">Thrown when a setting with the same name already exists.</exception>
    public SettingRegistrationResult RegisterSetting(Setting setting)
    {
        string name = setting.Name;
        string groupName = setting.GroupName;

        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!NameValidation.IsValidUiName(setting.UiName))
        {
            throw InvalidNameSettingsException.ForUiName(setting.UiName);
        }

        _Lock.EnterWriteLock();
        try
        {
            if (_SettingsByName.ContainsKey(name))
            {
                throw DuplicateNameSettingsException.ForSetting(name);
            }

            // Ensure group exists
            _EnsureGroupExistsLocked(groupName);

            // Apply any pre-loaded typed value (set via PreloadValue before the setting
            // existed) — takes precedence over orphaned JSON values.
            SettingLoadResult loadResult;
            if (_PreloadedValues.Remove(name, out SettingValue preloaded))
            {
                try
                {
                    setting.SetPendingValue(preloaded);
                    setting.Apply();
                    loadResult = SettingLoadResult.Success;
                }
                catch (SettingsException)
                {
                    loadResult = SettingLoadResult.OutOfRange;
                }
            }
            else
            {
                // Try to load persisted value from orphaned values
                loadResult = _TryLoadPersistedValueLocked(setting);
            }

            SettingValue currentValue = setting.Value;

            // Add to group
            if (_GroupsByName.TryGetValue(groupName, out SettingGroup? group))
            {
                group.AddSetting(setting);
            }

            // Store
            _SettingsByName[name] = setting;
            _SettingsList.Add(setting);

            return new SettingRegistrationResult(loadResult, currentValue);
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    /// <summary>Ensures a group exists, creating it implicitly if needed.</summary>
    private void _EnsureGroupExistsLocked(string groupName)
    {
        if (_GroupsByName.ContainsKey(groupName))
        {
            return;
        }

        string uiName = groupName.Length == 0
            ? SettingGroup.DefaultGroupUiName
            : groupName;

        SettingGroup group = new(groupName, uiName);
        _GroupsByName[groupName] = group;
        _GroupsList.Add(groupName);
    }

    /// <summary>Tries to load a persisted value for a setting from orphaned values.</summary>
    private SettingLoadResult _TryLoadPersistedValueLocked(Setting setting)
    {
        if (!_OrphanedValues.TryGetValue(setting.GroupName, out Dictionary<string, JsonNode>? groupValues))
        {
            return SettingLoadResult.NoPersistedValue;
        }

        if (!groupValues.TryGetValue(setting.Name, out JsonNode? jsonNode))
        {
            return SettingLoadResult.NoPersistedValue;
        }

        (SettingValue? value, SettingLoadResult parseResult) =
            _JsonToSettingValue(jsonNode, setting.Type, setting.EnumMetadata);
        if (value is null)
        {
            // Return the specific parse failure reason (TypeMismatch or DeserializationError).
            return parseResult;
        }

        try
        {
            setting.SetPendingValue(value.Value);
        }
        catch (SettingsException)
        {
            return SettingLoadResult.OutOfRange;
        }

        setting.Apply();

        // Remove from orphaned values since it's been consumed
        groupValues.Remove(setting.Name);
        if (groupValues.Count == 0)
        {
            _OrphanedValues.Remove(setting.GroupName);
        }

        return SettingLoadResult.Success;
    }

    /// <summary>
    /// Pre-loads a typed value for a setting that has not been registered yet.
    /// When the setting is later registered via <see cref="RegisterSetting"/>, the
    /// pre-loaded value is automatically applied (taking precedence over any
    /// JSON-orphaned value loaded from persistence).
    /// <para>
    /// Intended for hosts that need to override protocol settings before the protocols
    /// register them, e.g., test harnesses that build a stack with non-default values
    /// for settings that the protocol reads during <c>RegisterFields</c> (config-file
    /// paths, checksum-validation flags, etc.).
    /// </para>
    /// <para>
    /// If a setting with <paramref name="name"/> already exists, the value is not
    /// applied here — callers should set it directly on the registered setting.
    /// Subsequent calls with the same name overwrite the previously pre-loaded value.
    /// </para>
    /// </summary>
    /// <param name="name">The setting name to pre-load.</param>
    /// <param name="value">The typed value to apply when the setting is registered.</param>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> is not a valid setting name.</exception>
    public void PreloadValue(string name, SettingValue value)
    {
        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }

        _Lock.EnterWriteLock();
        try
        {
            _PreloadedValues[name] = value;
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    #endregion

    #region Querying

    /// <summary>Gets a setting by name.</summary>
    public Setting? GetSetting(string name)
    {
        _Lock.EnterReadLock();
        try
        {
            if (_SettingsByName.TryGetValue(name, out Setting? s))
            {
                return s;
            }
            return null;
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    /// <summary>Gets all settings (snapshot).</summary>
    public IReadOnlyList<Setting> AllSettings
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return [.. _SettingsList];
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    private IReadOnlyList<IReadOnlySetting> _AllSettings
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return [.. _SettingsList];
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets all group names (snapshot).</summary>
    public IReadOnlyList<string> AllGroups
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return [.. _GroupsList];
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Gets a group by name.</summary>
    public SettingGroup? GetGroup(string name)
    {
        _Lock.EnterReadLock();
        try
        {
            if (_GroupsByName.TryGetValue(name, out SettingGroup? g))
            {
                return g;
            }
            return null;
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    /// <summary>Gets all settings in a group. Delegates to the group's own snapshot to avoid a linear scan.</summary>
    public IReadOnlyList<Setting> GetSettingsInGroup(string groupName)
    {
        _Lock.EnterReadLock();
        try
        {
            if (_GroupsByName.TryGetValue(groupName, out SettingGroup? group))
            {
                return group.Settings;
            }
            return [];
        }
        finally
        {
            _Lock.ExitReadLock();
        }
    }

    #endregion

    #region Typed Accessors

    /// <summary>Convenience: gets a boolean setting value by name. Returns null if not found or not a Bool setting.</summary>
    public bool? GetBoolSetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsBool(out bool v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a string setting value by name. Returns null if not found or not a String setting.</summary>
    public string? GetStringSetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsString(out string v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a double setting value by name. Returns null if not found or not an F64 setting.</summary>
    public double? GetF64Setting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsF64(out double v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a ulong setting value by name. Returns null if not found or not a U64 setting.</summary>
    public ulong? GetU64Setting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsU64(out ulong v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a long setting value by name. Returns null if not found or not an I64 setting.</summary>
    public long? GetI64Setting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsI64(out long v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a byte array copy by name. Returns null if not found or not a Bytes setting.</summary>
    public byte[]? GetBytesSetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsBytes(out byte[] v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets an enum (name, numeric value) by name. Returns null if not found or not an Enum setting.</summary>
    public (string Name, ulong Value)? GetEnumSetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsEnum(out (string Name, ulong Value) v))
        {
            return v;
        }
        return null;
    }

    #endregion

    #region Change Notification

    /// <summary>
    /// Fired after <see cref="ApplyChanges"/> has applied at least one pending change.
    /// Carries the names of all settings that changed.
    /// </summary>
    public event EventHandler<SettingsAppliedEventArgs>? SettingsApplied;

    #endregion

    #region Bulk Operations

    /// <summary>Applies all pending changes, returns list of changed setting names,
    /// and fires <see cref="SettingsApplied"/> if at least one setting changed.</summary>
    public IReadOnlyList<string> ApplyChanges()
    {
        List<string> changed;
        _Lock.EnterWriteLock();
        try
        {
            changed = [];
            foreach (Setting setting in _SettingsList)
            {
                if (setting.IsDirty && setting.Apply())
                {
                    changed.Add(setting.Name);
                }
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }

        // Fire event outside the lock to avoid holding the lock during subscriber callbacks.
        if (changed.Count > 0)
        {
            SettingsApplied?.Invoke(this, new SettingsAppliedEventArgs(changed));
        }

        return changed;
    }

    /// <summary>Resets all pending changes to current values.</summary>
    public void ResetChanges()
    {
        _Lock.EnterWriteLock();
        try
        {
            foreach (Setting setting in _SettingsList)
            {
                setting.Reset();
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    /// <summary>Resets all settings to their default values.</summary>
    public void ResetAllToDefaults()
    {
        _Lock.EnterWriteLock();
        try
        {
            foreach (Setting setting in _SettingsList)
            {
                setting.ResetToDefault();
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    #endregion

    #region Orphaned Values

    /// <summary>Returns the number of orphaned value entries.</summary>
    public int OrphanedEntryCount
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                int count = 0;
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> group in _OrphanedValues)
                {
                    count += group.Value.Count;
                }
                return count;
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Clears orphaned entries.</summary>
    public void ClearOrphanedEntries()
    {
        _Lock.EnterWriteLock();
        try
        {
            _OrphanedValues.Clear();
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Loads settings from JSON files in the storage path.
    /// Each JSON file represents a group. The filename (without extension) is the group name.
    /// <c>default.json</c> maps to the default group (empty-string name).
    /// <para>
    /// Invalid or incompatible persisted values are skipped — they do not prevent loading.
    /// The returned list describes every skipped entry so callers can surface diagnostics.
    /// </para>
    /// </summary>
    /// <returns>
    /// A (possibly empty) list of non-fatal warnings encountered during loading.
    /// Each entry describes one skipped setting or skipped group file.
    /// </returns>
    /// <exception cref="PersistenceSettingsException">
    /// Thrown when no storage path is configured or on I/O orJSON errors.
    /// </exception>
    public IReadOnlyList<SettingsLoadWarning> Load()
    {
        if (_StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }
        if (!Directory.Exists(_StoragePath))
        {
            return [];
        }

        List<SettingsLoadWarning> warnings = [];
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(_StoragePath, "*.json"))
            {
                _LoadGroupFile(filePath, warnings);
            }
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
        return warnings;
    }

    /// <summary>
    /// Loads a single group file and appends any encountered warnings to <paramref name="warnings"/>.
    /// </summary>
    private void _LoadGroupFile(string filePath, List<SettingsLoadWarning> warnings)
    {
        try
        {
            string groupName = Path.GetFileNameWithoutExtension(filePath);
            // "default" maps to the empty-string default group
            if (groupName == "default")
            {
                groupName = SettingGroup.DefaultGroupName;
            }

            // Validate the group name derived from the file name.
            // Non-default group names must be lowercase dot-separated identifiers.
            if (!NameValidation.IsValidGroupName(groupName))
            {
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupName,
                    groupName,
                    string.Empty,
                    $"Group name '{groupName}' (from file '{Path.GetFileName(filePath)}') is not a valid group name. " +
                    "Group names must be lowercase dot-separated identifiers (e.g. 'my.group'). The file is skipped."));
                return;
            }

            string json = File.ReadAllText(filePath);
            JsonNode? root = JsonNode.Parse(json);
            if (root is not JsonObject obj)
            {
                // The file's root is not a JSON object — emit a diagnostic and skip.
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupFileShape,
                    groupName,
                    string.Empty,
                    $"Settings file '{Path.GetFileName(filePath)}' does not contain a JSON object at the root. " +
                    "Expected an object with setting name/value pairs. The file is skipped."));
                return;
            }

            // Collect what to apply and what to orphan
            List<(Setting Setting, SettingValue Value)> toApply = [];
            List<(string Name, JsonNode Value)> toOrphan = [];

            _Lock.EnterWriteLock();
            try
            {
                foreach (KeyValuePair<string, JsonNode?> prop in obj)
                {
                    if (prop.Value is null)
                    {
                        continue;
                    }

                    if (_SettingsByName.TryGetValue(prop.Key, out Setting? setting))
                    {
                        (SettingValue? value, SettingLoadResult parseResult) =
                            _JsonToSettingValue(prop.Value, setting.Type, setting.EnumMetadata);
                        if (value is not null)
                        {
                            toApply.Add((setting, value.Value));
                        }
                        else if (parseResult == SettingLoadResult.DeserializationError)
                        {
                            warnings.Add(new SettingsLoadWarning(
                                SettingsLoadWarningKind.DeserializationError,
                                groupName,
                                prop.Key,
                                $"Persisted value for '{prop.Key}' could not be decoded for setting type '{setting.Type}' " +
                                "(content format error, e.g. invalid base64 for a Bytes setting). The value is ignored."));
                        }
                        else
                        {
                            // The persisted JSON value's structural type does not match the registered setting type.
                            warnings.Add(new SettingsLoadWarning(
                                SettingsLoadWarningKind.TypeMismatch,
                                groupName,
                                prop.Key,
                                $"Persisted value for '{prop.Key}' has an incompatible type for setting type '{setting.Type}'. The value is ignored."));
                        }
                    }
                    else
                    {
                        toOrphan.Add((prop.Key, prop.Value));
                    }
                }

                // Apply collected settings; record constraint violations as OutOfRange warnings.
                foreach ((Setting setting, SettingValue value) in toApply)
                {
                    try
                    {
                        setting.SetPendingValue(value);
                        setting.Apply();
                    }
                    catch (SettingsException ex)
                    {
                        warnings.Add(new SettingsLoadWarning(
                            SettingsLoadWarningKind.OutOfRange,
                            setting.GroupName,
                            setting.Name,
                            $"Persisted value for '{setting.Name}' failed validation: {ex.Message} The value is ignored."));
                    }
                }

                // Store orphaned values for later resolution when the setting is registered.
                if (toOrphan.Count > 0)
                {
                    if (!_OrphanedValues.TryGetValue(groupName, out Dictionary<string, JsonNode>? orphans))
                    {
                        orphans = [];
                        _OrphanedValues[groupName] = orphans;
                    }
                    foreach ((string name, JsonNode node) in toOrphan)
                    {
                        orphans[name] = node;
                    }
                }
            }
            finally
            {
                _Lock.ExitWriteLock();
            }
        }
        catch (JsonException ex)
        {
            throw PersistenceSettingsException.ForJson(ex);
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
    }

    /// <summary>
    /// Saves all settings to JSON files in the storage path.
    /// One file per group, pretty-printed.
    /// </summary>
    /// <exception cref="PersistenceSettingsException">Thrown when no storage path is configured or on I/O/JSON errors.</exception>
    public void Save()
    {
        if (_StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        try
        {
            Directory.CreateDirectory(_StoragePath);

            // Group settings by group name
            Dictionary<string, JsonObject> groups = [];

            _Lock.EnterReadLock();
            try
            {
                foreach (Setting setting in _SettingsList)
                {
                    string groupName = setting.GroupName.Length == 0 ? "default" : setting.GroupName;

                    if (!groups.TryGetValue(groupName, out JsonObject? obj))
                    {
                        obj = [];
                        groups[groupName] = obj;
                    }

                    obj[setting.Name] = _SettingValueToJson(setting.Value);
                }
            }
            finally
            {
                _Lock.ExitReadLock();
            }

            // Write each group
            foreach (KeyValuePair<string, JsonObject> group in groups)
            {
                string filePath = Path.Combine(_StoragePath, $"{group.Key}.json");
                string json = group.Value.ToJsonString(_WriteOptions);
                File.WriteAllText(filePath, json);
            }
        }
        catch (JsonException ex)
        {
            throw PersistenceSettingsException.ForJson(ex);
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
    }

    #endregion

    #region JSON Conversion

    /// <summary>
    /// Converts a JSON node to a setting value based on the setting type.
    /// Returns a tuple of the parsed value (or null) and a <see cref="SettingLoadResult"/> discriminant.
    /// <list type="bullet">
    ///   <item><see cref="SettingLoadResult.Success"/> — value parsed successfully.</item>
    ///   <item><see cref="SettingLoadResult.TypeMismatch"/> — JSON node's structural type does not match the setting type.</item>
    ///   <item><see cref="SettingLoadResult.DeserializationError"/> — JSON node type matches but content cannot be decoded (e.g. invalid base64).</item>
    /// </list>
    /// </summary>
    private static (SettingValue? Value, SettingLoadResult Result) _JsonToSettingValue(
        JsonNode node, SettingType type, EnumSettingMetadata? enumMetadata)
    {
        try
        {
            if (type == SettingType.Bytes)
            {
                // Bytes parsing is handled separately because the FormatException from
                // invalid base64 is a DeserializationError, not a TypeMismatch.
                return _TryParseBytesWithResult(node);
            }

            SettingValue? value = type switch
            {
                SettingType.Bool => (SettingValue?)SettingValue.Bool(node.GetValue<bool>()),
                SettingType.String => node.GetValue<string>() is { } s ? (SettingValue?)SettingValue.String(s) : null,
                SettingType.F64 => _TryParseFiniteF64(node),
                SettingType.U64 => _TryParseU64(node),
                SettingType.I64 => _TryParseI64(node),
                SettingType.Enum => _TryParseEnum(node),
                _ => null,
            };
            return (value, value is not null ? SettingLoadResult.Success : SettingLoadResult.TypeMismatch);
        }
        catch (InvalidOperationException)
        {
            // JSON node structural type doesn't match expected setting type (e.g., got array for bool).
            return (null, SettingLoadResult.TypeMismatch);
        }
        catch (FormatException)
        {
            // JSON value cannot be converted to the target number/string format.
            return (null, SettingLoadResult.DeserializationError);
        }
    }

    /// <summary>Tries to parse a finite <see cref="SettingType.F64"/> from a JSON node. Returns null for NaN or Infinity.</summary>
    private static SettingValue? _TryParseFiniteF64(JsonNode node)
    {
        if (node is JsonValue val && val.TryGetValue(out double d) && double.IsFinite(d))
        {
            return SettingValue.F64(d);
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.U64"/> from a JSON node.</summary>
    private static SettingValue? _TryParseU64(JsonNode node)
    {
        // Try direct numeric first, then string parsing
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out ulong u))
            {
                return SettingValue.U64(u);
            }
            if (val.TryGetValue(out long l) && l >= 0)
            {
                return SettingValue.U64((ulong)l);
            }
            if (val.TryGetValue(out string? s)
                && ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
            {
                return SettingValue.U64(parsed);
            }
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.I64"/> from a JSON node.</summary>
    private static SettingValue? _TryParseI64(JsonNode node)
    {
        if (node is JsonValue val)
        {
            if (val.TryGetValue(out long l))
            {
                return SettingValue.I64(l);
            }
            if (val.TryGetValue(out string? s)
                && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return SettingValue.I64(parsed);
            }
        }
        return null;
    }

    /// <summary>Tries to parse a <see cref="SettingType.Bytes"/> from a base64-encoded JSON string.
    /// Distinguishes <see cref="SettingLoadResult.TypeMismatch"/> (node is not a JSON string)
    /// from <see cref="SettingLoadResult.DeserializationError"/> (string is not valid base64).</summary>
    private static (SettingValue? Value, SettingLoadResult Result) _TryParseBytesWithResult(JsonNode node)
    {
        if (node is not JsonValue val || !val.TryGetValue(out string? s))
        {
            // Node is not a JSON string — structurally wrong type.
            return (null, SettingLoadResult.TypeMismatch);
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(s);
            return (SettingValue.Bytes(bytes), SettingLoadResult.Success);
        }
        catch (FormatException)
        {
            // String is not valid base64 — content error, correct structural type.
            return (null, SettingLoadResult.DeserializationError);
        }
    }

    /// <summary>Tries to parse a <see cref="SettingType.Enum"/> from a JSON object with name/value properties.</summary>
    private static SettingValue? _TryParseEnum(JsonNode node)
    {
        // Expect object with "name" and "value" properties
        if (node is not JsonObject obj)
        {
            return null;
        }
        if (obj["name"] is not JsonValue nameVal)
        {
            return null;
        }
        if (obj["value"] is not JsonValue valueVal)
        {
            return null;
        }

        if (!nameVal.TryGetValue(out string? name))
        {
            return null;
        }

        if (valueVal.TryGetValue(out ulong numVal))
        {
            return SettingValue.Enum(name, numVal);
        }

        if (valueVal.TryGetValue(out long longVal) && longVal >= 0)
        {
            return SettingValue.Enum(name, (ulong)longVal);
        }

        if (valueVal.TryGetValue(out string? strVal)
            && ulong.TryParse(strVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed))
        {
            return SettingValue.Enum(name, parsed);
        }

        return null;
    }

    /// <summary>Converts a setting value to a JSON node.</summary>
    private static JsonNode? _SettingValueToJson(SettingValue value)
    {
        switch (value.Type)
        {
            case SettingType.Bool:
                value.TryGetAsBool(out bool boolVal);
                return JsonValue.Create(boolVal);
            case SettingType.String:
                value.TryGetAsString(out string strVal);
                return JsonValue.Create(strVal);
            case SettingType.F64:
                return _SettingF64ToJson(value);
            case SettingType.U64:
                value.TryGetAsU64(out ulong u64Val);
                return JsonValue.Create(u64Val);
            case SettingType.I64:
                value.TryGetAsI64(out long i64Val);
                return JsonValue.Create(i64Val);
            case SettingType.Bytes:
                value.TryGetAsBytes(out byte[] bytesVal);
                return JsonValue.Create(Convert.ToBase64String(bytesVal));
            case SettingType.Enum:
                return _CreateEnumJson(value);
            default:
                return null;
        }
    }

    /// <summary>Converts an F64 setting to a JSON value. F64 settings are always finite (enforced at registration and mutation).</summary>
    private static JsonValue _SettingF64ToJson(SettingValue value)
    {
        value.TryGetAsF64(out double f64);
        // F64 settings are guaranteed finite by Setting.F64() and Setting.ValidateF64().
        // Guard against invariant violations that would produce invalid JSON.
        if (!double.IsFinite(f64))
        {
            ThrowHelpers.ThrowNonFiniteF64(f64);
        }
        return JsonValue.Create(f64);
    }

    /// <summary>Creates a JSON object for an enum setting value.</summary>
    private static JsonObject _CreateEnumJson(SettingValue value)
    {
        value.TryGetAsEnum(out (string name, ulong numericValue) e);
        return new JsonObject
        {
            ["name"] = JsonValue.Create(e.name),
            ["value"] = JsonValue.Create(e.numericValue),
        };
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases the internal <see cref="ReaderWriterLockSlim"/>.
    /// When ownership has transferred to a <see cref="Stack"/>, this is called
    /// automatically by <see cref="Stack.Dispose"/>. In standalone usage (e.g., tests)
    /// callers must call this directly.
    /// Idempotent — safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) == 0)
        {
            // Dispose all group-owned ReaderWriterLockSlim instances before the manager lock.
            // SettingGroup documents that its lock lifetime is managed by the owning SettingsManager,
            // so this is the single canonical disposal site for every group lock.
            foreach (SettingGroup group in _GroupsByName.Values)
            {
                group.DisposeResources();
            }
            _Lock.Dispose();
        }
    }

    /// <summary>Internal alias called by <see cref="Stack.Dispose"/> via the existing disposal path.</summary>
    internal void DisposeResources() => Dispose();
    #endregion

    private sealed class ReadOnlySettingsView(SettingsManager owner) : IReadOnlySettingsManager
    {
        public int SettingCount => owner.SettingCount;

        public int GroupCount => owner.GroupCount;

        public IReadOnlySetting? GetSetting(string name) => owner.GetSetting(name);

        public IReadOnlyList<IReadOnlySetting> AllSettings => owner._AllSettings;

        public IReadOnlyList<string> AllGroups => owner.AllGroups;

        public IReadOnlySettingGroup? GetGroup(string name) => owner.GetGroup(name);

        public IReadOnlyList<IReadOnlySetting> GetSettingsInGroup(string groupName) =>
            owner.GetSettingsInGroup(groupName);

        public bool? GetBoolSetting(string name) => owner.GetBoolSetting(name);

        public string? GetStringSetting(string name) => owner.GetStringSetting(name);

        public double? GetF64Setting(string name) => owner.GetF64Setting(name);

        public ulong? GetU64Setting(string name) => owner.GetU64Setting(name);

        public long? GetI64Setting(string name) => owner.GetI64Setting(name);
    }
}
