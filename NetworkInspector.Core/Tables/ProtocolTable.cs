// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tables;

/// <summary>
/// Protocol dispatch table mapping typed keys to protocol IDs.
/// Uses <see cref="List{T}"/> with capacity 1 per key (most keys have 1 protocol).
/// </summary>
internal sealed class ProtocolTable
{
    #region Fields

    private readonly Dictionary<ulong, List<ProtocolId>>? _U64Map;
    private readonly Dictionary<string, List<ProtocolId>>? _StringMap;
    private readonly Dictionary<BytesKey, List<ProtocolId>>? _BytesMap;
    private List<ProtocolId>? _BoolTrue;
    private List<ProtocolId>? _BoolFalse;
    private List<ProtocolId>? _AnyList;

    #endregion

    #region Constructors

    /// <summary>Creates a protocol dispatch table from its metadata.</summary>
    internal ProtocolTable(ProtocolTableInfo info)
    {
        Info = info;
        switch (info.KeyType)
        {
            case ProtocolTableKeyType.U64:
                _U64Map = [];
                break;
            case ProtocolTableKeyType.String:
                _StringMap = new Dictionary<string, List<ProtocolId>>(StringComparer.Ordinal);
                break;
            case ProtocolTableKeyType.Bytes:
                _BytesMap = [];
                break;
            case ProtocolTableKeyType.Bool:
                break;
            case ProtocolTableKeyType.Any:
                _AnyList = new List<ProtocolId>(1);
                break;
        }
    }

    #endregion

    #region Properties

    internal ProtocolTableInfo Info { get; }
    internal ProtocolTableId Id => Info.Id;
    internal string Name => Info.Name;
    internal string UiName => Info.UiName;
    internal ProtocolTableKeyType KeyType => Info.KeyType;
    internal string? Description => Info.Description;

    #endregion

    #region Registration

    // Registration
    internal void RegisterU64(ulong key, ProtocolId protocolId)
    {
        if (_U64Map is null)
        {
            throw new InvalidOperationException(
                $"Cannot register U64 key on protocol table '{Name}' with KeyType {KeyType}.");
        }
        if (!_U64Map.TryGetValue(key, out List<ProtocolId>? list))
        {
            list = new List<ProtocolId>(1);
            _U64Map[key] = list;
        }
        if (!list.Contains(protocolId))
        {
            list.Add(protocolId);
        }
    }

    internal void RegisterString(string key, ProtocolId protocolId)
    {
        if (_StringMap is null)
        {
            throw new InvalidOperationException(
                $"Cannot register String key on protocol table '{Name}' with KeyType {KeyType}.");
        }
        if (!_StringMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            list = new List<ProtocolId>(1);
            _StringMap[key] = list;
        }
        if (!list.Contains(protocolId))
        {
            list.Add(protocolId);
        }
    }

    internal void RegisterBytes(BytesKey key, ProtocolId protocolId)
    {
        if (_BytesMap is null)
        {
            throw new InvalidOperationException(
                $"Cannot register Bytes key on protocol table '{Name}' with KeyType {KeyType}.");
        }
        if (!_BytesMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            list = new List<ProtocolId>(1);
            _BytesMap[key] = list;
        }
        if (!list.Contains(protocolId))
        {
            list.Add(protocolId);
        }
    }

    internal void RegisterBool(bool key, ProtocolId protocolId)
    {
        ref List<ProtocolId>? list = ref key ? ref _BoolTrue : ref _BoolFalse;
        list ??= new List<ProtocolId>(1);
        if (!list.Contains(protocolId))
        {
            list.Add(protocolId);
        }
    }

    internal void RegisterAny(ProtocolId protocolId)
    {
        _AnyList ??= new List<ProtocolId>(1);
        if (!_AnyList.Contains(protocolId))
        {
            _AnyList.Add(protocolId);
        }
    }

    #endregion

    #region Single-Value Lookups

