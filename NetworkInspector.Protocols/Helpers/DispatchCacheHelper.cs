// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Extension methods on <see cref="Stack"/> for building pre-computed protocol dispatch
/// caches.
/// <para>
/// Call these methods once from <see cref="IProtocol.OnStart"/>; never call them per packet.
/// The cache eliminates per-packet protocol table lookups in the normal dispatch path.
/// </para>
/// <para>
/// <b>Delegate caches</b> (<see cref="BuildU64DelegateCache"/>,
/// <see cref="BuildU64SparseDelegateCache"/>) resolve <see cref="ProtocolId"/> to
/// pre-bound <see cref="ParseDelegate"/> at build time, enabling direct method invocation
/// without interface vtable dispatch.
/// </para>
/// <para>
/// <b>ID caches</b> (<see cref="BuildU64IdCache"/>, <see cref="BuildU64SparseIdCache"/>)
/// store <see cref="ProtocolId"/> values for contexts that need IDs rather than delegates.
/// </para>
/// </summary>
internal static class DispatchCacheHelper
{
    #region Delegate Caches

    /// <summary>
    /// Builds a dense <see cref="ParseDelegate"/>[] cache for a u64 dispatch table over the
    /// key domain <c>[0, <paramref name="domainSize"/>)</c>.
    /// For each key with exactly one registered protocol, the resolved <see cref="ParseDelegate"/>
    /// is stored at <c>cache[key]</c>; keys with zero or multiple protocols get
    /// <see langword="null"/> so the caller falls back to full table dispatch.
    /// <para>
    /// Intended for 8-bit key domains (e.g., IP protocol byte, IPv6 next-header byte)
    /// where the full 256-entry array costs only ~2 kB.
    /// At dispatch time: <c>cache[protocolByte]?.Invoke()</c> — one array load + direct call.
    /// </para>
    /// </summary>
    internal static ParseDelegate?[] BuildU64DelegateCache(
        this Stack stack, ProtocolTableId tableId, int domainSize)
    {
        ParseDelegate?[] cache = new ParseDelegate?[domainSize];

        IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? entries =
            stack.GetU64TableEntries(tableId);
        if (entries is null)
        {
            return cache;
        }

        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> kvp in entries)
        {
            ulong key = kvp.Key;
            // Only cache single-protocol keys within the allocated domain; multi-protocol → fallback.
            if (key < (ulong)domainSize && kvp.Value.Length == 1)
            {
                cache[key] = stack.ResolveParseDelegate(kvp.Value.Span[0]);
            }
        }

        return cache;
    }

    /// <summary>
    /// Builds a sparse <c>(<see cref="ulong"/>, <see cref="ParseDelegate"/>)[]</c> cache for
    /// a u64 dispatch table, containing one entry per key where exactly one protocol is registered.
    /// <para>
    /// Suitable for tables with few registered protocols (e.g., EtherType → 4–6 entries,
    /// link type → 1–3 entries). A linear scan over the returned array is faster than
    /// dictionary hashing for small entry counts — all entries fit in one or two L1 cache lines.
    /// At dispatch time: direct delegate call, no ID resolution or vtable lookup.
    /// </para>
    /// </summary>
    internal static (ulong Key, ParseDelegate Parse)[] BuildU64SparseDelegateCache(
        this Stack stack, ProtocolTableId tableId)
    {
        IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? entries =
            stack.GetU64TableEntries(tableId);
        if (entries is null)
        {
            return [];
        }

        List<(ulong Key, ParseDelegate Parse)> result = [];
        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> kvp in entries)
        {
            // Skip multi-protocol entries — those require the full table dispatch path.
            if (kvp.Value.Length == 1)
            {
                ParseDelegate? parse = stack.ResolveParseDelegate(kvp.Value.Span[0]);
                if (parse is not null)
                {
                    result.Add((kvp.Key, parse));
                }
            }
        }

        return [.. result];
    }

    #endregion

    #region ID Caches

    /// <summary>
    /// Builds a pre-computed dense ID cache for a u64 dispatch table over the key domain
    /// <c>[0, <paramref name="domainSize"/>)</c>.
    /// For each key, if exactly one protocol is registered, its <see cref="ProtocolId"/> is
    /// stored at <c>cache[key]</c>; keys with zero or multiple protocols get
    /// <see langword="null"/> so the caller falls back to full table dispatch.
    /// <para>
    /// Intended for 8-bit key domains (e.g., IP protocol byte, IPv6 next-header byte)
    /// where the full 256-entry array costs only ~1 kB — smaller than a typical dictionary's
    /// bucket array.
    /// </para>
    /// </summary>
    internal static ProtocolId?[] BuildU64IdCache(
        this Stack stack, ProtocolTableId tableId, int domainSize)
    {
        ProtocolId?[] cache = new ProtocolId?[domainSize];

        IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? entries =
            stack.GetU64TableEntries(tableId);
        if (entries is null)
        {
            return cache;
        }

        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> kvp in entries)
        {
            ulong key = kvp.Key;
            if (key < (ulong)domainSize && kvp.Value.Length == 1)
            {
                cache[key] = kvp.Value.Span[0];
            }
        }

        return cache;
    }

    /// <summary>
    /// Builds a pre-computed sparse ID cache for a u64 dispatch table, containing one entry
    /// per key where exactly one protocol is registered.
    /// <para>
    /// Suitable for tables with few registered protocols (e.g., EtherType → 4–6 entries, link
    /// type → 1–3 entries). A linear scan over the returned array is faster than dictionary
    /// hashing for small entry counts.
    /// </para>
    /// </summary>
    internal static (ulong Key, ProtocolId Id)[] BuildU64SparseIdCache(
        this Stack stack, ProtocolTableId tableId)
    {
        IEnumerable<KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>>>? entries =
            stack.GetU64TableEntries(tableId);
        if (entries is null)
        {
            return [];
        }

        List<(ulong Key, ProtocolId Id)> result = [];
        foreach (KeyValuePair<ulong, ReadOnlyMemory<ProtocolId>> kvp in entries)
        {
            if (kvp.Value.Length == 1)
            {
                result.Add((kvp.Key, kvp.Value.Span[0]));
            }
        }

        return [.. result];
    }
    #endregion
}
