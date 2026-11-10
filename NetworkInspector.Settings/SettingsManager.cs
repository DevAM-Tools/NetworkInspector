// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Central manager for application settings.
///
/// Provides thread-safe registration, access, persistence, and change notification.
/// Supports JSON-based persistence via <see cref="Load"/> and <see cref="Save"/>.
/// When no storage path is set, both <see cref="Load"/> and <see cref="Save"/>
/// throw <see cref="PersistenceSettingsException"/>; file-system failures, including access denials, throw it as well.
/// <para>
/// Uses one short <see cref="System.Threading.Lock"/> for the registries; JSON conversion and file work run outside it.
/// </para>
/// <para>Architecture: docs/architecture.md § Settings.</para>
/// <para>
/// <b>Ownership and disposal:</b> <see cref="SettingsManager"/> implements
/// <see cref="IDisposable"/>. When an application passes it to a component that takes ownership, ownership
/// transfers to that component; callers must not dispose the
/// manager themselves after that point. When used standalone (for example in tests),
/// callers are responsible for calling <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>Thread-safety:</b> All public members are thread-safe. After <see cref="Dispose"/>
/// is called the instance must not be used from any thread; a member that is called afterwards throws
/// <see cref="ObjectDisposedException"/>.
/// <see cref="Limits"/> builds a snapshot of the live values of the five limits and takes no lock; the
/// <see cref="Limit"/> holders behind it are read lock-free.
/// </para>
/// <para>
/// <b>Own limits:</b> the manager works with the limits of <see cref="SettingsLimits"/>. The values given at construction apply
/// at once. After <see cref="RegisterOwnLimits"/> the limits are also settings of the group <see cref="SettingsLimits.Group"/>:
/// <see cref="Load"/> reads that group's file first, so a stored value bounds every later file, and a value that is applied,
/// reset, or stored changes the live holder that the loader reads.
/// </para>
/// <para>
/// <b>Locking:</b> the instance lock guards the registries (settings, groups, orphans, preloads); each critical section is a few
/// dictionary operations or a copy, with no I/O, no JSON work, and no callback. The order is always instance
/// lock first, then the lock of a <see cref="Setting"/> or <see cref="SettingGroup"/>; those never call back into the manager
/// while holding their own lock. <see cref="Load"/> reads and converts a file without the lock and takes it only to apply the
/// converted values, and <see cref="Save"/> copies what it needs under the lock and builds and writes the JSON afterwards. Events
/// (<see cref="SettingsApplied"/>, <see cref="PendingChanged"/>) are raised after every lock is released, so a handler may call
/// back into the manager. Cross-process safety of the storage folder comes from the lock file used by <see cref="Save"/> and
/// <see cref="SaveChanged"/>, not from the instance lock.
/// </para>
/// </summary>
public sealed partial class SettingsManager : IDisposable
{
    #region Configuration and state

    /// <summary>
    /// Depth passed to System.Text.Json when <see cref="SettingsLimitValues.MaxJsonDepth"/> is <see cref="LimitValue.Off"/>.
    /// STJ treats <c>0</c> as "use the default of 64", so a large positive ceiling is required. Internal tuning, not a limit: it
    /// only says "no depth cap" to the parser.
    /// </summary>
    internal const int UnboundedJsonMaxDepth = 1_000_000;

    // Guards _SettingsByName, _SettingsList, _GroupsByName, _GroupsList, _OrphanedValues, and _PreloadedValues. Every critical
    // section is a handful of dictionary operations or a copy, so a plain lock costs less per lookup than a reader-writer lock
    // and no thread waits for JSON work (Section 4.3). Names are ordinal (case-sensitive).
    // Not guarded by it: _KnownGroupFiles (copy on write), _Disposed and _IsLoading (atomic flags).
    /// <summary>The instance lock; see the comment above for what it guards and why it is a plain lock.</summary>
    private readonly Lock _Lock = new();

    /// <summary>The registered settings by name; guarded by <see cref="_Lock"/>.</summary>
    private readonly Dictionary<string, Setting> _SettingsByName = new(StringComparer.Ordinal);

    /// <summary>The registered settings in registration order; guarded by <see cref="_Lock"/>.</summary>
    private readonly List<Setting> _SettingsList = [];

    /// <summary>The registered groups by name; guarded by <see cref="_Lock"/>.</summary>
    private readonly Dictionary<string, SettingGroup> _GroupsByName = new(StringComparer.Ordinal);

    /// <summary>The group names in registration order; guarded by <see cref="_Lock"/>.</summary>
    private readonly List<string> _GroupsList = [];

    /// <summary>
    /// Orphaned values: group name, then setting name, then the JSON node. Values loaded from persistence before settings are
    /// registered are stored here and automatically applied when the setting is later registered. Guarded by
    /// <see cref="_Lock"/>.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, JsonNode>> _OrphanedValues = [];

    /// <summary>
    /// Group files this manager saw at Load or wrote itself, and whether Load ran. Save deletes only known files as stale: a
    /// file that another manager or process created after Load is not this manager's to delete; a manager that never loaded
    /// has seen nothing and replaces everything it owns (all manifest files).
    /// Lock-free by design (copy on write): the snapshot is immutable and replaced with Interlocked.CompareExchange. It changes
    /// once per Load, Save, or SaveChanged and is read once per Save, so a new small set per change costs nothing that matters,
    /// and no caller ever waits for another.
    /// </summary>
    private KnownGroupFiles _KnownGroupFiles = KnownGroupFiles.Empty;

    /// <summary>
    /// Pre-loaded typed values keyed by setting name. Populated via PreloadValue
    /// before the corresponding setting is registered. Consumed in RegisterSetting and
    /// applied to the registered setting at registration time. This is the in-process
    /// counterpart to <see cref="_OrphanedValues"/> and avoids round-tripping through JSON
    /// when a caller (for example a test harness) wants to override a setting before the component
    /// that owns it has registered it. Guarded by <see cref="_Lock"/>.
    /// </summary>
    private readonly Dictionary<string, SettingValue> _PreloadedValues = new(StringComparer.Ordinal);

    /// <summary>Guards against double-dispose (0 = live, 1 = disposed). Written via <see cref="Interlocked"/>.</summary>
    private volatile int _Disposed;

    /// <summary>Non-zero while <see cref="Load"/> is applying persisted values (0 = idle, 1 = loading).</summary>
    private volatile int _IsLoading;

    /// <summary>
    /// The writers of the five limits of <see cref="SettingsLimits"/>, in the order of <see cref="SettingsLimits.All"/>: file size,
    /// nesting depth, lock wait, group-file count, text length. The manager keeps the writers and enforces through the read sides
    /// below; <see cref="RegisterOwnLimits"/> hands the same writers to the settings it registers.
    /// </summary>
    private readonly LimitWriter[] _OwnLimitWriters;

    /// <summary>Read side of <see cref="SettingsLimits.MaxFileSize"/>; read once per file.</summary>
    private readonly Limit _LiveMaxFileSize;

