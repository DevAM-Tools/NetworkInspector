// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// A configurable runtime setting with pending/current value model.
/// Thread-safe: reads are lock-free via an immutable snapshot reference,
/// writes use a lock for mutual exclusion and atomically swap the snapshot.
///
/// Setting is a reference type so that cloning shares the same mutable state
/// (equivalent to Rust's <c>Arc&lt;SettingState&gt;</c> pattern).
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
    /// <summary>Immutable snapshot holding both current and pending values.
    /// Swapped atomically via <see cref="Volatile.Write{T}(ref T, T)"/>.</summary>
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

    // Lock-free reads: readers use Volatile.Read to get a consistent snapshot.
    // Writers hold _WriteLock and atomically swap the snapshot via Volatile.Write.
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
        EnumSettingMetadata? enumMetadata)
    {
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
    /// Gets a zero-allocation struct view of this setting.
    /// Do not assign the result to <see cref="IReadOnlySetting"/> — that boxes.
    /// </summary>
    public ReadOnlySettingView AsReadOnlyView() => new(this);

    #endregion

    #region Mutation

    /// <summary>Associates this setting with its owning manager. Called from <see cref="SettingsManager.RegisterSetting"/>.</summary>
    internal void BindToManager(SettingsManager owner) => _Owner = owner;

    /// <summary>
    /// Validates and atomically applies a persisted value during <see cref="SettingsManager.Load"/>.
    /// Bypasses the load-in-progress guard because the manager holds the write lock.
    /// </summary>
    internal void ApplyFromPersistence(SettingValue value)
    {
        lock (_WriteLock)
        {
            (ValidationErrorKind kind, string? error) = _Validate(value);
            if (kind == ValidationErrorKind.TypeMismatch)
            {
                throw TypeMismatchSettingsException.For(Type, value.Type);
            }
            if (kind == ValidationErrorKind.ValidationFailed)
            {
                throw ValidationSettingsException.For(error!);
            }

            _Snapshot = new SettingSnapshot(value, value);
        }
    }

    /// <summary>Throws when the owning manager is applying persisted values via <see cref="SettingsManager.Load"/>.</summary>
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
    /// Returns <c>true</c> if changed, <c>false</c> if same.
    /// Throws <see cref="TypeMismatchSettingsException"/> or
    /// <see cref="ValidationSettingsException"/> if validation fails (value is NOT stored).
    /// Throws <see cref="InvalidOperationException"/> when <see cref="SettingsManager.Load"/> is in progress.
    /// </summary>
    public bool SetPendingValue(SettingValue value)
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.PendingValue == value)
            {
                return false;
            }

            // Validate before storing — throws on failure
            (ValidationErrorKind kind, string? error) = _Validate(value);
            if (kind == ValidationErrorKind.TypeMismatch)
            {
                throw TypeMismatchSettingsException.For(Type, value.Type);
            }
            if (kind == ValidationErrorKind.ValidationFailed)
            {
                throw ValidationSettingsException.For(error!);
            }

            _Snapshot = new SettingSnapshot(snapshot.CurrentValue, value);
            return true;
        }
    }

    /// <summary>
    /// Applies the pending value to the current value.
    /// Returns true if the value was changed.
    /// Throws <see cref="InvalidOperationException"/> when <see cref="SettingsManager.Load"/> is in progress.
    /// </summary>
    public bool Apply()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.CurrentValue == snapshot.PendingValue)
            {
                return false;
            }
            _Snapshot = new SettingSnapshot(snapshot.PendingValue, snapshot.PendingValue);
            return true;
        }
    }

    /// <summary>Resets the pending value to the current value.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="SettingsManager.Load"/> is in progress.</exception>
    public void Reset()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            SettingSnapshot snapshot = _Snapshot;
            _Snapshot = new SettingSnapshot(snapshot.CurrentValue, snapshot.CurrentValue);
        }
    }

    /// <summary>Resets both current and pending values to the default.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="SettingsManager.Load"/> is in progress.</exception>
    public void ResetToDefault()
    {
        lock (_WriteLock)
        {
            _ThrowIfManagerIsLoading();

            _Snapshot = new SettingSnapshot(DefaultValue, DefaultValue);
        }
    }

    #endregion

    #region Validation

    /// <summary>
    /// Validates a value against the setting's constraints.
    /// Returns <see cref="ValidationErrorKind.None"/> if valid.
    /// </summary>
    private (ValidationErrorKind Kind, string? Message) _Validate(SettingValue value)
    {
        return (Type, value.Type) switch
        {
            (SettingType.Bool, SettingType.Bool) => (ValidationErrorKind.None, null),
            (SettingType.String, SettingType.String) => (ValidationErrorKind.None, null),
            (SettingType.Bytes, SettingType.Bytes) => (ValidationErrorKind.None, null),
            (SettingType.F64, SettingType.F64) when value.TryGetAsF64(out double f64v) => _WrapValidation(_ValidateF64(f64v)),
            (SettingType.U64, SettingType.U64) when value.TryGetAsU64(out ulong u64v) => _WrapValidation(_ValidateU64(u64v)),
            (SettingType.I64, SettingType.I64) when value.TryGetAsI64(out long i64v) => _WrapValidation(_ValidateI64(i64v)),
            (SettingType.Enum, SettingType.Enum) => _WrapValidation(_ValidateEnum(value)),
            _ => (ValidationErrorKind.TypeMismatch, null),
        };
    }

    /// <summary>Wraps a nullable string validation message into a typed result.</summary>
    private static (ValidationErrorKind Kind, string? Message) _WrapValidation(string? error)
    {
        if (error is null)
        {
            return (ValidationErrorKind.None, null);
        }
        return (ValidationErrorKind.ValidationFailed, error);
    }

    private string? _ValidateF64(double v)
    {
        if (!double.IsFinite(v))
        {
            return $"F64 setting value must be finite, got {v}";
        }
        if (MinValue is not null && MinValue.Value.TryGetAsF64(out double minF64) && v < minF64)
        {
            return $"Value {v} is below minimum {minF64}";
        }
        if (MaxValue is not null && MaxValue.Value.TryGetAsF64(out double maxF64) && v > maxF64)
        {
            return $"Value {v} is above maximum {maxF64}";
        }
        return null;
    }

    private string? _ValidateU64(ulong v)
    {
        if (MinValue is not null && MinValue.Value.TryGetAsU64(out ulong minU64) && v < minU64)
        {
            return $"Value {v} is below minimum {minU64}";
        }
        if (MaxValue is not null && MaxValue.Value.TryGetAsU64(out ulong maxU64) && v > maxU64)
        {
            return $"Value {v} is above maximum {maxU64}";
        }
        return null;
    }

    private string? _ValidateI64(long v)
    {
        if (MinValue is not null && MinValue.Value.TryGetAsI64(out long minI64) && v < minI64)
        {
            return $"Value {v} is below minimum {minI64}";
        }
        if (MaxValue is not null && MaxValue.Value.TryGetAsI64(out long maxI64) && v > maxI64)
        {
            return $"Value {v} is above maximum {maxI64}";
        }
        return null;
    }

    private string? _ValidateEnum(SettingValue value)
    {
        if (EnumMetadata is null)
        {
            return null;
        }
        if (!value.TryGetAsEnum(out (string _, ulong numericValue) e))
        {
            return "Invalid enum value";
        }
        if (!EnumMetadata.IsAllowedNumeric(e.numericValue))
        {
            return $"Value {e.numericValue} is not an allowed enum value";
        }
        return null;
    }

    #endregion

    #region Factory Methods

    /// <summary>Creates a new boolean setting.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    public static Setting Bool(
        string name, string uiName, string groupName,
        bool defaultValue, string? description = null)
    {
        SettingValue def = SettingValue.Bool(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Bool, def, null, null, null);
    }

    /// <summary>Creates a new string setting.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    public static Setting String(
        string name, string uiName, string groupName,
        string defaultValue, string? description = null)
    {
        SettingValue def = SettingValue.String(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.String, def, null, null, null);
    }

    /// <summary>Creates a new f64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    public static Setting F64(
        string name, string uiName, string groupName,
        double defaultValue, double? min = null, double? max = null,
        string? description = null)
    {
        if (!double.IsFinite(defaultValue))
        {
            throw ValidationSettingsException.For($"default f64 value must be finite, got {defaultValue}");
        }
        if (min.HasValue && !double.IsFinite(min.Value))
        {
            throw ValidationSettingsException.For($"min f64 value must be finite, got {min.Value}");
        }
        if (max.HasValue && !double.IsFinite(max.Value))
        {
            throw ValidationSettingsException.For($"max f64 value must be finite, got {max.Value}");
        }
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For($"min ({min.Value}) must be <= max ({max.Value})");
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be >= min ({min.Value})");
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be <= max ({max.Value})");
        }

        SettingValue def = SettingValue.F64(defaultValue);
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.F64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.F64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.F64, def, minVal, maxVal, null);
    }

    /// <summary>Creates a new u64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    public static Setting U64(
        string name, string uiName, string groupName,
        ulong defaultValue, ulong? min = null, ulong? max = null,
        string? description = null)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For($"min ({min.Value}) must be <= max ({max.Value})");
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be >= min ({min.Value})");
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be <= max ({max.Value})");
        }

        SettingValue def = SettingValue.U64(defaultValue);
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.U64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.U64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.U64, def, minVal, maxVal, null);
    }

    /// <summary>Creates a new i64 setting with optional min/max constraints.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    public static Setting I64(
        string name, string uiName, string groupName,
        long defaultValue, long? min = null, long? max = null,
        string? description = null)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            throw ValidationSettingsException.For($"min ({min.Value}) must be <= max ({max.Value})");
        }
        if (min.HasValue && defaultValue < min.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be >= min ({min.Value})");
        }
        if (max.HasValue && defaultValue > max.Value)
        {
            throw ValidationSettingsException.For($"default ({defaultValue}) must be <= max ({max.Value})");
        }

        SettingValue def = SettingValue.I64(defaultValue);
        SettingValue? minVal = min.HasValue ? (SettingValue?)SettingValue.I64(min.Value) : null;
        SettingValue? maxVal = max.HasValue ? (SettingValue?)SettingValue.I64(max.Value) : null;
        return new Setting(name, uiName, description, groupName,
            SettingType.I64, def, minVal, maxVal, null);
    }

    /// <summary>Creates a new byte array setting.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    public static Setting Bytes(
        string name, string uiName, string groupName,
        byte[] defaultValue, string? description = null)
    {
        SettingValue def = SettingValue.Bytes(defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Bytes, def, null, null, null);
    }

    /// <summary>Creates a new enum setting from allowed value pairs.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    public static Setting Enum(
        string name, string uiName, string groupName,
        ulong defaultValue, IEnumerable<(string Name, ulong Value)> allowedValues,
        string? description = null)
    {
        EnumSettingMetadata metadata = EnumSettingMetadata.FromPairs(allowedValues);
        return EnumWithMetadata(name, uiName, groupName, defaultValue, metadata, description);
    }

    /// <summary>Creates a new enum setting with pre-built metadata.</summary>
    /// <exception cref="InvalidNameRegistrationException">Thrown when <paramref name="name"/> or <paramref name="groupName"/>
    /// is not a valid dot-separated C-style identifier.</exception>
    /// <exception cref="InvalidUiNameRegistrationException">Thrown when <paramref name="uiName"/> is empty or contains control characters.</exception>
    /// <exception cref="ValidationSettingsException">Thrown when constraints are invalid.</exception>
    public static Setting EnumWithMetadata(
        string name, string uiName, string groupName,
        ulong defaultValue, EnumSettingMetadata metadata,
        string? description = null)
    {
        if (metadata.AllowedValues.Count == 0)
        {
            throw ValidationSettingsException.For("Enum setting requires at least one allowed value");
        }

        EnumSettingValue? defaultEntry = metadata.GetByNumeric(defaultValue)
            ?? throw ValidationSettingsException.For($"Default value ({defaultValue}) must be one of the allowed enum values");

        SettingValue def = SettingValue.Enum(defaultEntry.Value.Name, defaultValue);
        return new Setting(name, uiName, description, groupName,
            SettingType.Enum, def, null, null, metadata);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Type}: {Value})";

    /// <summary>Discriminates between validation error types.</summary>
    private enum ValidationErrorKind
    {
        /// <summary>Value is valid.</summary>
        None,
        /// <summary>Value type does not match the setting type.</summary>
        TypeMismatch,
        /// <summary>Value failed a range or constraint check.</summary>
        ValidationFailed,
    }
    #endregion
}
