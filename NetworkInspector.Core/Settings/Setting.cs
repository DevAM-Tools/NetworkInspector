// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

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
    /// Owning manager, set at registration for load/mutation coordination.
    /// <c>volatile</c> so a bind from <see cref="SettingsManager.RegisterSetting"/> is visible
    /// to other threads that already hold this <see cref="Setting"/> instance.
    /// </summary>
    private volatile SettingsManager? _Owner;

    // Lock-free reads: a reader copies the volatile _Snapshot reference once and reads both values from that one object.
    // Writers hold _WriteLock, so read-modify-write sequences (compare pending, validate, store) cannot interleave, and publish
    // the new snapshot by assigning the volatile field.
    // Lock order: the manager lock is taken before this lock (the manager resets and loads settings while it holds its own
    // lock). This lock is therefore never held while calling into the manager's locks; the PendingChanged event is raised
    // after the lock is released for the same reason. _ThrowIfManagerIsLoading only reads a volatile flag.
    private readonly Lock _WriteLock = new();
    private volatile SettingSnapshot _Snapshot;

    /// <summary>Creates a setting with the specified metadata and value constraints (used by factory methods).</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> is not a valid dot-separated
    /// C-style identifier, or <paramref name="groupName"/> is not a valid lowercase dot-separated identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
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
        SettingActivation activation)
    {
        // Names are validated here, at construction, so a Setting that exists always has a valid identity (the constructor is
        // private; every factory below ends here). SettingsManager.RegisterSetting repeats the name checks.
        if (!NameValidation.IsValidName(name))
        {
            throw InvalidNameRegistrationException.For(name);
        }
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw InvalidUiNameRegistrationException.For(uiName);
        }
        if (!NameValidation.IsValidGroupName(groupName))
        {
            throw InvalidNameRegistrationException.For(groupName);
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

        // A new setting is clean: current and pending both start at the default. The factories have already checked that the
        // default satisfies the bounds, so no validation is needed here.
        _Snapshot = new SettingSnapshot(defaultValue, defaultValue);
    }

    #region Immutable Metadata

    /// <summary>Machine-readable name (e.g., "tcp.check_checksum").</summary>
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
    /// Informational: Core applies every change at once and the host or app decides about session resets and restarts.
    /// </summary>
    public SettingActivation Activation { get; }

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
    public bool TryGetAsBool(out bool value) => Value.TryGetAsBool(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.String"/>. Lock-free.</summary>
    public bool TryGetAsString(out string value) => Value.TryGetAsString(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.F64"/>. Lock-free.</summary>
    public bool TryGetAsF64(out double value) => Value.TryGetAsF64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.U64"/>. Lock-free.</summary>
    public bool TryGetAsU64(out ulong value) => Value.TryGetAsU64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.I64"/>. Lock-free.</summary>
    public bool TryGetAsI64(out long value) => Value.TryGetAsI64(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Bytes"/>. Returns a defensive copy. Lock-free.</summary>
    public bool TryGetAsBytes(out byte[] value) => Value.TryGetAsBytes(out value);

    /// <summary>Returns <see langword="true"/> if the current value is <see cref="SettingType.Enum"/>. Lock-free.</summary>
    public bool TryGetAsEnum(out (string Name, ulong Value) value) => Value.TryGetAsEnum(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.BoolArray"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    public bool TryGetAsBoolArray(out bool[] value) => Value.TryGetAsBoolArray(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.StringArray"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    public bool TryGetAsStringArray(out string[] value) => Value.TryGetAsStringArray(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.F64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    public bool TryGetAsF64Array(out double[] value) => Value.TryGetAsF64Array(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.U64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    public bool TryGetAsU64Array(out ulong[] value) => Value.TryGetAsU64Array(out value);

    /// <summary>
    /// Returns <see langword="true"/> if the current value is <see cref="SettingType.I64Array"/>.
    /// Returns a defensive copy. Lock-free.
    /// </summary>
    public bool TryGetAsI64Array(out long[] value) => Value.TryGetAsI64Array(out value);

    /// <summary>
    /// Gets a zero-allocation struct view of this setting.
    /// Do not assign the result to <see cref="IReadOnlySetting"/> — that boxes.
    /// </summary>
    public ReadOnlySettingView AsReadOnlyView() => new(this);

    #endregion

    #region Mutation

    /// <summary>Associates this setting with its owning manager. Called from <see cref="SettingsManager.RegisterSetting"/>.</summary>
    internal void BindToManager(SettingsManager owner) => _Owner = owner;

    /// <summary>
    /// Validates and atomically applies a persisted value during <see cref="SettingsManager.Load"/> (and for preloaded or
    /// orphaned values at registration). Sets current and pending to the value, so a pending edit is discarded, and does not
    /// raise <see cref="SettingsManager.PendingChanged"/>: a persisted value is loaded state, not a user edit.
    /// Bypasses the load-in-progress guard because the manager calls it itself, while it holds its write lock.
    /// </summary>
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

            _Snapshot = new SettingSnapshot(value, value);
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
        if (_Owner is not null && _Owner.IsLoading)
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
        _Owner?.OnPendingChanged(Name);
        return true;
    }

    /// <summary>
    /// Checks <paramref name="value"/> against the type, the bounds, the allowed enumeration values, and finiteness without
    /// storing it. Never throws for a bad value.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="problem">Why the value is refused; <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when the value is valid for this setting.</returns>
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
            _Owner?.OnPendingChanged(Name);
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
            _Owner?.OnPendingChanged(Name);
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
            _Owner?.OnPendingChanged(Name);
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
    private SettingsException _ToException(SettingProblem problem, SettingValue value)
    {
        if (problem.Kind == SettingProblemKind.WrongType)
        {
            return TypeMismatchSettingsException.For(Type, value.Type);
        }

        return ValidationSettingsException.For(problem.Message);
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a new boolean setting.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.F64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.F64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.F64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new u64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.U64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.U64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.U64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new i64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.I64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.I64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.I64, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new byte array setting. The default array is copied, so later changes to it do not reach the setting.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> or one of its elements is <see langword="null"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range or non-finite.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.F64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.F64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.F64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new U64 array setting with optional per-element min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.U64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.U64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.U64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new I64 array setting with optional per-element min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid or a default element is out of range.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="defaultValue"/> is <see langword="null"/>.</exception>
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
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.I64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.I64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.I64Array, def, minVal, maxVal, null, activation);
    }

    /// <summary>Creates a new enum setting from allowed value pairs.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="allowedValues"/> is <see langword="null"/>.</exception>
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
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="metadata"/> is <see langword="null"/>.</exception>
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

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type}: {Value})";

    #endregion
}
