// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Central manager for application settings.
///
/// Provides thread-safe registration, access, persistence, and change notification.
/// Supports JSON-based persistence via <see cref="Load"/> and <see cref="Save"/>.
/// When no storage path is set, both <see cref="Load"/> and <see cref="Save"/>
/// throw <see cref="PersistenceSettingsException"/>; file-system failures, including access denials, throw it as well.
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
/// <see cref="MaxConfigFileBytes"/> and <see cref="MaxJsonDepth"/> are assigned in the
/// constructor and never change; readers do not take the instance reader-writer lock for those fields.
/// </para>
/// </summary>
public sealed partial class SettingsManager : IDisposable
{
    /// <summary>
    /// Default maximum size in bytes for persisted settings JSON and referenced protocol config files (1 GiB).
    /// A 1 GiB load fully deserializes JSON and can exhaust memory. Values <c>&lt;= 0</c> disable the size check.
    /// </summary>
    public const long DefaultMaxConfigFileBytes = 1_073_741_824;

    /// <summary>
    /// Default maximum JSON nesting depth for persisted settings JSON and referenced protocol config files.
    /// Values <c>&lt;= 0</c> disable the depth check (System.Text.Json still has a library ceiling).
    /// </summary>
    public const int DefaultMaxJsonDepth = 1024;

    /// <summary>
    /// Depth passed to System.Text.Json when the caller disables <see cref="MaxJsonDepth"/>.
    /// STJ treats <c>0</c> as "use the default of 64", so a large positive ceiling is required.
    /// </summary>
    internal const int UnboundedJsonMaxDepth = 1_000_000;

    /// <summary>Reusable JSON serializer options for saving settings.</summary>
    private static readonly JsonSerializerOptions _WriteOptions = new()
    {
        WriteIndented = true
    };
    private readonly ReaderWriterLockSlim _Lock = new();
    private readonly Dictionary<string, Setting> _SettingsByName = new(StringComparer.Ordinal);
    private readonly List<Setting> _SettingsList = [];
    private readonly Dictionary<string, SettingGroup> _GroupsByName = new(StringComparer.Ordinal);
    private readonly List<string> _GroupsList = [];

    // Orphaned values: group name -> (setting name -> JSON node)
    // Values loaded from persistence before settings are registered are stored here
    // and automatically applied when the setting is later registered.
    private readonly Dictionary<string, Dictionary<string, JsonNode>> _OrphanedValues = [];

    // Group files this manager saw at Load or wrote itself, and whether Load ran. Save deletes only known files as stale: a
    // file that another manager or process created after Load is not this manager's to delete; a manager that never loaded
    // has seen nothing and replaces everything it owns (all manifest files).
    // Lock-free by design (copy on write): the snapshot is immutable and replaced with Interlocked.CompareExchange. It changes
    // once per Load, Save, or SaveChanged and is read once per Save, so a new small set per change costs nothing that matters,
    // and no caller ever waits for another.
    private KnownGroupFiles _KnownGroupFiles = KnownGroupFiles.Empty;

    // Pre-loaded typed values keyed by setting name. Populated via PreloadValue
    // before the corresponding setting is registered. Consumed in RegisterSetting and
    // applied to the registered setting at registration time. This is the in-process
    // counterpart to <see cref="_OrphanedValues"/> and avoids round-tripping through JSON
    // when a caller (e.g., a test harness) wants to override a setting before the protocol
    // that owns it has registered it.
    private readonly Dictionary<string, SettingValue> _PreloadedValues = new(StringComparer.Ordinal);

    /// <summary>Guards against double-dispose (0 = live, 1 = disposed). Written via <see cref="Interlocked"/>.</summary>
    private volatile int _Disposed;

    /// <summary>Non-zero while <see cref="Load"/> is applying persisted values (0 = idle, 1 = loading).</summary>
    private volatile int _IsLoading;

    /// <summary>
    /// Creates a new settings manager without a storage path.
    /// <see cref="MaxConfigFileBytes"/> is <see cref="DefaultMaxConfigFileBytes"/>.
    /// <see cref="MaxJsonDepth"/> is <see cref="DefaultMaxJsonDepth"/>.
    /// </summary>
    public SettingsManager()
        : this(storagePath: null, DefaultMaxConfigFileBytes, DefaultMaxJsonDepth)
    {
    }