    /// <summary>Read side of <see cref="SettingsLimits.MaxJsonDepth"/>; read once per file.</summary>
    private readonly Limit _LiveMaxJsonDepth;

    /// <summary>Read side of <see cref="SettingsLimits.FolderLockTimeout"/>; read once per lock attempt.</summary>
    private readonly Limit _LiveFolderLockTimeout;

    /// <summary>Read side of <see cref="SettingsLimits.MaxGroupFiles"/>; read once per file of a load.</summary>
    private readonly Limit _LiveMaxGroupFiles;

    /// <summary>Read side of <see cref="SettingsLimits.MaxTextLength"/>; read by the parse of a value text.</summary>
    private readonly Limit _LiveMaxTextLength;

    /// <summary>The values the application chose at construction; the start values of the own-limit settings.</summary>
    private readonly SettingsLimitValues _StartLimits;

    /// <summary>
    /// Non-zero once <see cref="RegisterOwnLimits"/> ran (0 = not registered). Decides whether <see cref="Load"/> reads
    /// <c>settings.json</c> first.
    /// </summary>
    private volatile int _OwnLimitsRegistered;

    #endregion

    /// <summary>
    /// Creates a new settings manager without a storage path and without a layout: it keeps its values in memory and cannot
    /// <see cref="Load"/> or <see cref="Save"/> (both throw <see cref="PersistenceSettingsException"/>). Every limit has the
    /// default of <see cref="SettingsLimits"/>.
    /// </summary>
    public SettingsManager()
        : this(SettingsLimitValues.Default, null, null)
    {
    }

    /// <summary>
    /// Creates a new settings manager with a storage path for JSON persistence. Every limit has the default of
    /// <see cref="SettingsLimits"/>.
    /// </summary>
    /// <param name="storagePath">Directory for JSON persistence.</param>
    /// <param name="layout">
    /// The file names of the application. Required, because the names of the lock file and of the ownership manifest are part of
    /// the format on disk: a manager that wrote other names than an earlier run would strand the files that run owned.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="storagePath"/> or <paramref name="layout"/> is <see langword="null"/>.
    /// A manager that does not persist is created with the parameterless constructor.</exception>
    public SettingsManager(string storagePath, SettingsStorageLayout layout)
        : this(_RequireStoragePath(storagePath), layout, SettingsLimitValues.Default)
    {
    }

    /// <summary>
    /// Checks the argument of the path constructor. A null path would otherwise turn the manager into one that cannot persist
    /// without any sign, and the first <see cref="Save"/> would report "No storage path configured" far from the cause.
    /// </summary>
    /// <param name="storagePath">The path argument of the public constructor.</param>
    /// <returns><paramref name="storagePath"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storagePath"/> is <see langword="null"/>.</exception>
    private static string _RequireStoragePath(string storagePath)
    {
        ArgumentNullException.ThrowIfNull(storagePath);
        return storagePath;
    }

    /// <summary>
    /// Creates a new settings manager with a storage path, a layout, and the starting values of the limits of
    /// <see cref="SettingsLimits"/>. They apply until a stored value replaces them (see <see cref="RegisterOwnLimits"/>).
    /// Omitted validation: <paramref name="storagePath"/> is neither checked nor created; a path that cannot be used shows up
    /// when <see cref="Load"/> or <see cref="Save"/> touches the file system.
    /// </summary>
    /// <param name="storagePath">
    /// Directory for JSON persistence, or <see langword="null"/> when this manager does not persist.
    /// </param>
    /// <param name="layout">The file names of the application; required (see <see cref="Layout"/>).</param>
    /// <param name="limits">
    /// The starting values of the limits: the largest file, the deepest nesting, the wait for the folder lock, the most group
    /// files, and the longest value text. Start from <see cref="SettingsLimitValues.Default"/> and change a value with <c>with</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> or <paramref name="limits"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value of <paramref name="limits"/> is outside the range of its definition; <c>0</c> is refused, <see cref="LimitValue.Off"/>
    /// is the only way to remove a limit.
    /// </exception>
    public SettingsManager(string? storagePath, SettingsStorageLayout layout, SettingsLimitValues limits)
        : this(limits, storagePath, _RequireLayout(layout))
    {
    }

    /// <summary>
    /// The one constructor that sets the fields. A manager without a storage path may have no layout; a manager with one has
    /// both (the public constructors guarantee it).
    /// </summary>
    /// <param name="limits">The limit values; checked here.</param>
    /// <param name="storagePath">Directory for JSON persistence, or <see langword="null"/>.</param>
    /// <param name="layout">The layout, or <see langword="null"/> for the in-memory manager.</param>
    private SettingsManager(SettingsLimitValues limits, string? storagePath, SettingsStorageLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(limits);

        // A forgotten value is default(LimitValue), which is Of(0): below every range, so it fails here and never runs open.
        limits.Validate();
        StoragePath = storagePath;
        Layout = layout;
        _StartLimits = limits;

        // One live holder per limit, created here so that the loader enforces through it from the first file on. The writers start
        // at the chosen values: validated above, so TrySet cannot refuse them. A manager that never registers its own limits keeps
        // these values for its whole life; one that does registers settings that adopt the same writers.
        _OwnLimitWriters = new LimitWriter[SettingsLimits.All.Count];
        for (int index = 0; index < _OwnLimitWriters.Length; index++)
        {
            _OwnLimitWriters[index] = new LimitWriter(SettingsLimits.All[index]);
        }

        _Publish(_OwnLimitWriters[0], limits.MaxFileSize);
        _Publish(_OwnLimitWriters[1], limits.MaxJsonDepth);
        _Publish(_OwnLimitWriters[2], limits.FolderLockTimeout);
        _Publish(_OwnLimitWriters[3], limits.MaxGroupFiles);
        _Publish(_OwnLimitWriters[4], limits.MaxTextLength);
        _LiveMaxFileSize = _OwnLimitWriters[0].Limit;
        _LiveMaxJsonDepth = _OwnLimitWriters[1].Limit;
        _LiveFolderLockTimeout = _OwnLimitWriters[2].Limit;
        _LiveMaxGroupFiles = _OwnLimitWriters[3].Limit;
        _LiveMaxTextLength = _OwnLimitWriters[4].Limit;
    }

