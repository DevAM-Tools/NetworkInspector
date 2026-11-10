// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// A configurable runtime setting with pending/current value model.
/// Thread-safe: reads are lock-free via an immutable snapshot reference,
/// writes use a lock for mutual exclusion and atomically swap the snapshot.
///
/// Setting is a reference type so that every copy of the reference shares the same mutable state.
/// <para>
/// <b>Load coordination:</b> After registration with a <see cref="SettingsManager"/>,
/// <see cref="SetPendingValue"/>, <see cref="Apply"/>, <see cref="Reset"/>, and
/// <see cref="ResetToDefault"/> reject concurrent calls while
/// <see cref="SettingsManager.Load"/> is in progress. <see cref="SettingsManager.Load"/>
/// requires exclusive access to apply persisted values atomically via
/// <see cref="ApplyFromPersistence"/>.
/// </para>
/// </summary>
public sealed class Setting : IReadOnlySetting
{
    /// <summary>
    /// Immutable snapshot holding both current and pending values.
    /// Replaced as a whole by assigning the volatile <c>_Snapshot</c> field (a release write). One object instead of two fields:
    /// <see cref="SettingValue"/> is larger than a machine word and cannot be written atomically, and a lock-free reader must
    /// never see the current value of one update paired with the pending value of another. Each write allocates a new snapshot;
    /// that is accepted because writes are rare (user edits, loads) while reads are frequent and take no lock.
    /// </summary>
    private sealed class SettingSnapshot(SettingValue currentValue, SettingValue pendingValue)
    {
        /// <summary>The current (applied) value.</summary>
        public readonly SettingValue CurrentValue = currentValue;

        /// <summary>The pending value (may differ from current before Apply).</summary>
        public readonly SettingValue PendingValue = pendingValue;
    }

    /// <summary>
    /// Owning manager, set once at registration for load/mutation coordination by <see cref="TryBindToManager"/>. Not
    /// <c>volatile</c>: it is written with <see cref="Interlocked"/> and read with <see cref="Volatile"/> only, so a bind is
    /// visible to other threads that already hold this <see cref="Setting"/> instance.
    /// </summary>
    private SettingsManager? _Owner;

    // Lock-free reads: a reader copies the volatile _Snapshot reference once and reads both values from that one object.
    // Writers hold _WriteLock, so read-modify-write sequences (compare pending, validate, store) cannot interleave, and publish
    // the new snapshot by assigning the volatile field.
    // Lock order: the manager lock is taken before this lock (the manager resets and loads settings while it holds its own
    // lock). This lock is therefore never held while calling into the manager's locks; the PendingChanged event is raised
    // after the lock is released for the same reason. _ThrowIfManagerIsLoading only reads a volatile flag.
    private readonly Lock _WriteLock = new();
    /// <summary>
    /// The current and pending value as one immutable object. Volatile because readers take no lock; writers hold
    /// <see cref="_WriteLock"/> and replace it as a whole.
    /// </summary>
    private volatile SettingSnapshot _Snapshot;

    /// <summary>
    /// The live holder of a limit setting, <see langword="null"/> for every other type. Owned by this setting and never by the
    /// static <see cref="LimitDefinition"/>: two managers (profiles, a per-run override) that register the same definition must
    /// not share one holder. The only caller of <see cref="LimitWriter.TrySet"/> is <see cref="_PublishLive"/>, which runs under
    /// <see cref="_WriteLock"/>; every other consumer gets only the read side, <see cref="LiveLimit"/>.
    /// </summary>
    private readonly LimitWriter? _Live;