    /// <summary>
    /// Creates a new settings manager with a storage path for JSON persistence.
    /// <see cref="MaxConfigFileBytes"/> is <see cref="DefaultMaxConfigFileBytes"/>.
    /// <see cref="MaxJsonDepth"/> is <see cref="DefaultMaxJsonDepth"/>.
    /// </summary>
    public SettingsManager(string storagePath)
        : this(storagePath, DefaultMaxConfigFileBytes, DefaultMaxJsonDepth)
    {
    }

    /// <summary>
    /// Creates a new settings manager with a storage path, file-size cap, and JSON depth cap.
    /// Both caps are fixed for the lifetime of the instance. Values <c>&lt;= 0</c> disable that check.
    /// </summary>
    /// <param name="storagePath">
    /// Directory for JSON persistence, or <see langword="null"/> when this manager does not persist.
    /// </param>
    /// <param name="maxConfigFileBytes">
    /// Maximum accepted size in bytes for settings group JSON, the owned-group manifest, and
    /// referenced protocol config files. Unit is bytes. <c>&lt;= 0</c> disables the size check.
    /// Loading a large file fully deserializes JSON and can exhaust memory.
    /// </param>
    /// <param name="maxJsonDepth">
    /// Maximum JSON nesting depth for those same files. <c>&lt;= 0</c> disables the depth check.
    /// </param>
    public SettingsManager(
        string? storagePath,
        long maxConfigFileBytes,
        int maxJsonDepth = DefaultMaxJsonDepth)
    {
        StoragePath = storagePath;
        MaxConfigFileBytes = maxConfigFileBytes;
        MaxJsonDepth = maxJsonDepth;
    }

    /// <summary>
    /// Gets a zero-allocation read-only view of this manager.
    /// Keep the compile-time type as <see cref="ReadOnlySettingsManagerView"/> or pass it to a
    /// generic <c>where TSettings : IReadOnlySettingsManager</c> parameter. Do not assign the
    /// result to <see cref="IReadOnlySettingsManager"/> — that boxes.
    /// </summary>
    public ReadOnlySettingsManagerView ReadOnly => new(this);

    /// <summary>Alias for <see cref="ReadOnly"/>.</summary>
    public ReadOnlySettingsManagerView AsReadOnlyView() => new(this);

    /// <summary>Gets the storage path, or null if no storage path is configured.</summary>
    public string? StoragePath { get; }

    /// <summary>
    /// Maximum accepted size in bytes for persisted settings JSON and referenced protocol config files.
    /// Values <c>&lt;= 0</c> disable the size check. Fixed at construction; there is no setter.
    /// </summary>
    public long MaxConfigFileBytes { get; }

    /// <summary>
    /// Maximum JSON nesting depth for persisted settings JSON and referenced protocol config files.
    /// Values <c>&lt;= 0</c> disable the depth check. Fixed at construction; there is no setter.
    /// </summary>
    public int MaxJsonDepth { get; }

    /// <summary>
    /// Maps a caller depth cap to the value System.Text.Json accepts.
    /// <c>&lt;= 0</c> becomes <see cref="UnboundedJsonMaxDepth"/> because STJ <c>MaxDepth == 0</c>
    /// means the library default of 64, not unlimited.
    /// </summary>
    internal static int EffectiveJsonMaxDepth(int maxJsonDepth)
    {
        if (maxJsonDepth <= 0)
        {
            return UnboundedJsonMaxDepth;
        }

        return maxJsonDepth;
    }