    /// <summary>Gives a writer its starting value.</summary>
    /// <param name="writer">The writer of one limit of <see cref="SettingsLimits"/>.</param>
    /// <param name="value">
    /// The value of <see cref="SettingsLimitValues"/> for that limit; inside the range (checked by
    /// <see cref="SettingsLimitValues.Validate"/>).
    /// </param>
    /// <exception cref="InvalidOperationException">The writer refused the value: a broken invariant, never an expected failure.</exception>
    private static void _Publish(LimitWriter writer, LimitValue value)
    {
        if (!writer.TrySet(value, out string? problem))
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>Checks the layout argument of the public constructor.</summary>
    /// <param name="layout">The layout.</param>
    /// <returns><paramref name="layout"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    private static SettingsStorageLayout _RequireLayout(SettingsStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout;
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
    /// The file names this manager uses in its storage folder (lock file, ownership manifest, reserved folders); <see langword="null"/>
    /// for the in-memory manager, which has no storage path and never touches a file.
    /// </summary>
    public SettingsStorageLayout? Layout { get; }

    /// <summary>
    /// The layout for a member that writes or reads the storage folder.
    /// Invariant: every constructor that sets <see cref="StoragePath"/> also sets <see cref="Layout"/>, and each persisting member
    /// returns "No storage path configured" before it reads this property, so the exception cannot be reached through a
    /// public member.
    /// </summary>
    private SettingsStorageLayout _PersistLayout => Layout
        ?? throw new InvalidOperationException("The manager has no storage layout.");

    /// <summary>
    /// The values of the limits of <see cref="SettingsLimits"/> this manager works with now: the largest file, the deepest JSON
    /// nesting, the wait for the folder lock, the most group files, and the longest value text. A new snapshot on every call, for
    /// display and for a caller that passes the values on; it takes no lock. The values change when the own limits are registered
    /// and a stored value is applied; hot code keeps the <see cref="Limit"/> from <see cref="GetLiveLimit"/> instead.
    /// </summary>
    /// <remarks>Cost: one small record per call; read it once before a loop, never per item.</remarks>
    public SettingsLimitValues Limits => new()
    {
        MaxFileSize = _LiveMaxFileSize.Value,
        MaxJsonDepth = _LiveMaxJsonDepth.Value,
        FolderLockTimeout = _LiveFolderLockTimeout.Value,
        MaxGroupFiles = _LiveMaxGroupFiles.Value,
        MaxTextLength = _LiveMaxTextLength.Value,
    };

    /// <summary>
    /// Maps the depth limit to the value System.Text.Json accepts: the number itself, or <see cref="UnboundedJsonMaxDepth"/> for
    /// <see cref="LimitValue.Off"/>, because STJ <c>MaxDepth == 0</c> means the library default of 64, not unlimited.
    /// </summary>
    /// <param name="maxJsonDepth">A value of <see cref="SettingsLimits.MaxJsonDepth"/>.</param>
    /// <returns>The depth for <see cref="JsonDocumentOptions.MaxDepth"/>.</returns>
    internal static int EffectiveJsonMaxDepth(LimitValue maxJsonDepth) =>
        maxJsonDepth.IsOff
            ? UnboundedJsonMaxDepth
            : checked((int)maxJsonDepth.Value);

    /// <summary>Returns true while <see cref="Load"/> is applying persisted values.</summary>
    internal bool IsLoading => _IsLoading != 0;

    /// <summary>Refuses a call on a manager that was disposed.</summary>
    /// <exception cref="ObjectDisposedException"><see cref="Dispose"/> was called before.</exception>
    /// <remarks>Cost: one volatile read. The plain lock cannot be disposed, so this check keeps the documented behavior after disposal.</remarks>
    private void _ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_Disposed != 0, this);

    #region Counts

