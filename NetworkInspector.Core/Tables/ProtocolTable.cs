// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tables;

/// <summary>
/// Protocol dispatch table mapping typed keys to protocol IDs.
/// Uses <see cref="List{T}"/> with capacity 1 per key (most keys have 1 protocol).
/// <para>
/// Lifecycle and thread safety: the table is filled during stack building (<c>Register*</c>, single-threaded, through
/// <see cref="StackBuilder"/>) and only read afterwards. It has no synchronization: concurrent readers are safe because
/// nothing mutates it after building, and registering while another thread reads is not supported. The <c>GetAll*</c>
/// lookups return spans over the internal lists (no copy, no allocation); such a span is valid only as long as nobody
/// registers into the table, which the build-then-read lifecycle guarantees. The <c>Iter*</c> members return copies.
/// </para>
/// <para>
/// Several protocols can be registered under one key (in registration order, no duplicates); the dispatcher treats more
/// than one as ambiguous. A table has exactly one key type, chosen at creation from its info; only the structure matching
/// that type exists (<c>Bool</c> uses two lists, <c>Any</c> uses one list without keys).
/// </para>
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
    /// <param name="info">The table metadata; its key type decides which structure is created.</param>
    internal ProtocolTable(ProtocolTableInfo info)
    {
        Info = info;

        // Allocate only the structure for this key type; the others stay null, which is how the Register*/Get* members
        // detect a key of the wrong type. Bool tables create their two lists lazily.
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

    // Registration (build phase only, see the class remarks). Each member adds the protocol to the key's list unless it is
    // already there, so registering the same protocol twice under one key is harmless, while different protocols under one
    // key are kept in registration order. The lists start with capacity 1 because most keys map to a single protocol (the
    // default capacity would allocate four slots per key). The linear Contains is fine for such short lists.
    // Every Register* member rejects a table of another key type with InvalidOperationException.

    /// <summary>Registers <paramref name="protocolId"/> under a numeric key.</summary>
    /// <exception cref="InvalidOperationException">This is not a U64-keyed table.</exception>
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

    /// <summary>Registers <paramref name="protocolId"/> under a text key (ordinal comparison).</summary>
    /// <exception cref="InvalidOperationException">This is not a String-keyed table.</exception>
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

    /// <summary>Registers <paramref name="protocolId"/> under a byte-sequence key (value equality, see <see cref="BytesKey"/>).</summary>
    /// <exception cref="InvalidOperationException">This is not a Bytes-keyed table.</exception>
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

    /// <summary>
    /// Registers <paramref name="protocolId"/> under <see langword="true"/> or <see langword="false"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is not a Bool-keyed table.</exception>
    internal void RegisterBool(bool key, ProtocolId protocolId)
    {
        // The two lists are created lazily, so there is no null structure to test as in the other Register* members; the key type
        // is checked instead. Without the check a registration on a table of another key type would succeed silently and be
        // found by no lookup of that table.
        if (KeyType != ProtocolTableKeyType.Bool)
        {
            throw new InvalidOperationException(
                $"Cannot register Bool key on protocol table '{Name}' with KeyType {KeyType}.");
        }

        // Select the field itself by ref, so that the lazily created list is stored back into _BoolTrue or _BoolFalse.
        ref List<ProtocolId>? list = ref key ? ref _BoolTrue : ref _BoolFalse;
        list ??= new List<ProtocolId>(1);
        if (!list.Contains(protocolId))
        {
            list.Add(protocolId);
        }
    }

    /// <summary>Registers <paramref name="protocolId"/> in the key-less list of an Any table.</summary>
    /// <exception cref="InvalidOperationException">This is not an Any-keyed table.</exception>
    internal void RegisterAny(ProtocolId protocolId)
    {
        // Same reason as RegisterBool: the list may be created lazily, so the key type is the check.
        if (KeyType != ProtocolTableKeyType.Any)
        {
            throw new InvalidOperationException(
                $"Cannot register Any entry on protocol table '{Name}' with KeyType {KeyType}.");
        }

        _AnyList ??= new List<ProtocolId>(1);
        if (!_AnyList.Contains(protocolId))
        {
            _AnyList.Add(protocolId);
        }
    }

    #endregion

    #region Single-Value Lookups

    // Single-value lookups (first protocol): the first registered protocol of a key, or null when the key is unknown, has no
    // protocol, or the table has another key type. Tiny, hence AggressiveInlining. Allocation-free.

    /// <summary>First protocol registered under a numeric key, or <see langword="null"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetU64(ulong key)
    {
        if (_U64Map is not null && _U64Map.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    /// <summary>First protocol registered under a text key, or <see langword="null"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetString(string key)
    {
        if (_StringMap is not null && _StringMap.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    /// <summary>First protocol registered under a byte-sequence key, or <see langword="null"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProtocolId? GetBytes(BytesKey key)
    {
        if (_BytesMap is not null && _BytesMap.TryGetValue(key, out List<ProtocolId>? list) && list.Count > 0)
        {
            return list[0];
        }
        return null;
    }

    /// <summary>First protocol registered under <see langword="true"/> or <see langword="false"/>, or <see langword="null"/>.</summary>
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

    /// <summary>First protocol registered in the key-less list, or <see langword="null"/>.</summary>
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

    // Multi-value lookups (all protocols), used by the dispatcher on every dispatch. They return a span directly over the
    // key's list storage (CollectionsMarshal.AsSpan): no copy and no allocation, in registration order, empty for an
    // unknown key or another key type. The span is a view, valid only while the table is not modified (see the class
    // remarks); callers must not keep it beyond the dispatch.

    /// <summary>All protocols registered under a numeric key, in registration order; empty when none.</summary>
    internal ReadOnlySpan<ProtocolId> GetAllU64(ulong key)
    {
        if (_U64Map is not null && _U64Map.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    /// <summary>All protocols registered under a text key, in registration order; empty when none.</summary>
    internal ReadOnlySpan<ProtocolId> GetAllString(string key)
    {
        if (_StringMap is not null && _StringMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    /// <summary>All protocols registered under a byte-sequence key, in registration order; empty when none.</summary>
    internal ReadOnlySpan<ProtocolId> GetAllBytes(BytesKey key)
    {
        if (_BytesMap is not null && _BytesMap.TryGetValue(key, out List<ProtocolId>? list))
        {
            return CollectionsMarshal.AsSpan(list);
        }
        return [];
    }

    /// <summary>All protocols registered under <see langword="true"/> or <see langword="false"/>, in registration order; empty when none.</summary>
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

    /// <summary>All protocols of the key-less list, in registration order; empty when none.</summary>
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

    // Counts. Keys with at least one protocol for U64, String, and Bytes tables; for Bool the number of keys that were
    // registered (0 to 2); for Any the number of protocols.

    /// <summary>Number of registered keys (see the remarks above for Bool and Any tables).</summary>
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

    /// <summary>Whether <see cref="Count"/> is 0.</summary>
    internal bool IsEmpty => Count == 0;

    // Iterators. These serve introspection (listing a table in tools and tests), not dispatch, so they favor safety over
    // speed: every entry carries a copy of the protocol list (ToArray), which keeps callers from reaching the internal
    // lists, and the sequence is lazy over the live dictionary, so it must not be enumerated while the table is being
    // registered into. They return null for a table of another key type.

    /// <summary>Entries of a U64-keyed table with a copy of each protocol list; <see langword="null"/> for another key type.</summary>
    internal IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? IterU64Entries()
    {
        if (_U64Map is null)
        {
            return null;
        }
        return _U64Map.Select(kvp => new KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>(
            kvp.Key, kvp.Value.ToArray()));
    }

    /// <summary>Entries of a String-keyed table with a copy of each protocol list; <see langword="null"/> for another key type.</summary>
    internal IEnumerable<KeyValuePair<string, ReadOnlyMemory<ProtocolId>>>? IterStringEntries()
    {
        if (_StringMap is null)
        {
            return null;
        }
        return _StringMap.Select(kvp => new KeyValuePair<string, ReadOnlyMemory<ProtocolId>>(
            kvp.Key, kvp.Value.ToArray()));
    }

    /// <summary>Entries of a Bytes-keyed table with a copy of each protocol list; <see langword="null"/> for another key type.</summary>
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

    /// <summary>Lazily yields the false entry (when it has protocols), then the true entry, each with a copy of its list.</summary>
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
/// <para>
/// Immutable after construction (thread-safe) provided nobody mutates the backing array: the public constructor copies its
/// input, and the internal one wraps an array the caller promises not to change. The default value and an empty key are
/// equal. The hash code uses <see cref="HashCode"/>, which is seeded randomly per process, so hash codes differ between
/// runs and must never be persisted or compared across processes.
/// </para>
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
        // A default key has null Data; treating null as empty makes default and empty keys equal. SequenceEqual on spans
        // compares the bytes without allocating.
        (Data ?? []).AsSpan().SequenceEqual((other.Data ?? []).AsSpan());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BytesKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Must agree with Equals: a null Data hashes like an empty array, and the whole content is hashed.
        HashCode hash = new();
        hash.AddBytes((Data ?? []).AsSpan());
        return hash.ToHashCode();
    }

    #endregion

    #region Formatting

    /// <summary>
    /// Upper-case hexadecimal text of the key bytes without separators (<c>AB01</c> for <c>[0xAB, 0x01]</c>); empty for
    /// an empty or default key. Used as the label of the <c>packet.choice</c> container of an ambiguous bytes dispatch.
    /// </summary>
    public override string ToString() => Convert.ToHexString(Span);

    #endregion

    #region Operators

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> contain identical bytes.</summary>
    public static bool operator ==(BytesKey left, BytesKey right) => left.Equals(right);

    /// <summary>Returns <see langword="true"/> if <paramref name="left"/> and <paramref name="right"/> do not contain identical bytes.</summary>
    public static bool operator !=(BytesKey left, BytesKey right) => !left.Equals(right);

    #endregion
}
