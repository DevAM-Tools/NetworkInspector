// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// A configurable runtime setting with pending/current value model.
/// Thread-safe: reads are lock-free via an immutable snapshot reference,
/// writes use a lock for mutual exclusion and atomically swap the snapshot.
///
/// Setting is a reference type so that cloning shares the same mutable state
/// (equivalent to Rust's <c>Arc&lt;SettingState&gt;</c> pattern).
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

    private readonly string _Name;
    private readonly string _UiName;
    private readonly string? _Description;
    private readonly string _GroupName;
    private readonly SettingType _Type;
    private readonly SettingValue _DefaultValue;
    private readonly SettingValue? _MinValue;
    private readonly SettingValue? _MaxValue;
    private readonly EnumSettingMetadata? _EnumMetadata;

    // Lock-free reads: readers use Volatile.Read to get a consistent snapshot.
    // Writers hold _WriteLock and atomically swap the snapshot via Volatile.Write.
    private readonly Lock _WriteLock = new();
    private SettingSnapshot _Snapshot;

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
        _Name = name;
        _UiName = uiName;
        _Description = description;
        _GroupName = groupName;
        _Type = type;
        _DefaultValue = defaultValue;
        _MinValue = minValue;
        _MaxValue = maxValue;
        _EnumMetadata = enumMetadata;
        _Snapshot = new SettingSnapshot(defaultValue, defaultValue);
    }

    #region Immutable Metadata

    /// <summary>Machine-readable name (e.g., "tcp.check_checksum").</summary>
    public string Name => _Name;

    /// <summary>Human-readable display name.</summary>
    public string UiName => _UiName;

    /// <summary>Optional description.</summary>
    public string? Description => _Description;

    /// <summary>Group name for UI organization.</summary>
    public string GroupName => _GroupName;

    /// <summary>The setting value type.</summary>
    public SettingType Type => _Type;

    /// <summary>The default value.</summary>
    public SettingValue DefaultValue => _DefaultValue;

    /// <summary>Optional minimum value (for numeric types).</summary>
    public SettingValue? MinValue => _MinValue;

    /// <summary>Optional maximum value (for numeric types).</summary>
    public SettingValue? MaxValue => _MaxValue;

    /// <summary>Enum metadata, if this is an enum setting.</summary>
    public EnumSettingMetadata? EnumMetadata => _EnumMetadata;

    #endregion

    #region Mutable State

    /// <summary>Gets the current (applied) value. Lock-free.</summary>
    public SettingValue Value => Volatile.Read(ref Unsafe.AsRef(in _Snapshot)).CurrentValue;

    /// <summary>Gets the pending value. Lock-free.</summary>
    public SettingValue PendingValue => Volatile.Read(ref Unsafe.AsRef(in _Snapshot)).PendingValue;

    /// <summary>Returns true if the pending value differs from the current value. Lock-free.</summary>
    public bool IsDirty
    {
        get
        {
            SettingSnapshot snapshot = Volatile.Read(ref Unsafe.AsRef(in _Snapshot));
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

    #endregion

    #region Mutation

    /// <summary>
    /// Sets the pending value. Validates the value before storing.
    /// Returns <c>true</c> if changed, <c>false</c> if same.
    /// Throws <see cref="TypeMismatchSettingsException"/> or
    /// <see cref="ValidationSettingsException"/> if validation fails (value is NOT stored).
    /// </summary>
    public bool SetPendingValue(SettingValue value)
    {
        lock (_WriteLock)
        {
            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.PendingValue == value)
            {
                return false;
            }

            // Validate before storing — throws on failure
            (ValidationErrorKind kind, string? error) = _Validate(value);
            if (kind == ValidationErrorKind.TypeMismatch)
            {
                throw TypeMismatchSettingsException.For(_Type, value.Type);
            }
            if (kind == ValidationErrorKind.ValidationFailed)
            {
                throw ValidationSettingsException.For(error!);
            }

            Volatile.Write(ref _Snapshot, new SettingSnapshot(snapshot.CurrentValue, value));
            return true;
        }
    }

    /// <summary>
    /// Applies the pending value to the current value.
    /// Returns true if the value was changed.
    /// </summary>
    public bool Apply()
    {
        lock (_WriteLock)
        {
            SettingSnapshot snapshot = _Snapshot;
            if (snapshot.CurrentValue == snapshot.PendingValue)
            {
                return false;
            }
            Volatile.Write(ref _Snapshot, new SettingSnapshot(snapshot.PendingValue, snapshot.PendingValue));
            return true;
        }
    }

    /// <summary>Resets the pending value to the current value.</summary>
    public void Reset()
    {
        lock (_WriteLock)
        {
            SettingSnapshot snapshot = _Snapshot;
            Volatile.Write(ref _Snapshot, new SettingSnapshot(snapshot.CurrentValue, snapshot.CurrentValue));
        }
    }

    /// <summary>Resets both current and pending values to the default.</summary>
    public void ResetToDefault()
    {
        lock (_WriteLock)
        {
            Volatile.Write(ref _Snapshot, new SettingSnapshot(_DefaultValue, _DefaultValue));
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
        return (_Type, value.Type) switch
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
        if (_MinValue is { } min && min.TryGetAsF64(out double minF64) && v < minF64)
        {
            return $"Value {v} is below minimum {minF64}";
        }
        if (_MaxValue is { } max && max.TryGetAsF64(out double maxF64) && v > maxF64)
        {
            return $"Value {v} is above maximum {maxF64}";
        }
        return null;
    }

    private string? _ValidateU64(ulong v)
    {
        if (_MinValue is { } min && min.TryGetAsU64(out ulong minU64) && v < minU64)
        {
            return $"Value {v} is below minimum {minU64}";
        }
        if (_MaxValue is { } max && max.TryGetAsU64(out ulong maxU64) && v > maxU64)
        {
            return $"Value {v} is above maximum {maxU64}";
        }
        return null;
    }

    private string? _ValidateI64(long v)
    {
        if (_MinValue is { } min && min.TryGetAsI64(out long minI64) && v < minI64)
        {
            return $"Value {v} is below minimum {minI64}";
        }
        if (_MaxValue is { } max && max.TryGetAsI64(out long maxI64) && v > maxI64)
        {
            return $"Value {v} is above maximum {maxI64}";
        }
        return null;
    }

    private string? _ValidateEnum(SettingValue value)
    {
        if (_EnumMetadata is null)
        {
            return null;
        }
        if (!value.TryGetAsEnum(out (string _, ulong numericValue) e))
        {
            return "Invalid enum value";
        }
        if (!_EnumMetadata.IsAllowedNumeric(e.numericValue))
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
    public override string ToString() => $"{_Name} ({_Type}: {Value})";

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
