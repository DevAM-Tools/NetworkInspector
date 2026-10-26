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
/// <para>
/// <b>Locking:</b> the instance lock guards the registries (settings, groups, orphans, preloads). The order is always instance
/// lock first, then the lock of a <see cref="Setting"/> or <see cref="SettingGroup"/>; those never call back into the manager
/// while holding their own lock. No file I/O happens under the instance lock: <see cref="Load"/> reads and parses a file first
/// and only then takes the write lock to apply the parsed values, and <see cref="Save"/> copies what it needs under the read
/// lock and writes afterwards. Events
/// (<see cref="SettingsApplied"/>, <see cref="PendingChanged"/>) are raised after every lock is released, so a handler may call
/// back into the manager. Cross-process safety of the storage folder comes from the lock file used by <see cref="Save"/> and
/// <see cref="SaveChanged"/>, not from the instance lock.
/// </para>
/// </summary>
public sealed partial class SettingsManager : IDisposable
{
    /// <summary>
    /// Default maximum size in bytes for persisted settings JSON and referenced protocol config files (1 GiB).
    /// A 1 GiB load fully deserializes JSON and can exhaust memory. Values <c>&lt;= 0</c> disable the size check.
    /// Limit record: value 1 GiB · threat: a hostile or runaway file exhausting memory, because each file is parsed into a
    /// complete in-memory document · configurable per manager at construction by code or by the operator (the CLI options
    /// <c>--settings-max-config-file-bytes</c>), never by end users at run time, and fixed afterwards · posture: open by
    /// default (a loose default so that large legitimate configuration files load; lowering it is opt-in). A file above the
    /// cap is skipped with a warning naming the cap, never truncated silently. Residual risk: a file just under the cap can
    /// still use a large amount of memory on a small host.
    /// </summary>
    public const long DefaultMaxConfigFileBytes = 1_073_741_824;

    /// <summary>
    /// Default maximum JSON nesting depth for persisted settings JSON and referenced protocol config files.
    /// Values <c>&lt;= 0</c> disable the depth check (System.Text.Json still has a library ceiling).
    /// Limit record: value 1024 · threat: deeply nested JSON exhausting the stack or time of the parser and of recursive walks
    /// over the parsed tree · configurable at construction (code, operator via <c>--settings-max-json-depth</c>), fixed
    /// afterwards · posture: open by default (far above any real settings file; lowering is opt-in). A file that nests deeper
    /// is skipped with a syntax warning.
    /// </summary>
    public const int DefaultMaxJsonDepth = 1024;

    /// <summary>
    /// Depth passed to System.Text.Json when the caller disables <see cref="MaxJsonDepth"/>.
    /// STJ treats <c>0</c> as "use the default of 64", so a large positive ceiling is required.
    /// </summary>
    internal const int UnboundedJsonMaxDepth = 1_000_000;

    /// <summary>
    /// Reusable JSON serializer options for saving settings: indented, because the files are meant to be read and edited by
    /// people. One shared instance that is never modified after the type initializer, so concurrent saves can use it.
    /// </summary>
    private static readonly JsonSerializerOptions _WriteOptions = new()
    {
        WriteIndented = true
    };

    // Guards _SettingsByName, _SettingsList, _GroupsByName, _GroupsList, _OrphanedValues, and _PreloadedValues. Reads (lookups,
    // snapshots, counts) are frequent and overlap; writes (registration, load, bulk apply/reset, orphan changes) are rare, which
    // is why a reader-writer lock was chosen over a plain lock. Names are ordinal (case-sensitive).
    // Not guarded by it: _KnownGroupFiles (copy on write), _Disposed and _IsLoading (atomic flags).
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
    // counterpart to _OrphanedValues and avoids round-tripping through JSON
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
    /// <exception cref="ArgumentNullException"><paramref name="storagePath"/> is <see langword="null"/>. A manager that does not
    /// persist is created with the parameterless constructor.</exception>
    public SettingsManager(string storagePath)
        : this(_RequireStoragePath(storagePath), DefaultMaxConfigFileBytes, DefaultMaxJsonDepth)
    {
    }

    /// <summary>
    /// Checks the argument of the path constructor. A null path would otherwise turn the manager into one that cannot persist
    /// without any sign, and the first <see cref="Save"/> would report "No storage path configured" far from the cause.
    /// </summary>
    private static string _RequireStoragePath(string storagePath)
    {
        ArgumentNullException.ThrowIfNull(storagePath);
        return storagePath;
    }