    /// <summary>Returns the number of registered settings.</summary>
    public int SettingCount
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                return _SettingsList.Count;
            }
        }
    }

    /// <summary>Returns the number of registered groups.</summary>
    public int GroupCount
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                return _GroupsList.Count;
            }
        }
    }

    /// <summary>Returns true if any settings have pending changes.</summary>
    public bool HasDirtySettings
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
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
        }
    }

    #endregion

    #region Group registration

    /// <summary>
    /// Registers a group with explicit UI name and description.
    /// Groups can also be created implicitly when registering a setting with a new group name.
    /// </summary>
    /// <param name="name">The group name: lower case, dot-separated ASCII identifier; not <c>default</c>; empty for the default group.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    /// <exception cref="DuplicateNameSettingsException">Thrown when a group with the same name already exists.</exception>
    public void RegisterGroup(
        string name, string uiName, string? description = null)
    {
        if (!SettingNames.IsValidGroupName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!SettingNames.IsValidUiName(uiName))
        {
            throw InvalidUiNameSettingsException.For(uiName);
        }

        _ThrowIfDisposed();
        lock (_Lock)
        {
            if (_GroupsByName.ContainsKey(name))
            {
                throw DuplicateNameSettingsException.ForGroup(name);
            }

            SettingGroup group = new(name, uiName, description);
            _GroupsByName[name] = group;
            _GroupsList.Add(name);
        }
    }

    #endregion

    #region Setting registration

    /// <summary>
    /// Registers a setting. If the group doesn't exist, it is created implicitly.
    /// If a persisted value exists (from <see cref="Load"/>), it is applied.
    /// </summary>
    /// <param name="setting">The setting to register; it must not belong to another manager.</param>
    /// <returns>What happened to a stored value of this name, and the current value of the setting.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="setting"/> is null.</exception>
    /// <exception cref="InvalidNameSettingsException">Thrown when the name or UI name is invalid.</exception>
    /// <exception cref="DuplicateNameSettingsException">Thrown when a setting with the same name already exists.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="setting"/> belongs to another manager.</exception>
    public SettingRegistrationResult RegisterSetting(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        string name = setting.Name;
        string groupName = setting.GroupName;

        if (!SettingNames.IsValidName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!SettingNames.IsValidUiName(setting.UiName))
        {
            throw InvalidUiNameSettingsException.For(setting.UiName);
        }

        // The whole registration runs under the lock so that the duplicate check, the group creation, the application of a
        // stored value, and the insertion into both registries are one atomic step: a concurrent Load or registration sees the
        // setting either completely or not at all. The setting lock taken inside (ApplyFromPersistence) comes second, as always.
        _ThrowIfDisposed();
        lock (_Lock)
        {
            // The duplicate check comes first so that a rejected registration changes nothing (no group, no preload consumed).
            if (_SettingsByName.ContainsKey(name))
            {
                throw DuplicateNameSettingsException.ForSetting(name);
            }

            // A Setting has one owner for its whole life: the load guard and the pending-change event follow it. A second manager
            // would take them over silently, so it is refused before any group is created. Binding before the stored value is
            // applied makes the setting coordinate with Load from now on.
            if (!setting.TryBindToManager(this))
            {
                throw new InvalidOperationException(
                    Invariant($"Setting '{name}' is registered with another manager; create a new Setting for each manager."));
            }

            // Ensure group exists
            _EnsureGroupExistsLocked(groupName);

            // Apply any pre-loaded typed value (set via PreloadValue before the setting
            // existed) — takes precedence over orphaned JSON values.
            SettingLoadResult loadResult;
            SettingsLoadWarning? refusal = null;
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
                // Try to load persisted value from orphaned values; a refusal comes with the same text Load would have reported.
                (loadResult, refusal) = _TryLoadPersistedValueLocked(setting);
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

            return new SettingRegistrationResult(loadResult, currentValue) { Warning = refusal };
        }
    }

    /// <summary>
    /// Registers the limits of <see cref="SettingsLimits"/> as settings of this manager, in the group
    /// <see cref="SettingsLimits.Group"/>, so a user can read, change, and store them like any other setting. Each setting starts
    /// at the value the application chose when it created the manager, so a sparse save stores only a value that differs from it,
    /// and it adopts the live holder that the manager enforces through: a value that is applied, loaded, or reset takes effect for
    /// the next file at once. Call it once, before <see cref="Load"/>: <see cref="Load"/> then reads <c>settings.json</c> first,
    /// so a stored value bounds every other file.
    /// </summary>
    /// <param name="defaults">
    /// The default values of the settings (what a reset returns to and what a sparse save leaves out), or <see langword="null"/>
    /// for the values given at construction. A front end that enforces a different value while it loads (for example a value
    /// that the user passed for one run) gives its normal defaults here. The live value becomes the default of each setting at
    /// the registration, so call this member before <see cref="Load"/>, or set the wanted value again afterwards.
    /// </param>
    /// <returns>
    /// The warnings for stored values that the registration refused (a value outside the range or in a form that cannot be read),
    /// each with the spelling to write instead; empty when <see cref="Load"/> has not run yet, because then the load reports them.
    /// </returns>
    /// <remarks>
    /// Opt-in by design: the short-lived managers that a request builds for its options must not list the limits of the loader,
    /// and a manager that never calls this member behaves exactly as before. Security: whoever can write the settings folder can
    /// raise these limits; that person also owns the files that they bound, so no separate protection is added. Rejected:
    /// registering automatically in the constructor (it would add five settings to every manager).
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">A value of <paramref name="defaults"/> is outside the range of its definition.</exception>
    /// <exception cref="DuplicateNameSettingsException">The limits were registered before, or the application registered one of the keys.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public IReadOnlyList<SettingsLoadWarning> RegisterOwnLimits(SettingsLimitValues? defaults = null)
    {
        defaults?.Validate();
        // A key that exists already fails before anything is created: constructing a setting publishes its start value to the
        // shared live holder, and a refused registration must not move a value that is in effect.
        _ThrowIfDisposed();
        foreach (LimitDefinition definition in SettingsLimits.All)
        {
            if (GetSetting(definition.Key) is not null)
            {
                throw DuplicateNameSettingsException.ForSetting(definition.Key);
            }
        }

        SettingsLimitValues basis = defaults ?? _StartLimits;
        LimitValue[] starts =
        [
            basis.MaxFileSize,
            basis.MaxJsonDepth,
            basis.FolderLockTimeout,
            basis.MaxGroupFiles,
            basis.MaxTextLength,
        ];
        List<SettingsLoadWarning> warnings = [];
        for (int index = 0; index < _OwnLimitWriters.Length; index++)
        {
            SettingRegistrationResult result =
                RegisterSetting(Setting.FromOwnLimit(SettingsLimits.All[index], starts[index], _OwnLimitWriters[index]));
            if (result.Warning is { } refusal)
            {
                warnings.Add(refusal);
            }
        }

        _OwnLimitsRegistered = 1;
        return warnings;
    }

    /// <summary>Ensures a group exists, creating it implicitly if needed; the caller holds the lock.</summary>
    /// <param name="groupName">The group; empty for the default group.</param>
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
    /// Tries to load a persisted value for a setting from orphaned values; the caller holds the lock. Only a value that was
    /// applied successfully is removed from the orphan table; a refused one (shape, decoding, or constraints) stays there and the
    /// setting keeps its default.
    /// </summary>
    /// <param name="setting">The setting that is being registered, or that was registered while a load converted a file.</param>
    /// <returns>
    /// What happened to the stored value, and the warning that explains a refusal (a limit text with the spelling to write
    /// instead); the warning is <see langword="null"/> for no stored value and for success.
    /// </returns>
    private (SettingLoadResult Result, SettingsLoadWarning? Warning) _TryLoadPersistedValueLocked(Setting setting)
    {
        if (!_OrphanedValues.TryGetValue(setting.GroupName, out Dictionary<string, JsonNode>? groupValues))
        {
            return (SettingLoadResult.NoPersistedValue, null);
        }

        if (!groupValues.TryGetValue(setting.Name, out JsonNode? jsonNode))
        {
            return (SettingLoadResult.NoPersistedValue, null);
        }

        (SettingValue? value, SettingLoadResult parseResult, SettingsLoadWarning? parseWarning) =
            SettingValueJson.FromNode(jsonNode, setting.Type, setting.EnumMetadata, setting.GroupName, setting.Name, setting.LimitDefinition);
        if (value is null)
        {
            // Return the specific parse failure reason (TypeMismatch or DeserializationError) with the text of the converter.
            return (parseResult, parseWarning);
        }

        try
        {
            setting.ApplyFromPersistence(value.Value);
        }
        catch (SettingsException ex)
        {
            return (SettingLoadResult.OutOfRange, new SettingsLoadWarning(
                SettingsLoadWarningKind.OutOfRange,
                setting.GroupName,
                setting.Name,
                Invariant($"Persisted value for '{setting.Name}' failed validation: {ex.Message} The value is ignored.")));
        }

        // Remove from orphaned values since it's been consumed
        groupValues.Remove(setting.Name);
        if (groupValues.Count == 0)
        {
            _OrphanedValues.Remove(setting.GroupName);
        }

        return (SettingLoadResult.Success, parseWarning);
    }

    /// <summary>
    /// Pre-loads a typed value for a setting that has not been registered yet.
    /// When the setting is later registered via <see cref="RegisterSetting"/>, the
    /// pre-loaded value is automatically applied (taking precedence over any
    /// JSON-orphaned value loaded from persistence).
    /// <para>
    /// Intended for applications that need to override the settings of a component before the component
    /// registers them, for example test harnesses that build a component with non-default values
    /// for settings that the component reads while it registers (config-file
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
        if (!SettingNames.IsValidName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }

        _ThrowIfDisposed();
        lock (_Lock)
        {
            _PreloadedValues[name] = value;
        }
    }

    #endregion

    #region Querying

    /// <summary>Looks up a registered setting by its exact name; the typed accessors and <see cref="GetLiveLimit"/> build on it.</summary>
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The setting, or <see langword="null"/> when no setting has this name. Takes the manager lock for one dictionary lookup.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <seealso cref="TryGetSetting"/>
    public Setting? GetSetting(string name)
    {
        _ThrowIfDisposed();
        lock (_Lock)
        {
            return _SettingsByName.GetValueOrDefault(name);
        }
    }

    /// <summary>Gets all settings (snapshot).</summary>
    public IReadOnlyList<Setting> AllSettings
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                return [.. _SettingsList];
            }
        }
    }

    /// <summary>
    /// Snapshot of all settings as read-only views. <c>Wrap</c> copies the list into a new array while the lock is held, so
    /// the returned array is private to the caller and later registrations do not appear in it.
    /// </summary>
    private IReadOnlyList<ReadOnlySettingView> _AllSettings
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                return ReadOnlySettingView.Wrap(_SettingsList);
            }
        }
    }

    /// <summary>Snapshot of all settings as read-only struct views (used by <see cref="ReadOnlySettingsManagerView"/>).</summary>
    internal IReadOnlyList<ReadOnlySettingView> ReadOnlyAllSettings => _AllSettings;

    /// <summary>Group settings snapshot as read-only struct views (used by <see cref="ReadOnlySettingsManagerView"/>).</summary>
    /// <param name="groupName">The group name; empty for the default group.</param>
    /// <returns>The views in registration order; empty when no group has this name.</returns>
    internal IReadOnlyList<ReadOnlySettingView> GetSettingsInGroupAsReadOnly(string groupName) =>
        ReadOnlySettingView.Wrap(GetSettingsInGroup(groupName));

    /// <summary>Gets all group names (snapshot).</summary>
    public IReadOnlyList<string> AllGroups
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                return [.. _GroupsList];
            }
        }
    }

    /// <summary>Looks up a group by its exact name.</summary>
    /// <param name="name">The group name; compared ordinally; empty for the default group.</param>
    /// <returns>The group, or <see langword="null"/> when no group has this name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public SettingGroup? GetGroup(string name)
    {
        _ThrowIfDisposed();
        lock (_Lock)
        {
            return _GroupsByName.GetValueOrDefault(name);
        }
    }

    /// <summary>Gets all settings in a group, in registration order. Delegates to the group's own snapshot to avoid a linear scan.</summary>
    /// <param name="groupName">The group name; compared ordinally; empty for the default group.</param>
    /// <returns>A read-only snapshot; empty when no group has this name. Later registrations do not appear in it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groupName"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<Setting> GetSettingsInGroup(string groupName)
    {
        _ThrowIfDisposed();
        lock (_Lock)
        {
            if (_GroupsByName.TryGetValue(groupName, out SettingGroup? group))
            {
                return group.CopySettings();
            }

            return [];
        }
    }

    #endregion

    #region Typed accessors

    // Each accessor looks the setting up under the manager lock (GetSetting) and then reads the value lock-free from the
    // setting's snapshot. They return null both for an unknown name and for a setting of another type, so callers that must tell
    // the two apart use GetSetting. A caller that reads the same setting repeatedly should keep the Setting and use its own
    // members, which take no lock at all. The array accessors return defensive copies.

    /// <summary>Convenience: gets a boolean setting value by name. Returns null if not found or not a Bool setting.</summary>
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public long? GetI64Setting(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsI64(out long v))
        {
            return v;
        }
        return null;
    }

    /// <summary>Convenience: gets a limit value by key. Returns null if not found or not a limit setting.</summary>
    /// <param name="name">The key of the limit setting; compared ordinally.</param>
    /// <returns>The value, or <see langword="null"/> when the key is unregistered or the setting is not a limit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public LimitValue? GetLimit(string name)
    {
        Setting? s = GetSetting(name);
        if (s is not null && s.TryGetAsLimit(out LimitValue v))
        {
            return v;
        }
        return null;
    }

    /// <summary>
    /// Gets the live holder of a limit by key. Hot code keeps the returned object and reads
    /// <see cref="Limit.Ceiling"/> instead of calling this member per item: the lookup takes the manager
    /// lock, the read of the holder takes none. The object is the read side only; it cannot change the limit.
    /// </summary>
    /// <param name="name">The key of the limit setting.</param>
    /// <returns>
    /// The holder of this manager's setting (<see cref="Setting.LiveLimit"/>), the same object on every call; <see langword="null"/>
    /// when the key is unregistered or the setting is not a <see cref="SettingType.Limit"/>.
    /// </returns>
    public Limit? GetLiveLimit(string name) => GetSetting(name)?.LiveLimit;

    /// <summary>Convenience: gets a byte array copy by name. Returns null if not found or not a Bytes setting.</summary>
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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
    /// <param name="name">The setting name; compared ordinally, so names are case-sensitive.</param>
    /// <returns>The value, or <see langword="null"/> when the name is unregistered or the setting has another type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
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

    /// <summary>Applies all pending changes of all settings in one step, returns the names of the changed settings,
    /// and fires <see cref="SettingsApplied"/> if at least one setting changed. The returned list is the same instance that the
    /// event carries.</summary>
    /// <returns>The names of the settings whose current value changed, in registration order; empty when nothing changed.</returns>
    /// <exception cref="InvalidOperationException">A <see cref="Load"/> is in progress (raised by the setting).</exception>
    public IReadOnlyList<string> ApplyChanges()
    {
        List<string> changed;

        // The lock makes the batch one step with respect to registration and other bulk operations, so subscribers see a
        // consistent set of changed names. IsDirty is a lock-free pre-check; clean settings never take their own lock.
        _ThrowIfDisposed();
        lock (_Lock)
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
        _ThrowIfDisposed();
        lock (_Lock)
        {
            foreach (Setting setting in _SettingsList)
            {
                if (setting.ResetWithoutEvent())
                {
                    changed.Add(setting.Name);
                }
            }
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
        _ThrowIfDisposed();
        lock (_Lock)
        {
            foreach (Setting setting in _SettingsList)
            {
                if (setting.ResetToDefaultWithoutEvent())
                {
                    changed.Add(setting.Name);
                }
            }
        }

        _RaisePendingChanged(changed);
    }

    #endregion

    #region Orphaned values

    /// <summary>Returns the number of orphaned value entries.</summary>
    public int OrphanedEntryCount
    {
        get
        {
            _ThrowIfDisposed();
            lock (_Lock)
            {
                int count = 0;
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> group in _OrphanedValues)
                {
                    count += group.Value.Count;
                }

                return count;
            }
        }
    }

    /// <summary>Drops every stored value that belongs to no registered setting; the next save no longer writes them back.</summary>
    public void ClearOrphanedEntries()
    {
        _ThrowIfDisposed();
        lock (_Lock)
        {
            _OrphanedValues.Clear();
        }
    }

    #endregion

    #region Loading the folder

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

        _ThrowIfDisposed();

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
            // The file of the own limits first: a stored limit then bounds every other file of this load. Only a manager that
            // registered its own limits reads it first; for every other manager it is an ordinary group file below.
            string ownFileName = SettingsLimits.Group + ".json";
            string ownPath = Path.Combine(StoragePath, ownFileName);
            bool ownFileFirst = _OwnLimitsRegistered != 0 && File.Exists(ownPath);
            int filesRead = 0;
            if (ownFileFirst)
            {
                filesRead++;
                _LoadGroupFile(ownPath, warnings);
                seen.Add(ownFileName);
            }

            // Each group file is independent: a bad file produces a warning and is skipped, only I/O errors abort the load.
            foreach (string filePath in Directory.EnumerateFiles(StoragePath, "*.json"))
            {
                string fileName = Path.GetFileName(filePath);
                if (ownFileFirst && string.Equals(fileName, ownFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The ceiling is read once per file, so a value loaded from the own file applies at once. Security: the number of
                // files multiplies the size limit, so it is bounded too; the files after the limit stay unread and unseen, so a
                // later Save does not treat them as its own.
                if (++filesRead > _LiveMaxGroupFiles.Ceiling)
                {
                    warnings.Add(new SettingsLoadWarning(
                        SettingsLoadWarningKind.TooManyGroupFiles,
                        string.Empty,
                        string.Empty,
                        SettingsLimits.MaxGroupFiles.HitMessage(
                            "The settings folder holds more files than allowed",
                            _LiveMaxGroupFiles.Value,
                            SettingsLimits.ChangeHint(SettingsLimits.MaxGroupFiles)) + " The remaining files are not read."));
                    break;
                }

                _LoadGroupFile(filePath, warnings);
                seen.Add(fileName);
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
    /// <param name="filePath">The group file; the group is its name without extension (<c>default</c> is the default group).</param>
    /// <param name="warnings">Receives one warning per skipped file or value.</param>
    /// <exception cref="PersistenceSettingsException">The file cannot be read for a reason other than that it vanished.</exception>
    private void _LoadGroupFile(string filePath, List<SettingsLoadWarning> warnings)
    {
        string groupName = Path.GetFileNameWithoutExtension(filePath);
        try
        {
            // The stem of the default group maps to the empty-string group. Sync: SettingNames.DefaultGroupStem; the save maps the
            // group "" to the same stem (_StemOf in SettingsManager.Merge.cs).
            if (groupName == SettingNames.DefaultGroupStem)
            {
                groupName = SettingGroup.DefaultGroupName;
            }

            // Validate the group name derived from the file name.
            // Non-default group names must be lowercase dot-separated identifiers.
            string fileLabel = SettingsFileAccess.SafeFileLabel(filePath);
            if (!SettingNames.IsValidGroupName(groupName))
            {
                warnings.Add(new SettingsLoadWarning(
                    SettingsLoadWarningKind.InvalidGroupName,
                    groupName,
                    string.Empty,
                    Invariant($"Group name '{SettingValidation.Quote(groupName)}' (from file '{SettingValidation.Quote(fileLabel)}') ") +
                    "is not a valid group name. " +
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
                // The ceiling is long.MaxValue when the limit is off, so the same code runs with and without a cap. Read from the
                // live holder, once per file: a value that an earlier file (the own limits) changed applies from this file on.
                long maxBytes = _LiveMaxFileSize.Ceiling;
                if (stream.Length > maxBytes)
                {
                    warnings.Add(_OversizedGroupFileWarning(groupName, fileLabel));
                    return;
                }

                using BoundedReadStream bounded = new(stream, maxBytes);
                try
                {
                    root = JsonNode.Parse(
                        bounded,
                        nodeOptions: null,
                        documentOptions: new JsonDocumentOptions { MaxDepth = EffectiveJsonMaxDepth(_LiveMaxJsonDepth.Value) });
                }
                catch (JsonException) when (bounded.Exceeded)
                {
                    // Cut at the cap: report the size, not the syntax error the cut produced.
                    warnings.Add(_OversizedGroupFileWarning(groupName, fileLabel));
                    return;
                }

                if (bounded.Exceeded)
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
                    Invariant($"Settings file '{SettingValidation.Quote(fileLabel)}' does not contain a JSON object at the root. ") +
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
                    Invariant($"Settings file '{SettingValidation.Quote(fileLabel)}' contains invalid JSON: ") +
                    "a property name occurs twice or cannot be decoded. The file is skipped."));
                return;
            }

            // Perf and Section 4.3: the conversion of a file allocates in proportion to the file, so it runs without the lock. The
            // lock is taken twice, each time for a few dictionary operations: once to copy the registry, once to apply the converted
            // values. Rejected: converting under the lock (a file of the size limit would block every lookup of the manager).
            Dictionary<string, Setting> known;
            _ThrowIfDisposed();
            lock (_Lock)
            {
                known = new Dictionary<string, Setting>(_SettingsByName, StringComparer.Ordinal);
            }

            // Collect what to apply and what to orphan. Conversion (shape and decoding) runs for the whole file first; only then are
            // the values applied, so every conversion warning is known before the first setting changes.
            List<(Setting Setting, SettingValue Value)> toApply = [];
            List<(string Name, JsonNode Value)> toOrphan = [];
            foreach (KeyValuePair<string, JsonNode?> prop in obj)
            {
                // A JSON null means "no value stored": the setting keeps its default and nothing becomes an orphan.
                if (prop.Value is null)
                {
                    continue;
                }

                if (known.TryGetValue(prop.Key, out Setting? setting) && !string.Equals(setting.GroupName, groupName, StringComparison.Ordinal))
                {
                    // A key belongs to the file of its group. Applying it from another file would make the result depend on the
                    // undefined order of the directory listing. It stays an orphan of this file, so a save keeps it.
                    warnings.Add(new SettingsLoadWarning(
                        SettingsLoadWarningKind.WrongGroupFile,
                        groupName,
                        prop.Key,
                        Invariant($"Persisted value for '{SettingValidation.Quote(prop.Key)}' is in the file of another group and is ignored.")));
                    toOrphan.Add((prop.Key, prop.Value));
                }
                else if (setting is not null)
                {
                    (SettingValue? value, SettingLoadResult parseResult, SettingsLoadWarning? parseWarning) =
                        SettingValueJson.FromNode(prop.Value, setting.Type, setting.EnumMetadata, groupName, prop.Key, setting.LimitDefinition);
                    if (parseWarning is not null)
                    {
                        warnings.Add(parseWarning.Value);
                    }

                    if (value is not null)
                    {
                        toApply.Add((setting, value.Value));
                    }
                    else if (parseWarning is not null)
                    {
                        // The converter already described the refusal (a limit text with the spelling to write instead);
                        // a second, generic warning for the same value would only repeat it.
                    }
                    else if (parseResult == SettingLoadResult.DeserializationError)
                    {
                        warnings.Add(new SettingsLoadWarning(
                            SettingsLoadWarningKind.DeserializationError,
                            groupName,
                            prop.Key,
                            Invariant($"Persisted value for '{SettingValidation.Quote(prop.Key)}' could not be decoded for setting type '{setting.Type}' ") +
                            "(content format error, e.g. invalid base64 for a Bytes setting). The value is ignored."));
                    }
                    else
                    {
                        // The persisted JSON value's structural type does not match the registered setting type.
                        warnings.Add(new SettingsLoadWarning(
                            SettingsLoadWarningKind.TypeMismatch,
                            groupName,
                            prop.Key,
                            Invariant($"Persisted value for '{SettingValidation.Quote(prop.Key)}' has an incompatible type for setting type '{setting.Type}'.")
                            + " The value is ignored."));
                    }
                }
                else
                {
                    toOrphan.Add((prop.Key, prop.Value));
                }
            }

            // Apply the converted values. Registrations and bulk operations cannot interleave with this step, and each setting still
            // swaps its snapshot atomically under its own lock.
            _ThrowIfDisposed();
            lock (_Lock)
            {
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
                            Invariant($"Persisted value for '{setting.Name}' failed validation: {ex.Message} The value is ignored.")));
                    }
                }

                // Store orphaned values for later resolution when the setting is registered (for example a component that loads
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

                    // Invariant: a setting that was registered after the copy above still gets its stored value. Its key came
                    // in as an orphan; with the orphan stored, the registration path applies it now, so a registration during
                    // the conversion loses nothing. A key of another group stays an orphan (see WrongGroupFile).
                    foreach ((string name, JsonNode _) in toOrphan)
                    {
                        if (_SettingsByName.TryGetValue(name, out Setting? late)
                            && !known.ContainsKey(name)
                            && string.Equals(late.GroupName, groupName, StringComparison.Ordinal))
                        {
                            (_, SettingsLoadWarning? lateWarning) = _TryLoadPersistedValueLocked(late);
                            if (lateWarning is not null)
                            {
                                warnings.Add(lateWarning.Value);
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            warnings.Add(_SyntaxWarning(filePath, groupName, ex));
        }
        catch (FileNotFoundException)
        {
            // Another process deleted the file after the directory listing (a save that removes a stale group file does this at any
            // time): a file that is gone has nothing to load. Every other I/O error still aborts the load below.
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
    /// Warning for a group file above <see cref="SettingsLimits.MaxFileSize"/> (reported length or bytes read): the standard hit
    /// sentence with the limit in its unit and its key, then the fact that the file is skipped.
    /// </summary>
    /// <param name="groupName">The group of the file.</param>
    /// <param name="fileLabel">The file name without folder.</param>
    /// <returns>A warning of the kind <see cref="SettingsLoadWarningKind.FileTooLarge"/>.</returns>
    private SettingsLoadWarning _OversizedGroupFileWarning(string groupName, string fileLabel) =>
        new(SettingsLoadWarningKind.FileTooLarge, groupName, string.Empty, _OversizedText(fileLabel) + " The file is skipped.");

    /// <summary>The hit sentence of a settings file above <see cref="SettingsLimits.MaxFileSize"/>.</summary>
    /// <param name="fileLabel">The file name without folder.</param>
    /// <returns>The event, the limit in its unit, the key, and the hint of the library.</returns>
    private string _OversizedText(string fileLabel) =>
        SettingsLimits.MaxFileSize.HitMessage(
            Invariant($"Settings file '{SettingValidation.Quote(fileLabel)}' is larger than allowed"),
            _LiveMaxFileSize.Value,
            SettingsLimits.ChangeHint(SettingsLimits.MaxFileSize));

    /// <summary>The hit sentence of a settings file that nests deeper than <see cref="SettingsLimits.MaxJsonDepth"/>.</summary>
    /// <param name="fileLabel">The file name without folder.</param>
    /// <returns>The event, the limit in its unit, the key, and the hint of the library.</returns>
    private string _TooDeepText(string fileLabel) =>
        SettingsLimits.MaxJsonDepth.HitMessage(
            Invariant($"Settings file '{SettingValidation.Quote(fileLabel)}' nests deeper than allowed"),
            _LiveMaxJsonDepth.Value,
            SettingsLimits.ChangeHint(SettingsLimits.MaxJsonDepth));

    /// <summary>
    /// Whether a file that failed to parse nests deeper than the live depth limit. The parser reports a too deep file as a plain
    /// <see cref="JsonException"/> whose text is localized and names neither the cause nor the limit, so the failure path counts
    /// the nesting itself (<see cref="JsonDepthProbe"/>).
    /// </summary>
    /// <param name="filePath">The file that failed.</param>
    /// <returns>
    /// <see langword="true"/> when the nesting is deeper than <see cref="SettingsLimits.MaxJsonDepth"/> allows;
    /// <see langword="false"/> also when the file cannot be read again (it vanished or is locked), because the caller already has a
    /// syntax failure to report.
    /// </returns>
    private bool _IsTooDeep(string filePath)
    {
        try
        {
            return JsonDepthProbe.FileExceeds(filePath, _LiveMaxFileSize.Ceiling, _LiveMaxJsonDepth.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The probe is a diagnosis after a failure; a second read failure must not replace the first report.
            return false;
        }
    }

    /// <summary>The warning for a group file that failed to parse: a too deep file names its limit, any other failure is a syntax error.</summary>
    /// <param name="filePath">The file that failed.</param>
    /// <param name="groupName">The group of the file.</param>
    /// <param name="exception">The parser failure.</param>
    /// <returns>
    /// A warning of the kind <see cref="SettingsLoadWarningKind.JsonTooDeep"/> or
    /// <see cref="SettingsLoadWarningKind.InvalidGroupFileSyntax"/>.
    /// </returns>
    private SettingsLoadWarning _SyntaxWarning(string filePath, string groupName, JsonException exception)
    {
        string label = SettingsFileAccess.SafeFileLabel(filePath);
        if (_IsTooDeep(filePath))
        {
            return new SettingsLoadWarning(SettingsLoadWarningKind.JsonTooDeep, groupName, string.Empty, _TooDeepText(label) + " The file is skipped.");
        }

        return new SettingsLoadWarning(
            SettingsLoadWarningKind.InvalidGroupFileSyntax,
            groupName,
            string.Empty,
            Invariant($"Settings file '{SettingValidation.Quote(label)}' contains invalid JSON: ") +
            SettingValidation.QuoteMessage(exception.Message) + " The file is skipped.");
    }

    #endregion

    #region Saving the whole state

    /// <summary>
    /// Saves the settings to JSON files in the storage path: one pretty-printed file per group.
    /// <para>
    /// <b>Sparse:</b> only values that differ from their default are written, so a later change of a default reaches every
    /// folder that never changed the value. Values that were loaded for settings that are not registered in this run
    /// (<see cref="Orphans"/>, for example the settings of a component that is not loaded) are written back unchanged, so saving
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
    /// treats every listed file as its own). The save runs under the cross-process lock file of the folder
    /// (<see cref="SettingsStorageLayout.LockFileName"/>), the
    /// same lock <see cref="SaveChanged"/> uses; other processes that save at the same time wait for it (see
    /// <see cref="SaveChanged"/>). Each file is replaced atomically (temporary file, then move).
    /// </para>
    /// <para>
    /// <b>All or nothing for unreadable files:</b> before the first write, every file that the save would replace or delete is
    /// read. <see cref="Load"/> only warns about a file that is too large, nests too deep, or is not valid JSON, and skips it; the
    /// file may still hold values the user wants back. A save that would overwrite or delete such a file throws instead, and the
    /// folder stays as it was.
    /// </para>
    /// </summary>
    /// <exception cref="PersistenceSettingsException">Thrown when no storage path is configured, when the folder lock is not
    /// available within <see cref="SettingsLimitValues.FolderLockTimeout"/> (the message names the limit), when a file that the
    /// save would replace or delete cannot be read (nothing is written then), or on I/O, access, or JSON errors.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public void Save()
    {
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        _ThrowIfDisposed();
        try
        {
            // The folder lock serializes every writer of the folder across processes (Save and SaveChanged), so the sequence
            // below (write group files, read manifest, delete stale files, rewrite manifest) cannot interleave with another
            // writer's. It is taken before the instance lock and is not held during Load, which only reads.
            _CreateStorageFolder(StoragePath);
            using FolderLock folderLock = FolderLock.Acquire(StoragePath, _PersistLayout.LockFileName, _LiveFolderLockTimeout.Value);

            // Copy what the JSON needs under the lock, build the JSON after it: the lock covers a list copy and one clone per
            // orphan (a JsonNode has one parent, and the orphan table keeps its own node), never the conversion of the values or
            // any file I/O, so slow disks and large values never block the readers of the manager.
            Setting[] settings;
            List<(string Group, string Name, JsonNode Node)> orphanNodes = [];
            lock (_Lock)
            {
                settings = [.. _SettingsList];

                // Orphans are written back; their group files therefore stay "current" and are not deleted. A stored value whose
                // setting is registered by now (the registration refused it, so it stayed in the orphan table) is not an orphan
                // of this run: the registered setting decides the key, like in SaveChanged. Without the skip the stale value
                // would be written after the setting's value and replace it, and a value the user corrected would come back.
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> orphanGroup in _OrphanedValues)
                {
                    foreach (KeyValuePair<string, JsonNode> orphan in orphanGroup.Value)
                    {
                        if (_SettingsByName.TryGetValue(orphan.Key, out Setting? owner)
                            && string.Equals(owner.GroupName, orphanGroup.Key, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        orphanNodes.Add((orphanGroup.Key, orphan.Key, orphan.Value.DeepClone()));
                    }
                }
            }

            // Group the content by file name: non-default values and orphans only. A group object is created only when something is
            // written, so a group whose orphans were all skipped gets no empty file.
            Dictionary<string, JsonObject> groups = [];
            foreach (Setting setting in settings)
            {
                // Sparse: a default is not stored, so a later change of the default reaches this folder.
                if (setting.Value == setting.DefaultValue)
                {
                    continue;
                }

                _GroupObject(groups, setting.GroupName)[setting.Name] = SettingValueJson.ToNode(setting.Value);
            }

            foreach ((string group, string name, JsonNode node) in orphanNodes)
            {
                _GroupObject(groups, group)[name] = node;
            }

            HashSet<string> currentGroupFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (string stem in groups.Keys)
            {
                currentGroupFiles.Add(Invariant($"{stem}.json"));
            }

            // Remove stale group files this manager knows, never unrelated JSON and never what another process created since Load.
            // A file is deleted only when all three hold: the manifest lists it (it is a group file of ours, not foreign JSON),
            // this manager saw it at Load or wrote it itself (or never loaded at all), and this save does not write it. The
            // manifest is the allow-list that keeps an unrelated "notes.json" in the folder safe from the cleanup.
            string manifestPath = Path.Combine(StoragePath, _PersistLayout.OwnedManifestFileName);
            HashSet<string> previouslyOwned = _ReadOwnedGroupFiles(manifestPath);
            KnownGroupFiles known = Volatile.Read(ref _KnownGroupFiles);
            List<string> staleFiles = [];
            foreach (string existingPath in Directory.EnumerateFiles(StoragePath, "*.json"))
            {
                string existingFileName = Path.GetFileName(existingPath);
                if (previouslyOwned.Contains(existingFileName) && (!known.HasLoaded || known.Files.Contains(existingFileName))
                    && !currentGroupFiles.Contains(existingFileName))
                {
                    staleFiles.Add(existingPath);
                }
            }

            // Invariant: every file this save replaces or deletes can be read by this manager. Load only warns about a file it
            // cannot read (too large, too deep, not valid JSON) and skips it, so the file may hold values the user wants back.
            // _ReadGroupObject throws a JsonException for it, which fails the save before anything is written. Rejected: skipping
            // the file silently, which would drop the new values of that group, and overwriting it, which would destroy its content.
            // Cost: each of these files is parsed once more; a save is rare and the files are bounded by the size limit.
            foreach (string fileName in currentGroupFiles)
            {
                _ = _ReadGroupObject(Path.Combine(StoragePath, fileName));
            }

            foreach (string stalePath in staleFiles)
            {
                _ = _ReadGroupObject(stalePath);
            }

            // Write each group atomically via temp file + replace: a reader sees the old file or the new one, never half of
            // one (the temp file is not flushed to disk before the move, so this protects against concurrent readers and
            // interrupted processes, not against power loss). The temp file lives in the same folder, so the move is a rename within one volume.
            foreach (KeyValuePair<string, JsonObject> group in groups)
            {
                string filePath = Path.Combine(StoragePath, Invariant($"{group.Key}.json"));
                string tempPath = SettingsFileAccess.NewTempPath(filePath);
                _WriteGroupFile(tempPath, group.Value);
                SettingsFileAccess.ReplaceFile(tempPath, filePath);
            }

            HashSet<string> deleted = new(StringComparer.OrdinalIgnoreCase);
            foreach (string stalePath in staleFiles)
            {
                File.Delete(stalePath);
                deleted.Add(Path.GetFileName(stalePath));
            }

            // Keep the manifest entries of files that other managers own; add what this save wrote, drop what it deleted.
            previouslyOwned.UnionWith(currentGroupFiles);
            previouslyOwned.ExceptWith(deleted);
            _WriteOwnedGroupFiles(manifestPath, previouslyOwned);
            _RecordKnownGroupFiles(loaded: false, currentGroupFiles, deleted);

            // Clean up leftover temp files from interrupted saves. Security: only names of the form SettingsFileAccess.NewTempPath
            // writes are deleted (a 32-digit id), because a user's own "notes.json.tmp" in this folder is not ours. Every writer
            // of the folder holds the folder lock while it owns such a file, so none can be in flight now. Rejected: deleting every
            // "*.tmp" (it removes foreign files). Residual risk: a foreign file that carries a 32-digit id in this exact form.
            foreach (string tempFile in Directory.EnumerateFiles(StoragePath, "*.tmp"))
            {
                if (SettingsFileAccess.IsTempFileName(Path.GetFileName(tempFile)))
                {
                    File.Delete(tempFile);
                }
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
    /// <param name="manifestPath">The manifest file; it need not exist.</param>
    /// <returns>The file names the manifest lists, compared ignoring case; empty on any failure.</returns>
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
            long maxBytes = _LiveMaxFileSize.Ceiling;
            if (stream.Length > maxBytes)
            {
                return owned;
            }

            // The cap also holds for the bytes actually read; one overlong line cannot exhaust memory.
            using BoundedReadStream bounded = new(stream, maxBytes);
            using StreamReader reader = new(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
    /// <param name="manifestPath">The manifest file to create or replace.</param>
    /// <param name="currentGroupFiles">The group file names to list.</param>
    /// <exception cref="IOException">The manifest cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder refuses the write.</exception>
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

    #region Disposal

    /// <summary>
    /// Marks the manager as disposed: every member that is called afterwards throws <see cref="ObjectDisposedException"/>.
    /// When ownership has transferred to another component, that component calls this
    /// when it is disposed. In standalone usage (for example tests)
    /// callers must call this directly.
    /// Idempotent — safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        // The manager owns no unmanaged resource: a plain lock is not disposable and the groups own no lock (they publish
        // immutable snapshots). The flag is the whole state; the exchange makes the first caller the only one that sets it.
        _ = Interlocked.Exchange(ref _Disposed, 1);
    }

    #endregion
}
