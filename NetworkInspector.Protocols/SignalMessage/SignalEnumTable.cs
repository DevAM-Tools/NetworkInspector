// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Immutable enum-name lookup for one signal. Prefer dense arrays when value names
/// occupy a contiguous low or high band of the raw range; otherwise sparse dictionary.
/// </summary>
/// <remarks>
/// Thread safety: immutable after construction; safe for concurrent reads. The dense array and the frozen dictionary
/// are not copied: the builder creates them and never touches them again, and a caller that passes its own array
/// to a factory must not modify it afterwards.
/// </remarks>
internal readonly struct SignalEnumTable
{
    #region Fields

    // Only the storage that matches Kind is set; the other reference stays null. _MaxRaw is only used by DenseHigh
    // (index = _MaxRaw - raw). The struct is small and copied by value into SignalInfo, so no extra allocation or
    // indirection is needed per signal.
    private readonly string[]? _DenseNames;
    private readonly FrozenDictionary<ulong, string>? _SparseNames;
    private readonly ulong _MaxRaw;

    #endregion

    #region Construction

    /// <summary>
    /// Creates a table with explicit storage. Used by factories and tests for miss paths (for example a
    /// dense array with empty slots, which the builder itself never produces).
    /// </summary>
    internal SignalEnumTable(
        SignalEnumKind kind,
        string[]? denseNames,
        FrozenDictionary<ulong, string>? sparseNames,
        ulong maxRaw)
    {
        Kind = kind;
        _DenseNames = denseNames;
        _SparseNames = sparseNames;
        _MaxRaw = maxRaw;
    }

    /// <summary>Empty table (no value names).</summary>
    internal static SignalEnumTable None { get; } = new(SignalEnumKind.None, null, null, 0);

    /// <summary>Creates a dense-low table where index equals the raw value.</summary>
    internal static SignalEnumTable CreateDenseLow(string[] names)
        => new(SignalEnumKind.DenseLow, names, null, 0);

    /// <summary>Creates a dense-high table where index equals <c>maxRaw - raw</c>.</summary>
    internal static SignalEnumTable CreateDenseHigh(string[] names, ulong maxRaw)
        => new(SignalEnumKind.DenseHigh, names, null, maxRaw);

    /// <summary>Creates a sparse frozen-dictionary table.</summary>
    internal static SignalEnumTable CreateSparse(FrozenDictionary<ulong, string> names)
        => new(SignalEnumKind.Sparse, null, names, 0);

    #endregion

    #region Properties

    /// <summary>Storage strategy.</summary>
    internal SignalEnumKind Kind { get; }

    #endregion

    #region Lookup

    /// <summary>
    /// Resolves a display name for <paramref name="raw"/>. Returns <see langword="false"/> when
    /// there is no mapping (including empty slots in dense arrays). A miss is an expected outcome (a raw value
    /// without a configured name), so it is reported through the return value, never by an exception.
    /// <para>
    /// Inlined because it runs once per materialized enum signal. Every branch compares in <see cref="ulong"/>
    /// before casting to <see cref="int"/>, so a large raw value can neither wrap to a negative index nor read
    /// outside the array.
    /// </para>
    /// </summary>
    /// <param name="raw">The raw (unsigned) signal value.</param>
    /// <param name="name">The display name on success; otherwise <see langword="null"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetName(ulong raw, [NotNullWhen(true)] out string? name)
    {
        switch (Kind)
        {
            case SignalEnumKind.DenseLow:
            {
                string[]? names = _DenseNames;
                if (names is not null && raw < (ulong)names.Length)
                {
                    string? entry = names[(int)raw];
                    if (entry is not null)
                    {
                        name = entry;
                        return true;
                    }
                }

                name = null;
                return false;
            }
            case SignalEnumKind.DenseHigh:
            {
                // Dense-high stores the name of the largest raw value at index 0. A raw above _MaxRaw cannot occur for
                // a signal of the configured width, but is rejected here so the unsigned subtraction cannot wrap.
                string[]? names = _DenseNames;
                if (names is null || raw > _MaxRaw)
                {
                    name = null;
                    return false;
                }

                ulong indexU = _MaxRaw - raw;
                if (indexU >= (ulong)names.Length)
                {
                    name = null;
                    return false;
                }

                string? entry = names[(int)indexU];
                if (entry is not null)
                {
                    name = entry;
                    return true;
                }

                name = null;
                return false;
            }
            case SignalEnumKind.Sparse:
            {
                FrozenDictionary<ulong, string>? map = _SparseNames;
                if (map is not null && map.TryGetValue(raw, out string? entry))
                {
                    name = entry;
                    return true;
                }

                name = null;
                return false;
            }
            default:
                name = null;
                return false;
        }
    }

    #endregion
}