    /// <summary>
    /// Creates a new settings manager with a storage path, file-size cap, and JSON depth cap.
    /// Both caps are fixed for the lifetime of the instance. Values <c>&lt;= 0</c> disable that check.
    /// Omitted validation: no argument is validated here. Every <see cref="long"/> and <see cref="int"/> value is meaningful
    /// (<c>&lt;= 0</c> means "no cap"), and <paramref name="storagePath"/> is neither checked nor created; a path that cannot
    /// be used shows up when <see cref="Load"/> or <see cref="Save"/> touches the file system.
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

        // The whole registration runs under the write lock so that the duplicate check, the group creation, the application of a
        // stored value, and the insertion into both registries are one atomic step: a concurrent Load or registration sees the
        // setting either completely or not at all. The setting lock taken inside (ApplyFromPersistence) comes second, as always.
        _Lock.EnterWriteLock();
        try
        {
            // The duplicate check comes first so that a rejected registration changes nothing (no group, no preload consumed).
            if (_SettingsByName.ContainsKey(name))
            {
                throw DuplicateNameSettingsException.ForSetting(name);
            }

            // Ensure group exists
            _EnsureGroupExistsLocked(groupName);

            // Bind before applying a stored value so that the setting coordinates with Load from now on.
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
                catch (TypeMismatchSettingsException)
                {
                    // A refused preload never fails the registration: the setting keeps its default and the result says why.
                    // A value of the wrong type is a type mismatch, not a range problem; reporting OutOfRange would send the
                    // caller looking for a bound that does not exist. The preload is consumed either way.
                    loadResult = SettingLoadResult.TypeMismatch;
                }
                catch (SettingsException)
                {
                    // Out of range (or another constraint of the setting): the setting keeps its default.
                    loadResult = SettingLoadResult.OutOfRange;
                }
            }
            else
            {
                // Try to load persisted value from orphaned values
                loadResult = _TryLoadPersistedValueLocked(setting);
            }

            // Read after the stored value was applied, so the result reports what the setting holds now.
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

    /// <summary>
    /// Tries to load a persisted value for a setting from orphaned values; the caller holds the write lock. Only a value that was
    /// applied successfully is removed from the orphan table; a refused one (shape, decoding, or constraints) stays there and the
    /// setting keeps its default.
    /// </summary>
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

    /// <summary>
    /// Snapshot of all settings as read-only views. <c>Wrap</c> copies the list into a new array while the read lock is held, so
    /// the returned array is private to the caller and later registrations do not appear in it.
    /// </summary>
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