    /// <summary>Creates a setting with the specified metadata and value constraints (used by factory methods).</summary>
    /// <param name="name">The machine-readable name.</param>
    /// <param name="uiName">The text a user interface shows.</param>
    /// <param name="description">The help text, or <see langword="null"/>.</param>
    /// <param name="groupName">The group; empty for the default group.</param>
    /// <param name="type">The value type.</param>
    /// <param name="defaultValue">The default; the factory has already checked it against the bounds.</param>
    /// <param name="minValue">The smallest accepted value, or <see langword="null"/>.</param>
    /// <param name="maxValue">The largest accepted value, or <see langword="null"/>.</param>
    /// <param name="enumMetadata">The allowed values of an enum setting, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect.</param>
    /// <param name="limit">The definition of a limit setting, or <see langword="null"/> for every other type.</param>
    /// <param name="live">
    /// The writer of the live holder to adopt, or <see langword="null"/> to create a new one at the default of
    /// <paramref name="limit"/>. The settings manager passes the writer of its own loader limits, so the setting and the manager
    /// enforce through one holder. Ignored when <paramref name="limit"/> is <see langword="null"/>.
    /// </param>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> is not a valid dot-separated
    /// C-style identifier, or <paramref name="groupName"/> is not a valid lowercase dot-separated identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    private Setting(
        string name,
        string uiName,
        string? description,
        string groupName,
        SettingType type,
        SettingValue defaultValue,
        SettingValue? minValue,
        SettingValue? maxValue,
        EnumSettingMetadata? enumMetadata,
        SettingActivation activation,
        LimitDefinition? limit = null,
        LimitWriter? live = null)
    {
        // Names are validated here, at construction, so a Setting that exists always has a valid identity (the constructor is
        // private; every factory below ends here). SettingsManager.RegisterSetting repeats the name checks.
        if (!SettingNames.IsValidName(name))
        {
            throw InvalidNameSettingsException.ForName(name);
        }
        if (!SettingNames.IsValidUiName(uiName))
        {
            throw InvalidUiNameSettingsException.For(uiName);
        }
        if (!SettingNames.IsValidGroupName(groupName))
        {
            throw InvalidNameSettingsException.ForName(groupName);
        }
        Name = name;
        UiName = uiName;
        Description = description;
        GroupName = groupName;
        Type = type;
        DefaultValue = defaultValue;
        MinValue = minValue;
        MaxValue = maxValue;
        EnumMetadata = enumMetadata;
        Activation = activation;
        LimitDefinition = limit;

        // The holder follows the default value of this setting: for an ordinary limit the default of the definition, for the loader
        // limits of the manager the value the application chose at construction (the default value of this setting then differs
        // from the default of the definition). The factories have already checked the default against the range, so the
        // publication cannot refuse it.
        _Live = limit is null
            ? null
            : live ?? new LimitWriter(limit);
        _PublishLive(defaultValue);

        // A new setting is clean: current and pending both start at the default. The factories have already checked that the
        // default satisfies the bounds, so no validation is needed here.
        _Snapshot = new SettingSnapshot(defaultValue, defaultValue);
    }

    #region Immutable Metadata

    /// <summary>Machine-readable name (e.g., "app.check_updates").</summary>
    public string Name { get; }

    /// <summary>Human-readable display name.</summary>
    public string UiName { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }

    /// <summary>Group name for UI organization.</summary>
    public string GroupName { get; }

    /// <summary>The setting value type.</summary>
    public SettingType Type { get; }

    /// <summary>The default value.</summary>
    public SettingValue DefaultValue { get; }

    /// <summary>Optional minimum value (for numeric types).</summary>
    public SettingValue? MinValue { get; }

    /// <summary>Optional maximum value (for numeric types).</summary>
    public SettingValue? MaxValue { get; }

    /// <summary>Enum metadata, if this is an enum setting.</summary>
    public EnumSettingMetadata? EnumMetadata { get; }

    /// <summary>
    /// How a change of this setting takes effect (factory parameter, default <see cref="SettingActivation.Immediate"/>).
    /// Informational: the manager applies every change at once and the application decides about session resets and restarts.
    /// </summary>
    public SettingActivation Activation { get; }

    /// <summary>
    /// The definition of the limit this setting stands for; <see langword="null"/> for every setting that is not a
    /// <see cref="SettingType.Limit"/>. Invariant: a setting of type <see cref="SettingType.Limit"/> always has a definition,
    /// because <see cref="FromLimit"/> and <see cref="FromLimitOption"/> are the only ways to create one; validation and the
    /// text and JSON codecs rely on it.
    /// </summary>
    public LimitDefinition? LimitDefinition { get; }

    /// <summary>
    /// The read side of the live holder of the limit: hot code keeps this object and reads <see cref="Limit.Ceiling"/>
    /// instead of looking the setting up. It follows the <em>current</em> value: <see cref="Apply"/>, a load, and a reset change
    /// it; a pending value does not. <see langword="null"/> for every setting that is not a <see cref="SettingType.Limit"/>.
    /// Never changes its identity: the same object for the life of the setting. Lock-free reads. The holder is published before
    /// <see cref="Value"/> changes, so a reader that reads the holder and then <see cref="Value"/> can see the new holder value
    /// first; both are valid values. The object has no way to change the value.
    /// </summary>
    /// <seealso cref="LimitDefinition"/>
    public Limit? LiveLimit => _Live?.Limit;

    #endregion

    #region Mutable State

    /// <summary>Gets the current (applied) value. Lock-free.</summary>
    public SettingValue Value => _Snapshot.CurrentValue;

    /// <summary>Gets the pending value. Lock-free.</summary>
    public SettingValue PendingValue => _Snapshot.PendingValue;

    /// <summary>Returns true if the pending value differs from the current value. Lock-free.</summary>
    public bool IsDirty
    {
        get
        {
            SettingSnapshot snapshot = _Snapshot;
            return snapshot.CurrentValue != snapshot.PendingValue;
        }
    }

    #endregion

    #region TryGetAs* — type check + value extraction in a single operation

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Bool"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBool(out bool value) => Value.TryGetAsBool(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.String"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsString(out string value) => Value.TryGetAsString(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.F64"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsF64(out double value) => Value.TryGetAsF64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.U64"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsU64(out ulong value) => Value.TryGetAsU64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.I64"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsI64(out long value) => Value.TryGetAsI64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Limit"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsLimit(out LimitValue value) => Value.TryGetAsLimit(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Bytes"/>. Returns a defensive copy. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBytes(out byte[] value) => Value.TryGetAsBytes(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Enum"/>. Lock-free.</summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsEnum(out (string Name, ulong Value) value) => Value.TryGetAsEnum(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.BoolArray"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsBoolArray(out bool[] value) => Value.TryGetAsBoolArray(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.StringArray"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsStringArray(out string[] value) => Value.TryGetAsStringArray(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.F64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsF64Array(out double[] value) => Value.TryGetAsF64Array(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.U64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsU64Array(out ulong[] value) => Value.TryGetAsU64Array(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.I64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    /// <param name="value">The value when the method returns <see langword="true"/>; otherwise the default of its type.</param>
    /// <returns><see langword="true"/> when the value has this type, so <paramref name="value"/> is set.</returns>
    public bool TryGetAsI64Array(out long[] value) => Value.TryGetAsI64Array(out value);

    /// <summary>
    /// Gets a zero-allocation struct view of this setting.
    /// Do not assign the result to <see cref="IReadOnlySetting"/> — that boxes.
    /// </summary>
    /// <returns>A view over this setting.</returns>
    public ReadOnlySettingView AsReadOnlyView() => new(this);

    #endregion

    #region Mutation

    /// <summary>
    /// Binds this setting to <paramref name="owner"/> for its whole life. Called from <see cref="SettingsManager.RegisterSetting"/>.
    /// </summary>
    /// <param name="owner">The manager that registers the setting.</param>
    /// <returns>
    /// <see langword="true"/> when the setting was free or already belongs to <paramref name="owner"/>; <see langword="false"/>
    /// when another manager owns it (the owner is never replaced).
    /// </returns>
    /// <remarks>
    /// Invariant: the load guard and the pending-change event follow the owner, so one setting has exactly one. Thread safety:
    /// one atomic compare-exchange, no lock.
    /// </remarks>
    internal bool TryBindToManager(SettingsManager owner)
    {
        SettingsManager? previous = Interlocked.CompareExchange(ref _Owner, owner, null);
        return previous is null || ReferenceEquals(previous, owner);
    }

    /// <summary>
    /// Validates and atomically applies a persisted value during <see cref="SettingsManager.Load"/> (and for preloaded or
    /// orphaned values at registration). Sets current and pending to the value, so a pending edit is discarded, and does not
    /// raise <see cref="SettingsManager.PendingChanged"/>: a persisted value is loaded state, not a user edit.
    /// Bypasses the load-in-progress guard because the manager calls it itself, while it holds its lock.
    /// </summary>
    /// <param name="value">The stored value; checked here against the type, the bounds, and the allowed values.</param>
    /// <exception cref="SettingsException">The value has the wrong type or violates the constraints (nothing is stored).</exception>
    internal void ApplyFromPersistence(SettingValue value)
    {
        lock (_WriteLock)
        {
            // Persisted files are a trust boundary: validate before the value becomes visible, never store a refused value.
            SettingProblem? problem = _Validate(value);
            if (problem is not null)
            {
                throw _ToException(problem, value);
            }

            // The holder first: if it refuses (a broken invariant), the snapshot is untouched and nothing is half applied.
            _PublishLive(value);
            _Snapshot = new SettingSnapshot(value, value);
        }
    }

    /// <summary>
    /// Publishes a new current value to the live holder of a limit. Called under <see cref="_WriteLock"/> right after the
    /// snapshot changed its current value, so the holder has a single writer per setting (and the constructor, before the setting
    /// is visible to any thread).
    /// </summary>
    /// <param name="current">The new current value; already validated.</param>
    /// <exception cref="InvalidOperationException">The holder refused the value: a broken invariant, never an expected failure.</exception>
    /// <remarks>
    /// Sync: the call sites are the constructor, <see cref="ApplyFromPersistence"/>, <see cref="Apply"/>, and
    /// <see cref="ResetToDefaultWithoutEvent"/>: the places that change the current value. A pending change, and a reset of the
    /// pending value to the current one, do not change it and do not publish.
    /// </remarks>
    private void _PublishLive(SettingValue current)
    {
        if (_Live is null)
        {
            return;
        }

        // The value was validated before it became current, so the holder accepts it; a refusal means a value reached the
        // snapshot without validation. Failing loudly beats a live holder that silently shows the old value.
        if (!current.TryGetAsLimit(out LimitValue value) || !_Live.TrySet(value, out _))
        {
            throw new InvalidOperationException(Invariant($"The live limit of '{Name}' refused a validated value."));
        }
    }

    /// <summary>
    /// Throws when the owning manager is applying persisted values via <see cref="SettingsManager.Load"/>. This is a usage-error
    /// guard, not a fence: a mutator that passed the check just before Load began still completes, serialized with Load by the
    /// setting lock, so a snapshot is never torn either way. A setting that is not registered yet has no owner and never throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">A load is in progress.</exception>
    private void _ThrowIfManagerIsLoading()
    {
        SettingsManager? owner = Volatile.Read(ref _Owner);
        if (owner is not null && owner.IsLoading)
        {
            throw new InvalidOperationException(
                "Cannot modify a setting while SettingsManager.Load() is in progress. " +
                "Load() requires exclusive access to apply persisted values.");
        }
    }

    /// <summary>
    /// Sets the pending value. Validates the value before storing.
    /// Returns <c>true</c> if changed, <c>false</c> if same (an unchanged value is not validated again and raises no event).
    /// Throws <see cref="TypeMismatchSettingsException"/> or
    /// <see cref="ValidationSettingsException"/> if validation fails (value is NOT stored).
    /// Throws <see cref="InvalidOperationException"/> when <see cref="SettingsManager.Load"/> is in progress.
    /// Raises <see cref="SettingsManager.PendingChanged"/> after the setting lock is released when the value changed.
    /// The throwing counterpart of <see cref="TrySetPendingValue"/>; thread-safe.
    /// </summary>
    /// <param name="value">The new pending value; must match the type and the constraints of the setting.</param>
    /// <returns><see langword="true"/> when the pending value changed; <see langword="false"/> when it already was this value.</returns>
    /// <exception cref="TypeMismatchSettingsException">The value has another type than the setting.</exception>
    /// <exception cref="ValidationSettingsException">
    /// The value violates a bound, a finiteness rule, the allowed enumeration values, or a read-only limit.
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="SettingsManager.Load"/> is in progress.</exception>
    public bool SetPendingValue(SettingValue value)
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            // Equal to the pending value means nothing to do. Validation can be skipped for it: the pending value was either
            // validated when it was stored or is the default, which the factories checked.
            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.PendingValue == value)
            {
                return false;
            }

            // Validate before storing — throws on failure. The exception texts are the problem messages, so the throwing
            // and the non-throwing members never disagree about a value.
            SettingProblem? problem = _Validate(value);
            if (problem is not null)
            {
                throw _ToException(problem, value);
            }

            _Snapshot = new SettingSnapshot(snapshot.CurrentValue, value);
        }

        // Outside the lock: a handler may read the manager or this setting.
        Volatile.Read(ref _Owner)?.OnPendingChanged(Name);
        return true;
    }

    /// <summary>
    /// Checks <paramref name="value"/> against the type, the bounds, the allowed enumeration values, and finiteness without
    /// storing it. Never throws for a bad value.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="problem">Why the value is refused; <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value is valid for this setting.</returns>
    /// <seealso cref="TrySetPendingValue"/>
    public bool TryValidate(SettingValue value, [NotNullWhen(false)] out SettingProblem? problem)
    {
        problem = _Validate(value);
        return problem is null;
    }

    /// <summary>
    /// Stores a valid value as the pending value; returns <see langword="false"/> with the problem otherwise (the pending
    /// value stays as it was). Raises <see cref="SettingsManager.PendingChanged"/> when the pending value changed, after the
    /// setting lock is released.
    /// </summary>
    /// <param name="value">The value to store.</param>
    /// <param name="problem">Why the value is refused; <see langword="null"/> when it was stored.</param>
    /// <returns><see langword="true"/> when the value is valid (also when it equals the pending value already).</returns>
    /// <exception cref="InvalidOperationException"><see cref="SettingsManager.Load"/> is in progress (a usage error, not an
    /// expected failure).</exception>
    public bool TrySetPendingValue(SettingValue value, [NotNullWhen(false)] out SettingProblem? problem)
    {
        bool changed;
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            // Validate first, unlike SetPendingValue: this member reports "valid" (true) even for an unchanged value, so every
            // call answers whether the value is acceptable. Expected failures return false with a problem, never an exception.
            problem = _Validate(value);
            if (problem is not null)
            {
                return false;
            }

            SettingSnapshot snapshot = _Snapshot;
            changed = snapshot.PendingValue != value;
            if (changed)
            {
                _Snapshot = new SettingSnapshot(snapshot.CurrentValue, value);
            }
        }

        // Outside the lock: a handler may read the manager or this setting.
        if (changed)
        {
            Volatile.Read(ref _Owner)?.OnPendingChanged(Name);
        }

        return true;
    }

    /// <summary>
    /// Applies the pending value to the current value.
    /// Returns true if the value was changed.
    /// Throws <see cref="InvalidOperationException"/> when <see cref="SettingsManager.Load"/> is in progress.
    /// Raises no event: <see cref="SettingsManager.SettingsApplied"/> is raised by the manager's bulk members, not by this one.
    /// Thread-safe.
    /// </summary>
    public bool Apply()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            // The pending value was validated when it was stored, so applying needs no second validation.
            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.CurrentValue == snapshot.PendingValue)
            {
                return false;
            }
            // The holder first, as in ApplyFromPersistence: a refusal leaves the snapshot untouched.
            _PublishLive(snapshot.PendingValue);
            _Snapshot = new SettingSnapshot(snapshot.PendingValue, snapshot.PendingValue);
            return true;
        }
    }

    /// <summary>
    /// Resets the pending value to the current value and raises <see cref="SettingsManager.PendingChanged"/> when the pending
    /// value changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="SettingsManager.Load"/> is in progress.</exception>
    public void Reset()
    {
        if (ResetWithoutEvent())
        {
            Volatile.Read(ref _Owner)?.OnPendingChanged(Name);
        }
    }

    /// <summary>
    /// Resets both current and pending values to the default. <see cref="SettingsManager.PendingChanged"/> is raised only when
    /// the pending value changed; a change of the current value alone raises no event (and not
    /// <see cref="SettingsManager.SettingsApplied"/> either).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="SettingsManager.Load"/> is in progress.</exception>
    public void ResetToDefault()
    {
        if (ResetToDefaultWithoutEvent())
        {
            Volatile.Read(ref _Owner)?.OnPendingChanged(Name);
        }
    }

    /// <summary>
    /// Resets the pending value to the current value without raising <see cref="SettingsManager.PendingChanged"/>, so that
    /// the manager can reset many settings under its lock and raise the events afterwards.
    /// </summary>
    /// <returns><see langword="true"/> when the pending value changed.</returns>
    internal bool ResetWithoutEvent()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            SettingSnapshot snapshot = _Snapshot;
            bool changed = snapshot.PendingValue != snapshot.CurrentValue;
            _Snapshot = new SettingSnapshot(snapshot.CurrentValue, snapshot.CurrentValue);
            return changed;
        }
    }

    /// <summary>
    /// Resets current and pending values to the default without raising <see cref="SettingsManager.PendingChanged"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the pending value changed.</returns>
    internal bool ResetToDefaultWithoutEvent()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            SettingSnapshot snapshot = _Snapshot;
            bool changed = snapshot.PendingValue != DefaultValue;
            // The holder first, as in ApplyFromPersistence: a refusal leaves the snapshot untouched.
            _PublishLive(DefaultValue);
            _Snapshot = new SettingSnapshot(DefaultValue, DefaultValue);
            return changed;
        }
    }

    #endregion

    #region Validation

    /// <summary>
    /// Validates a value against the setting's constraints and describes the first problem. One validation
    /// (<see cref="SettingValidation"/>) serves the throwing and the non-throwing members and both codecs, so every caller
    /// sees the same wording.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>The problem, or <see langword="null"/> when the value is valid.</returns>
    private SettingProblem? _Validate(SettingValue value) => SettingValidation.Validate(this, value);

    /// <summary>Maps a problem to the exception type the throwing members always used.</summary>
    /// <param name="problem">The problem that <see cref="SettingValidation"/> reported.</param>
    /// <param name="value">The refused value; its type goes into the type mismatch.</param>
    /// <returns>
    /// A <see cref="TypeMismatchSettingsException"/> for a wrong type, otherwise a <see cref="ValidationSettingsException"/> with
    /// the message of the problem.
    /// </returns>
    private SettingsException _ToException(SettingProblem problem, SettingValue value)
    {
        if (problem.Kind == SettingProblemKind.WrongType)
        {
            return TypeMismatchSettingsException.For(Type, value.Type);
        }

        return ValidationSettingsException.For(problem.Message);
    }

    #endregion

    #region Creating settings

    /// <summary>Creates a new boolean setting.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; a string or array is not <c>null</c> and is copied.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting Bool(
        string name, string uiName, string groupName,
        bool defaultValue, string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        SettingValue def = SettingValue.Bool(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Bool, def, null, null, null, activation);
    }

    /// <summary>Creates a new string setting.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; a string or array is not <c>null</c> and is copied.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting String(
        string name, string uiName, string groupName,
        string defaultValue, string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        SettingValue def = SettingValue.String(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.String, def, null, null, null, activation);
    }

    /// <summary>Creates a new f64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting F64(
        string name, string uiName, string groupName,
        double defaultValue, double? min = null, double? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        // A setting never holds NaN or infinity: JSON cannot represent them (the persistence form would be invalid) and the
        // bounds comparisons would be meaningless. The default and both bounds are therefore checked here, and the mutators
        // check every later value in the shared validation. The defaults of the integer factories need no finite check.
        if (!double.IsFinite(defaultValue))
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default f64 value must be finite, got {defaultValue}"));
        }
        if (min.HasValue && !double.IsFinite(min.Value))
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min f64 value must be finite, got {min.Value}"));
        }
        if (max.HasValue && !double.IsFinite(max.Value))
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"max f64 value must be finite, got {max.Value}"));
        }
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be >= min ({min.Value})"));
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be <= max ({max.Value})"));
        }

        SettingValue def = SettingValue.F64(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.F64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.F64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.F64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new u64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting U64(
        string name, string uiName, string groupName,
        ulong defaultValue, ulong? min = null, ulong? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be >= min ({min.Value})"));
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be <= max ({max.Value})"));
        }

        SettingValue def = SettingValue.U64(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.U64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.U64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.U64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new i64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting I64(
        string name, string uiName, string groupName,
        long defaultValue, long? min = null, long? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be >= min ({min.Value})"));
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({defaultValue}) must be <= max ({max.Value})"));
        }

        SettingValue def = SettingValue.I64(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.I64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.I64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.I64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new byte array setting. The default array is copied, so later changes to it do not reach the setting.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; a string or array is not <c>null</c> and is copied.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting Bytes(
        string name, string uiName, string groupName,
        byte[] defaultValue, string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        SettingValue def = SettingValue.Bytes(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Bytes, def, null, null, null, activation);
    }

    /// <summary>Creates a new boolean array setting. The default array is copied.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; a string or array is not <c>null</c> and is copied.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting BoolArray(
        string name, string uiName, string groupName,
        bool[] defaultValue, string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        SettingValue def = SettingValue.BoolArray(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.BoolArray, def, null, null, null, activation);
    }

    /// <summary>Creates a new string array setting. The default array is copied.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> or one of its elements is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; a string or array is not <c>null</c> and is copied.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting StringArray(
        string name, string uiName, string groupName,
        string[] defaultValue, string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        SettingValue def = SettingValue.StringArray(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.StringArray, def, null, null, null, activation);
    }

    /// <summary>Creates a new F64 array setting with optional per-element min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range or non-finite.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting F64Array(
        string name, string uiName, string groupName,
        double[] defaultValue, double? min = null, double? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        // For the array types min and max bound each element, not the length; they are stored as scalar values of the element
        // type. The default is checked element by element in the loop below, with the same finite rule as the scalar.
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (min.HasValue && !double.IsFinite(min.Value))
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min f64 value must be finite, got {min.Value}"));
        }
        if (max.HasValue && !double.IsFinite(max.Value))
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"max f64 value must be finite, got {max.Value}"));
        }
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }

        for (int i = 0; i < defaultValue.Length; i++)
        {
            double element = defaultValue[i];
            if (!double.IsFinite(element))
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default f64 value must be finite, got {element}"));
            }
            if (min.HasValue && element < min.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be >= min ({min.Value})"));
            }
            if (max.HasValue && element > max.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be <= max ({max.Value})"));
            }
        }

        SettingValue def = SettingValue.F64Array(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.F64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.F64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.F64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new U64 array setting with optional per-element min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting U64Array(
        string name, string uiName, string groupName,
        ulong[] defaultValue, ulong? min = null, ulong? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }

        for (int i = 0; i < defaultValue.Length; i++)
        {
            ulong element = defaultValue[i];
            if (min.HasValue && element < min.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be >= min ({min.Value})"));
            }
            if (max.HasValue && element > max.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be <= max ({max.Value})"));
            }
        }

        SettingValue def = SettingValue.U64Array(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.U64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.U64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.U64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new I64 array setting with optional per-element min/max constraints.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The value used until a stored value or a change replaces it; inside the bounds (every element for an array).</param>
    /// <param name="min">The smallest accepted value (element), or <see langword="null"/> for none.</param>
    /// <param name="max">The largest accepted value (element), or <see langword="null"/> for none; not below <paramref name="min"/>.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting I64Array(
        string name, string uiName, string groupName,
        long[] defaultValue, long? min = null, long? max = null,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"min ({min.Value}) must be <= max ({max.Value})"));
        }

        for (int i = 0; i < defaultValue.Length; i++)
        {
            long element = defaultValue[i];
            if (min.HasValue && element < min.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be >= min ({min.Value})"));
            }
            if (max.HasValue && element > max.Value)
            {
                throw ValidationSettingsException.For(string.Create(CultureInfo.InvariantCulture, $"default ({element}) must be <= max ({max.Value})"));
            }
        }

        SettingValue def = SettingValue.I64Array(defaultValue);
        SettingValue? minVal = min.HasValue
            ? (SettingValue?)SettingValue.I64(min.Value)
            : null;
        SettingValue? maxVal = max.HasValue
            ? (SettingValue?)SettingValue.I64(max.Value)
            : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.I64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new enum setting from allowed value pairs.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allowedValues"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The numeric value used until a stored value or a change replaces it; one of the allowed values.</param>
    /// <param name="allowedValues">The allowed names and numbers; at least one, names unique ignoring case, numbers unique.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting Enum(
        string name, string uiName, string groupName,
        ulong defaultValue, IEnumerable<(string Name, ulong Value)> allowedValues,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        EnumSettingMetadata metadata = EnumSettingMetadata.FromPairs(allowedValues);
        return EnumWithMetadata(name, uiName, groupName, defaultValue, metadata, description, activation);
    }

    /// <summary>Creates a new enum setting with pre-built metadata.</summary>
    /// <exception cref="InvalidNameSettingsException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameSettingsException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <param name="name">The setting name, a dot-separated ASCII identifier such as <c>app.flag</c>.</param>
    /// <param name="uiName">The text a user interface shows; non-empty, no control character.</param>
    /// <param name="groupName">The group; lower case, or empty for the default group.</param>
    /// <param name="defaultValue">The numeric value used until a stored value or a change replaces it; one of the allowed values.</param>
    /// <param name="metadata">The allowed names and numbers; not <see langword="null"/>, at least one entry.</param>
    /// <param name="description">Optional help text, or <see langword="null"/>.</param>
    /// <param name="activation">When a change takes effect; informational for the application.</param>
    /// <returns>A new setting at its default value; it belongs to no manager until it is registered.</returns>
    public static Setting EnumWithMetadata(
        string name, string uiName, string groupName,
        ulong defaultValue, EnumSettingMetadata metadata,
        string? description = null,
        SettingActivation activation = SettingActivation.Immediate)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.AllowedValues.Count == 0)
        {
            throw ValidationSettingsException.For("Enum setting requires at least one allowed value");
        }

        EnumSettingValue? defaultEntry = metadata.GetByNumeric(defaultValue)
            ?? throw ValidationSettingsException.For(
                string.Create(CultureInfo.InvariantCulture, $"Default value ({defaultValue}) must be one of the allowed enum values"));

        SettingValue def = SettingValue.Enum(defaultEntry.Value.Name, defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Enum, def, null, null, metadata, activation);
    }

    /// <summary>Creates a limit setting from its definition: name = key, UI name = title, group = definition group.</summary>
    /// <param name="definition">The limit; the single source of the key, the texts, the range, the default, and the flags.</param>
    /// <param name="activation">When a change takes effect, informational for the application.</param>
    /// <returns>
    /// A setting of type <see cref="SettingType.Limit"/> at the default of the definition, with a live holder of its own
    /// (<see cref="LiveLimit"/>); the range is checked by the definition, so the setting carries no minimum or maximum.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">
    /// The group of the key is not a valid group name, for example the reserved group <c>default</c> (the definition accepts it,
    /// a setting does not).
    /// </exception>
    /// <seealso cref="FromLimitOption"/>
    public static Setting FromLimit(LimitDefinition definition, SettingActivation activation = SettingActivation.Immediate)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new Setting(definition.Key, definition.Texts.Title, definition.Texts.Description, definition.Group,
            SettingType.Limit, SettingValue.Limit(definition.DefaultValue), null, null, null, activation, definition);
    }

    /// <summary>
    /// Creates the setting of one loader limit of the manager: like <see cref="FromLimit"/>, but the default value is the value the
    /// application chose when it created the manager, and the live holder is the one the manager enforces through.
    /// </summary>
    /// <param name="definition">The limit of <see cref="SettingsLimits"/>.</param>
    /// <param name="start">
    /// The value of the run before a stored value is read; the default of the setting, so a sparse save stores only a value that
    /// differs from it. Inside the range of <paramref name="definition"/>: the manager checked it at construction.
    /// </param>
    /// <param name="live">The writer the manager created for this limit; the setting adopts it.</param>
    /// <returns>A setting of type <see cref="SettingType.Limit"/> whose <see cref="LiveLimit"/> is the read side of <paramref name="live"/>.</returns>
    /// <remarks>Internal: only <see cref="SettingsManager.RegisterOwnLimits"/> creates these settings; the arguments are not checked again.</remarks>
    internal static Setting FromOwnLimit(LimitDefinition definition, LimitValue start, LimitWriter live) =>
        new(definition.Key, definition.Texts.Title, definition.Texts.Description, definition.Group,
            SettingType.Limit, SettingValue.Limit(start), null, null, null, SettingActivation.Immediate, definition, live);

    /// <summary>
    /// Creates the request option of a limit: name = local name, group <c>""</c> (the option group of a source or format
    /// request, which <see cref="SettingNames.IsValidGroupName"/> accepts as the default group).
    /// </summary>
    /// <param name="definition">The limit.</param>
    /// <returns>A setting of type <see cref="SettingType.Limit"/> at its default whose name is <see cref="LimitDefinition.LocalName"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidNameSettingsException">The local name of the key is not a valid setting name.</exception>
    /// <seealso cref="FromLimit"/>
    public static Setting FromLimitOption(LimitDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new Setting(definition.LocalName, definition.Texts.Title, definition.Texts.Description, "",
            SettingType.Limit, SettingValue.Limit(definition.DefaultValue), null, null, null,
            SettingActivation.Immediate, definition);
    }

    /// <inheritdoc/>
    public override string ToString() => Invariant($"{Name} ({Type}: {Value})");

    #endregion
}