    /// <summary>Returns true while <see cref="Load"/> is applying persisted values.</summary>
    internal bool IsLoading => _IsLoading != 0;

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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="setting"/> is null.</exception>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    /// <exception cref="DuplicateNameSettingsException">Thrown when a setting with the same name already exists.</exception>
    public SettingRegistrationResult RegisterSetting(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
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

            setting.BindToManager(this);

            // Apply any pre-loaded typed value (set via PreloadValue before the setting
            // existed) — takes precedence over orphaned JSON values.
            SettingLoadResult loadResult;
            if (_PreloadedValues.Remove(name, out SettingValue preloaded))
            {
                try
                {
                    // Same path as an orphaned persisted value: validate, then set current and pending. Unlike
                    // SetPendingValue + Apply it does not refuse while Load runs, so registering during Load cannot
                    // throw after the preload was consumed and the group was created.
                    setting.ApplyFromPersistence(preloaded);
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

        (SettingValue? value, SettingLoadResult parseResult, _) =
            SettingValueJson.FromNode(jsonNode, setting.Type, setting.EnumMetadata, setting.GroupName, setting.Name);
        if (value is null)
        {
            // Return the specific parse failure reason (TypeMismatch or DeserializationError).
            return parseResult;
        }

        try
        {
            setting.ApplyFromPersistence(value.Value);
        }
        catch (SettingsException)
        {
            return SettingLoadResult.OutOfRange;
        }

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

    private IReadOnlyList<ReadOnlySettingView> _AllSettings
    {
        get
        {
            _Lock.EnterReadLock();
            try
            {
                return ReadOnlySettingView.Wrap(_SettingsList);
            }
            finally
            {
                _Lock.ExitReadLock();
            }
        }
    }

    /// <summary>Snapshot of all settings as read-only struct views (used by <see cref="ReadOnlySettingsManagerView"/>).</summary>
    internal IReadOnlyList<ReadOnlySettingView> ReadOnlyAllSettings => _AllSettings;

    /// <summary>Group settings snapshot as read-only struct views (used by <see cref="ReadOnlySettingsManagerView"/>).</summary>
    internal IReadOnlyList<ReadOnlySettingView> GetSettingsInGroupAsReadOnly(string groupName) =>
        ReadOnlySettingView.Wrap(GetSettingsInGroup(groupName));

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
                return group.CopySettings();
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

    /// <summary>
    /// Convenience: gets a boolean array copy by name.
    /// Returns a defensive copy, or <see langword="null"/> when the name is unregistered
    /// or the setting is not <see cref="SettingType.BoolArray"/>.
    /// </summary>
    public bool[]? GetBoolArraySetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsBoolArray(out bool[] v))
        {
            return v;
        }
        return null;
    }

    /// <summary>
    /// Convenience: gets a string array copy by name.
    /// Returns a defensive copy, or <see langword="null"/> when the name is unregistered
    /// or the setting is not <see cref="SettingType.StringArray"/>.
    /// </summary>
    public string[]? GetStringArraySetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsStringArray(out string[] v))
        {
            return v;
        }
        return null;
    }

    /// <summary>
    /// Convenience: gets a double array copy by name.
    /// Returns a defensive copy, or <see langword="null"/> when the name is unregistered
    /// or the setting is not <see cref="SettingType.F64Array"/>.
    /// </summary>
    public double[]? GetF64ArraySetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsF64Array(out double[] v))
        {
            return v;
        }
        return null;
    }

    /// <summary>
    /// Convenience: gets a ulong array copy by name.
    /// Returns a defensive copy, or <see langword="null"/> when the name is unregistered
    /// or the setting is not <see cref="SettingType.U64Array"/>.
    /// </summary>
    public ulong[]? GetU64ArraySetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsU64Array(out ulong[] v))
        {
            return v;
        }
        return null;
    }

    /// <summary>
    /// Convenience: gets a long array copy by name.
    /// Returns a defensive copy, or <see langword="null"/> when the name is unregistered
    /// or the setting is not <see cref="SettingType.I64Array"/>.
    /// </summary>
    public long[]? GetI64ArraySetting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsI64Array(out long[] v))
        {
            return v;
        }
        return null;
    }

    #endregion

    #region Change Notification

    /// <summary>
    /// Fired after <see cref="ApplyChanges()"/> has applied at least one pending change.
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

    /// <summary>Resets all pending changes to current values and raises <see cref="PendingChanged"/> for each changed setting.</summary>
    public void ResetChanges()
    {
        List<string> changed = [];
        _Lock.EnterWriteLock();
        try
        {
            foreach (Setting setting in _SettingsList)
            {
                if (setting.ResetWithoutEvent())
                {
                    changed.Add(setting.Name);
                }
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }

        _RaisePendingChanged(changed);
    }

    /// <summary>Resets all settings to their default values and raises <see cref="PendingChanged"/> for each setting whose pending value changed.</summary>
    public void ResetAllToDefaults()
    {
        List<string> changed = [];
        _Lock.EnterWriteLock();
        try
        {
            foreach (Setting setting in _SettingsList)
            {
                if (setting.ResetToDefaultWithoutEvent())
                {
                    changed.Add(setting.Name);
                }
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }

        _RaisePendingChanged(changed);
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
    /// Requires exclusive access to registered settings while applying persisted values.
    /// Concurrent overlapping <see cref="Load"/> calls throw <see cref="InvalidOperationException"/>.
    /// Concurrent <see cref="Setting.SetPendingValue"/> / <see cref="Setting.Apply"/> calls
    /// on registered settings throw <see cref="InvalidOperationException"/> until load completes.
    /// </para>
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
    /// Thrown when no storage path is configured or on I/O, access, or JSON errors.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another <see cref="Load"/> is already in progress.
    /// </exception>
    public IReadOnlyList<SettingsLoadWarning> Load()
    {
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        // From here on the manager has seen the folder (also an empty or missing one): files that appear later are not its own.
        _RecordKnownGroupFiles(loaded: true, [], []);

        if (!Directory.Exists(StoragePath))
        {
            return [];
        }

        List<SettingsLoadWarning> warnings = [];
        if (Interlocked.CompareExchange(ref _IsLoading, 1, 0) != 0)
        {
            throw new InvalidOperationException("SettingsManager.Load() is already in progress.");
        }
        List<string> seen = [];
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(StoragePath, "*.json"))
            {
                _LoadGroupFile(filePath, warnings);
                seen.Add(Path.GetFileName(filePath));
            }
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PersistenceSettingsException.ForAccessDenied(ex);
        }
        finally
        {
            // Also after an error: the files read before it were seen.
            _RecordKnownGroupFiles(loaded: true, seen, []);
            _ = Interlocked.Exchange(ref _IsLoading, 0);
        }
        return warnings;
    }

    /// <summary>
    /// Loads a single group file and appends any encountered warnings to <paramref name="warnings"/>.
    /// </summary>
    private void _LoadGroupFile(string filePath, List<SettingsLoadWarning> warnings)
    {
        string groupName = Path.GetFileNameWithoutExtension(filePath);
        try
        {
            // "default" maps to the empty-string default group
            if (groupName == "default")
            {
                groupName = SettingGroup.DefaultGroupName;
            }

            // Validate the group name derived from the file name.
            // Non-default group names must be lowercase dot-separated identifiers.
            string fileLabel = SettingsFileAccess.SafeFileLabel(filePath);
            if (!NameValidation.IsValidGroupName(groupName))
            {
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupName,
                    groupName,
                    string.Empty,
                    $"Group name '{groupName}' (from file '{fileLabel}') is not a valid group name. " +
                    "Group names must be lowercase dot-separated identifiers (e.g. 'my.group'). The file is skipped."));
                return;
            }

            // Size cap on the reported length first, then on the bytes actually read: the file can grow between the
            // length check and the parse, and a non-regular file reports no useful length.
            JsonNode? root;
            using (FileStream stream = SettingsFileAccess.OpenSharedRead(filePath))
            {
                if (MaxConfigFileBytes > 0 && stream.Length > MaxConfigFileBytes)
                {
                    warnings.Add(_OversizedGroupFileWarning(groupName, fileLabel));
                    return;
                }

                using BoundedReadStream? bounded = MaxConfigFileBytes > 0 ? new BoundedReadStream(stream, MaxConfigFileBytes) : null;
                try
                {
                    root = JsonNode.Parse(
                        bounded ?? (Stream)stream,
                        nodeOptions: null,
                        documentOptions: new JsonDocumentOptions { MaxDepth = EffectiveJsonMaxDepth(MaxJsonDepth) });
                }
                catch (JsonException) when (bounded is { Exceeded: true })
                {
                    // Cut at the cap: report the size, not the syntax error the cut produced.
                    warnings.Add(_OversizedGroupFileWarning(groupName, fileLabel));
                    return;
                }

                if (bounded is { Exceeded: true })
                {
                    warnings.Add(_OversizedGroupFileWarning(groupName, fileLabel));
                    return;
                }
            }
            if (root is not JsonObject obj)
            {
                // The file's root is not a JSON object — emit a diagnostic and skip.
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupFileShape,
                    groupName,
                    string.Empty,
                    $"Settings file '{fileLabel}' does not contain a JSON object at the root. " +
                    "Expected an object with setting name/value pairs. The file is skipped."));
                return;
            }

            // Enumerating a JsonObject decodes and indexes every property name first. A repeated or undecodable name
            // makes the file invalid JSON for settings: skip it like any other syntax error instead of aborting Load.
            try
            {
                _ = obj.Count;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupFileSyntax,
                    groupName,
                    string.Empty,
                    $"Settings file '{fileLabel}' contains invalid JSON: {ex.Message} The file is skipped."));
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
                        (SettingValue? value, SettingLoadResult parseResult, SettingsLoadWarning? parseWarning) =
                            SettingValueJson.FromNode(prop.Value, setting.Type, setting.EnumMetadata, groupName, prop.Key);
                        if (parseWarning is not null)
                        {
                            warnings.Add(parseWarning.Value);
                        }
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
                        setting.ApplyFromPersistence(value);
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
            warnings.Add(new SettingsLoadWarning(
                SettingsLoadWarningKind.InvalidGroupFileSyntax,
                groupName,
                string.Empty,
                $"Settings file '{SettingsFileAccess.SafeFileLabel(filePath)}' contains invalid JSON: {ex.Message} The file is skipped."));
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PersistenceSettingsException.ForAccessDenied(ex);
        }
    }

    /// <summary>Warning for a group file above <see cref="MaxConfigFileBytes"/> (reported length or bytes read).</summary>
    private SettingsLoadWarning _OversizedGroupFileWarning(string groupName, string fileLabel) =>
        new(
            SettingsLoadWarningKind.InvalidGroupFileShape,
            groupName,
            string.Empty,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Settings file '{fileLabel}' exceeds {MaxConfigFileBytes} bytes. The file is skipped."));

    /// <summary>
    /// Saves the settings to JSON files in the storage path: one pretty-printed file per group.
    /// <para>
    /// <b>Sparse:</b> only values that differ from their default are written, so a later change of a default reaches every
    /// folder that never changed the value. Values that were loaded for settings that are not registered in this run
    /// (<see cref="Orphans"/>, for example the settings of a plugin that is not loaded) are written back unchanged, so saving
    /// never loses them. A group without content has no file; an owned group file that no longer has content is deleted.
    /// </para>
    /// <para>
    /// <b>Last writer wins:</b> <c>Save</c> writes the whole state of this manager. For every registered setting the key is set,
    /// or removed at its default, so a value that another process stored for a registered setting since <see cref="Load"/> is
    /// replaced. Use <see cref="SaveChanged"/> to change single keys without touching the rest.
    /// </para>
    /// <para>
    /// Stale cleanup deletes only group files that are listed in the previous save manifest <i>and</i> that this manager saw at
    /// <see cref="Load"/> or wrote itself, never unrelated JSON in <see cref="StoragePath"/> and never a group file that another
    /// manager or process created after <see cref="Load"/> (a manager that never called <see cref="Load"/> has seen nothing and
    /// treats every listed file as its own). The save runs under the cross-process lock file <c>.ni-settings.lock</c> of the folder, the
    /// same lock <see cref="SaveChanged"/> uses; other processes that save at the same time wait for it (see
    /// <see cref="SaveChanged"/>). Each file is replaced atomically (temporary file, then move).
    /// </para>
    /// </summary>
    /// <exception cref="PersistenceSettingsException">Thrown when no storage path is configured, when the folder lock is not
    /// available within two seconds, or on I/O, access, or JSON errors.</exception>
    public void Save()
    {
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        try
        {
            Directory.CreateDirectory(StoragePath);
            using FolderLock folderLock = FolderLock.Acquire(StoragePath);

            // Group the content by file name: non-default values and orphans only.
            Dictionary<string, JsonObject> groups = [];

            _Lock.EnterReadLock();
            try
            {
                foreach (Setting setting in _SettingsList)
                {
                    // Sparse: a default is not stored, so a later change of the default reaches this folder.
                    if (setting.Value == setting.DefaultValue)
                    {
                        continue;
                    }

                    _GroupObject(groups, setting.GroupName)[setting.Name] = SettingValueJson.ToNode(setting.Value);
                }

                // Orphans are written back; their group files therefore stay "current" and are not deleted.
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> orphanGroup in _OrphanedValues)
                {
                    JsonObject obj = _GroupObject(groups, orphanGroup.Key);
                    foreach (KeyValuePair<string, JsonNode> orphan in orphanGroup.Value)
                    {
                        obj[orphan.Key] = orphan.Value.DeepClone();
                    }
                }
            }
            finally
            {
                _Lock.ExitReadLock();
            }

            // Write each group atomically via temp file + replace
            HashSet<string> currentGroupFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonObject> group in groups)
            {
                string fileName = $"{group.Key}.json";
                currentGroupFiles.Add(fileName);
                string filePath = Path.Combine(StoragePath, fileName);
                string tempPath = filePath + ".tmp";
                string json = group.Value.ToJsonString(_WriteOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }

            // Remove stale group files this manager knows, never unrelated JSON and never what another process created since Load.
            string manifestPath = Path.Combine(StoragePath, SettingsFileAccess.OwnedGroupManifestFileName);
            HashSet<string> previouslyOwned = _ReadOwnedGroupFiles(manifestPath);
            KnownGroupFiles known = Volatile.Read(ref _KnownGroupFiles);
            HashSet<string> deleted = new(StringComparer.OrdinalIgnoreCase);
            foreach (string existingPath in Directory.EnumerateFiles(StoragePath, "*.json"))
            {
                string existingFileName = Path.GetFileName(existingPath);
                if (previouslyOwned.Contains(existingFileName) && (!known.HasLoaded || known.Files.Contains(existingFileName))
                    && !currentGroupFiles.Contains(existingFileName))
                {
                    File.Delete(existingPath);
                    deleted.Add(existingFileName);
                }
            }

            // Keep the manifest entries of files that other managers own; add what this save wrote, drop what it deleted.
            previouslyOwned.UnionWith(currentGroupFiles);
            previouslyOwned.ExceptWith(deleted);
            _WriteOwnedGroupFiles(manifestPath, previouslyOwned);
            _RecordKnownGroupFiles(loaded: false, currentGroupFiles, deleted);

            // Clean up leftover temp files from interrupted saves
            foreach (string tempFile in Directory.EnumerateFiles(StoragePath, "*.json.tmp"))
            {
                File.Delete(tempFile);
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
        catch (UnauthorizedAccessException ex)
        {
            throw PersistenceSettingsException.ForAccessDenied(ex);
        }
    }

    /// <summary>
    /// Reads the owned-group manifest. Missing or oversized files yield an empty set
    /// so unrelated JSON is never deleted.
    /// </summary>
    private HashSet<string> _ReadOwnedGroupFiles(string manifestPath)
    {
        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
        {
            return owned;
        }

        try
        {
            using FileStream stream = SettingsFileAccess.OpenSharedRead(manifestPath);
            if (MaxConfigFileBytes > 0 && stream.Length > MaxConfigFileBytes)
            {
                return owned;
            }

            // The cap also holds for the bytes actually read; one overlong line cannot exhaust memory.
            using BoundedReadStream? bounded = MaxConfigFileBytes > 0 ? new BoundedReadStream(stream, MaxConfigFileBytes) : null;
            using StreamReader reader = new(bounded ?? (Stream)stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Manifest entries are file names only; reject anything that looks like a path.
                string fileName = Path.GetFileName(line.Trim());
                if (fileName.Length == 0
                    || !string.Equals(fileName, line.Trim(), StringComparison.Ordinal)
                    || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                owned.Add(fileName);
            }

            // A manifest that grew past the cap while it was read counts as oversized: nothing is deleted.
            if (bounded is { Exceeded: true })
            {
                owned.Clear();
            }
        }
        catch (IOException)
        {
            owned.Clear();
        }
        catch (UnauthorizedAccessException)
        {
            owned.Clear();
        }

        return owned;
    }

    /// <summary>Writes the owned-group manifest as one file name per line.</summary>
    private static void _WriteOwnedGroupFiles(string manifestPath, HashSet<string> currentGroupFiles)
    {
        using FileStream stream = new(
            manifestPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);
        using StreamWriter writer = new(stream, Encoding.UTF8);
        foreach (string fileName in currentGroupFiles)
        {
            writer.WriteLine(fileName);
        }
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
}