    // Each accessor looks the setting up under the manager read lock (GetSetting) and then reads the value lock-free from the
    // setting's snapshot. They return null both for an unknown name and for a setting of another type, so callers that must tell
    // the two apart use GetSetting. A caller that reads the same setting repeatedly should keep the Setting and use its own
    // members, which take no lock at all. The array accessors return defensive copies.

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
    /// and fires <see cref="SettingsApplied"/> if at least one setting changed. The returned list is the same instance that the
    /// event carries.</summary>
    /// <exception cref="InvalidOperationException">A <see cref="Load"/> is in progress (raised by the setting).</exception>
    public IReadOnlyList<string> ApplyChanges()
    {
        List<string> changed;

        // Write lock: the batch is one step with respect to registration and other bulk operations, so subscribers see a
        // consistent set of changed names. IsDirty is a lock-free pre-check; clean settings never take their own lock.
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
        // The settings are reset without events while the lock is held and the events are raised for the collected names after
        // the lock is released (see _RaisePendingChanged): a handler may call back into the manager.
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

    /// <summary>
    /// Resets all settings to their default values (current and pending) and raises <see cref="PendingChanged"/> for each
    /// setting whose pending value changed. A setting whose current value changed but whose pending value already was the
    /// default raises no event, and <see cref="SettingsApplied"/> is not raised by this member.
    /// </summary>
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

        // Claim the loading flag atomically: a second overlapping Load fails fast instead of waiting, because two loads would
        // apply files in an interleaved order. While the flag is set the settings refuse their own mutators (see Setting),
        // so a concurrent edit cannot be overwritten half way by a persisted value. Taken only after the folder check so
        // that a missing folder never leaves the flag set.
        List<SettingsLoadWarning> warnings = [];
        if (Interlocked.CompareExchange(ref _IsLoading, 1, 0) != 0)
        {
            throw new InvalidOperationException("SettingsManager.Load() is already in progress.");
        }
        List<string> seen = [];
        try
        {
            // Each group file is independent: a bad file produces a warning and is skipped, only I/O errors abort the load.
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
            // length check and the parse, and a non-regular file reports no useful length. The cheap length check avoids
            // starting a parse of an obviously oversized file. The file is parsed into a complete JsonNode tree (not streamed)
            // because unknown keys must be kept verbatim as orphans; that is why the size and depth caps matter. This
            // happens before the manager lock is taken so no file I/O runs under it.
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

            // Collect what to apply and what to orphan. Conversion (shape and decoding) runs for the whole file first; only then are
            // the values applied, so every conversion warning is known before the first setting changes. The write lock covers
            // both the collection and the application (pure memory work, no I/O): registrations and bulk operations cannot
            // interleave with it, and each setting still swaps its snapshot atomically under its own lock.
            List<(Setting Setting, SettingValue Value)> toApply = [];
            List<(string Name, JsonNode Value)> toOrphan = [];

            _Lock.EnterWriteLock();
            try
            {
                foreach (KeyValuePair<string, JsonNode?> prop in obj)
                {
                    // A JSON null means "no value stored": the setting keeps its default and nothing becomes an orphan.
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

                // Store orphaned values for later resolution when the setting is registered (for example a plugin that loads
                // later). They are also what Save writes back, so keys of settings that are not registered in this run survive.
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
            // The folder lock serializes every writer of the folder across processes (Save and SaveChanged), so the sequence
            // below (write group files, read manifest, delete stale files, rewrite manifest) cannot interleave with another
            // writer's. It is taken before the instance lock and is not held during Load, which only reads.
            _CreateStorageFolder(StoragePath);
            using FolderLock folderLock = FolderLock.Acquire(StoragePath);

            // Group the content by file name: non-default values and orphans only. The JSON is built under the read lock into
            // private objects, and all file I/O below runs after the lock is released, so slow disks never block readers of
            // the manager.
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

                // Orphans are written back; their group files therefore stay "current" and are not deleted. A stored value whose
                // setting is registered by now (the registration refused it, so it stayed in the orphan table) is not an orphan
                // of this run: the registered setting decides the key, like in SaveChanged. Without the skip the stale value
                // would be written after the setting's value and replace it, and a value the user corrected would come back.
                // The group object is created only when something is written, so a group whose orphans were all skipped gets
                // no empty file.
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> orphanGroup in _OrphanedValues)
                {
                    foreach (KeyValuePair<string, JsonNode> orphan in orphanGroup.Value)
                    {
                        if (_SettingsByName.TryGetValue(orphan.Key, out Setting? owner)
                            && string.Equals(owner.GroupName, orphanGroup.Key, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        _GroupObject(groups, orphanGroup.Key)[orphan.Key] = orphan.Value.DeepClone();
                    }
                }
            }
            finally
            {
                _Lock.ExitReadLock();
            }

            // Write each group atomically via temp file + replace: a reader sees the old file or the new one, never half of
            // one (the temp file is not flushed to disk before the move, so this protects against concurrent readers and
            // interrupted processes, not against power loss). The temp file lives in the same folder, so the move is a rename within one volume.
            HashSet<string> currentGroupFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonObject> group in groups)
            {
                string fileName = $"{group.Key}.json";
                currentGroupFiles.Add(fileName);
                string filePath = Path.Combine(StoragePath, fileName);
                string tempPath = filePath + ".tmp";
                string json = group.Value.ToJsonString(_WriteOptions);
                File.WriteAllText(tempPath, json);
                SettingsFileAccess.ReplaceFile(tempPath, filePath);
            }

            // Remove stale group files this manager knows, never unrelated JSON and never what another process created since Load.
            // A file is deleted only when all three hold: the manifest lists it (it is a group file of ours, not foreign JSON),
            // this manager saw it at Load or wrote it itself (or never loaded at all), and this save does not write it. The
            // manifest is the allow-list that keeps an unrelated "notes.json" in the folder safe from the cleanup.
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

            // Clean up leftover temp files from interrupted saves. Safe to delete every "*.json.tmp": all writers hold the folder
            // lock while they create one, so none can be in flight now.
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
    /// Security/safety decision: every failure mode (missing, over the cap, unreadable, malformed line) fails toward "own less",
    /// because an empty or smaller set can only cause a stale file to stay, whereas a wrongly large set would delete a file
    /// the user keeps in the folder. Entries must be plain <c>*.json</c> file names; anything that looks like a path is
    /// ignored, so a tampered manifest cannot make <see cref="Save"/> delete files outside the folder.
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

    /// <summary>
    /// Writes the owned-group manifest as one file name per line (UTF-8 with a byte-order mark, which the reader detects).
    /// The file is rewritten in place under the folder lock (unlike the group files, there is no temp file and move). An
    /// interrupted write can only leave a manifest that lists fewer files (see <c>_ReadOwnedGroupFiles</c>), and the worst
    /// outcome of that is a stale group file that is not cleaned up.
    /// </summary>
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
        // Exchange makes the first caller the only one that disposes, even when two threads race. The group dictionary is
        // read without the instance lock: the contract is that nobody uses the manager during or after Dispose, so no
        // registration can modify it concurrently.
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
