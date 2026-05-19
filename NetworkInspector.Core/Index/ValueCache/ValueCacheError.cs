// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>Categorizes errors returned by ValueCache operations.</summary>
public enum ValueCacheErrorKind : byte
{
    /// <summary>The FieldType is not cacheable (None, String, Bytes).</summary>
    UnsupportedFieldType,

    /// <summary>The StorageMode is not compatible with the FieldType.</summary>
    IncompatibleStorageMode,

    /// <summary>No field with this FieldId is registered in the stack.</summary>
    UnknownField,

    /// <summary>A cache for this field already exists.</summary>
    AlreadyExists,

    /// <summary>No cache exists for this field.</summary>
    NotFound,

    /// <summary>The cache build was cancelled.</summary>
    Cancelled,

    /// <summary>The session has no PacketIndex (required for cache).</summary>
    NoPacketIndex,

    /// <summary>The request is invalid (empty, malformed, etc.).</summary>
    InvalidRequest,
}

/// <summary>Error result for ValueCache operations.</summary>
public readonly struct ValueCacheError
{
    /// <summary>The error category.</summary>
    public ValueCacheErrorKind Kind
    {
        get; init;
    }

    /// <summary>A human-readable description of the error.</summary>
    public string Message
    {
        get; init;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>
/// Result type for ValueCache operations. Carries either a value or an error.
/// Callers must inspect <see cref="IsSuccess"/> or use the Try-pattern before accessing the value.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public readonly struct ValueCacheResult<T>
{
    private readonly T? _Value;
    private readonly ValueCacheError? _Error;

    private ValueCacheResult(T? value, ValueCacheError? error)
    {
        _Value = value;
        _Error = error;
    }

    /// <summary><see langword="true"/> when the operation succeeded.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Error is null;
    }

    /// <summary><see langword="true"/> when the operation failed.</summary>
    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Error is not null;
    }

    /// <summary>Tries to extract the success value.</summary>
    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = _Value;
        return IsSuccess;
    }

    /// <summary>Tries to extract the error.</summary>
    public bool TryGetError([NotNullWhen(true)] out ValueCacheError? error)
    {
        error = _Error;
        return IsError;
    }

    /// <summary>Creates a successful result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueCacheResult<T> Success(T value) => new(value, null);

    /// <summary>Creates an error result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueCacheResult<T> Error(ValueCacheError error) => new(default, error);

    /// <summary>Creates an error result from kind and message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueCacheResult<T> Error(ValueCacheErrorKind kind, string message) =>
        new(default, new ValueCacheError { Kind = kind, Message = message });
}