    // Single-value lookups (first protocol)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetU64(ulong key)
    {
        if (_U64Map is not null && _U64Map.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetString(string key)
    {
        if (_StringMap is not null && _StringMap.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetBytes(BytesKey key)
    {
        if (_BytesMap is not null && _BytesMap.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetBool(bool key)
    {
        List<ProtocolId>? list;
        if (key)
        {
            list = _BoolTrue;
        }
        else
        {
            list = _BoolFalse;
        }
        if (list?.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetAny()
    {
        if (_AnyList?.Count > 0)
        {
            return _AnyList[0];
        }
        return null;
    }

    #endregion

    #region Multi-Value Lookups

    // Multi-value lookups (all protocols)
    internal ReadOnlySpan<ProtocolId> GetAllU64(ulong key)
    {
        if (_U64Map is not null && _U64Map.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    internal ReadOnlySpan<ProtocolId> GetAllString(string key)
    {
        if (_StringMap is not null && _StringMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    internal ReadOnlySpan<ProtocolId> GetAllBytes(BytesKey key)
    {
        if (_BytesMap is not null && _BytesMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    internal ReadOnlySpan<ProtocolId> GetAllBool(bool key)
    {
        List<ProtocolId>? list;
        if (key)
        {
            list = _BoolTrue;
        }
        else
        {
            list = _BoolFalse;
        }
        if (list is not null)
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return ReadOnlySpan<ProtocolId>.Empty;
    }

    internal ReadOnlySpan<ProtocolId> GetAllAny()
    {
        if (_AnyList is not null)
        {
            return CollectionsMarshal.AsSpan(_AnyList);
        }
        return ReadOnlySpan<ProtocolId>.Empty;
    }

    #endregion

    #region Counts and Iterators

    // Counts
    internal int Count
    {
        get
        {
            switch (Info.KeyType)
            {
                case ProtocolTableKeyType.U64:
                    if (_U64Map is not null)
                    {
                        return _U64Map.Count;
                    }
                    return 0;
                case ProtocolTableKeyType.String:
                    if (_StringMap is not null)
                    {
                        return _StringMap.Count;
                    }
                    return 0;
                case ProtocolTableKeyType.Bytes:
                    if (_BytesMap is not null)
                    {
                        return _BytesMap.Count;
                    }
                    return 0;
                case ProtocolTableKeyType.Bool:
                {
                    int count = 0;
                    if (_BoolTrue is not null)
                    {
                        count++;
                    }
                    if (_BoolFalse is not null)
                    {
                        count++;
                    }
                    return count;
                }
                case ProtocolTableKeyType.Any:
                    if (_AnyList is not null)
                    {
                        return _AnyList.Count;
                    }
                    return 0;
                default:
                    return 0;
            }
        }
    }

    internal bool IsEmpty => Count == 0;

    // Iterators
    internal IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? IterU64Entries()
    {
        if (_U64Map is null)
        {
            return null;
        }
        return _U64Map.Select(kvp => new KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>(
            kvp.Key, kvp.Value.ToArray()));
    }

    internal IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? IterStringEntries()
    {
        if (_StringMap is null)
        {
            return null;
        }
        return _StringMap.Select(kvp => new KeyValuePair<string, ReadOnlyMemory<ProtocolId>>(
            kvp.Key, kvp.Value.ToArray()));
    }

    internal IEnumerable<KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>>? IterBytesEntries()
    {
        if (_BytesMap is null)
        {
            return null;
        }
        return _BytesMap.Select(kvp => new KeyValuePair<BytesKey, ReadOnlyMemory<ProtocolId>>(
            kvp.Key, kvp.Value.ToArray()));
    }

    /// <summary>
    /// Iterates the two possible bool keys (<c>false</c>, <c>true</c>),
    /// returning an entry for each key that has at least one registered protocol.
    /// Returns <see langword="null"/> if this is not a bool-keyed table.
    /// </summary>
    internal IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>>? IterBoolEntries()
    {
        if (Info.KeyType != ProtocolTableKeyType.Bool)
        {
            return null;
        }
        return _YieldBoolEntries();
    }

    private IEnumerable<KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>> _YieldBoolEntries()
    {
        if (_BoolFalse?.Count > 0)
        {
            yield return new KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>(false, _BoolFalse.ToArray());
        }
        if (_BoolTrue?.Count > 0)
        {
            yield return new KeyValuePair<bool, ReadOnlyMemory<ProtocolId>>(true, _BoolTrue.ToArray());
        }
    }

    /// <summary>
    /// Returns all protocol IDs registered in this Any table (no key discrimination).
    /// Returns <see langword="null"/> if this is not an Any-keyed table.
    /// </summary>
    internal ReadOnlyMemory<ProtocolId>? GetAnyProtocolIds()
    {
        if (Info.KeyType != ProtocolTableKeyType.Any)
        {
            return null;
        }
        if (_AnyList is not null)
        {
            return _AnyList.ToArray();
        }
        return ReadOnlyMemory<ProtocolId>.Empty;
    }

    #endregion
}

/// <summary>
/// Byte-array key with value equality and hashing.
/// </summary>
public readonly struct BytesKey : IEquatable<BytesKey>
{


    #region Constructors

    /// <summary>Creates a bytes key by copying the given span.</summary>
    public BytesKey(ReadOnlySpan<byte> data)
    {
        Data = data.ToArray();
    }

    /// <summary>
    /// Wraps an existing array without copying. The array must not be mutated after wrap.
    /// Used by <see cref="DispatchContext"/> to store the backing array in an <see cref="object"/>
    /// slot (already a heap reference) instead of boxing this struct.
    /// </summary>
    internal BytesKey(byte[]? data)
    {
        Data = data;
    }

    #endregion

    #region Properties

    /// <summary>The raw bytes of this key.</summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            return Data ??
                [];
        }
    }

    /// <summary>Number of bytes in this key.</summary>
    public int Length
    {
        get
        {
            if (Data is not null)
            {
                return Data.Length;
            }
            return 0;
        }
    }

    /// <summary>
    /// Backing array, or <see langword="null"/> when empty.
    /// Shared with <see cref="DispatchContext"/> so the bytes key can occupy the existing
    /// reference slot without boxing this struct.
    /// </summary>
    internal byte[]? Data { get; }

    #endregion

    #region Equality

    /// <inheritdoc/>
    public bool Equals(BytesKey other) =>
        (Data ?? []).AsSpan().SequenceEqual((other.Data ?? []).AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BytesKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes((Data ?? []).AsSpan());
        return hash.ToHashCode();
    }

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> contain identical bytes.</summary>
    public static bool operator ==(BytesKey left, BytesKey right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> do not contain identical bytes.</summary>
    public static bool operator !=(BytesKey left, BytesKey right) => !left.Equals(right);

    #endregion
}
