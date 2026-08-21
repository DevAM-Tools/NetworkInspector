// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Immutable enum-name lookup for one signal. Prefer dense arrays when value names
/// occupy a contiguous low or high band of the raw range; otherwise sparse dictionary.
/// </summary>
/// <remarks>Thread safety: immutable after construction; safe for concurrent reads.</remarks>
internal readonly struct SignalEnumTable
{
    #region Fields

    private readonly string[]? _DenseNames;
    private readonly FrozenDictionary<ulong, string>? _SparseNames;
    private readonly ulong _MaxRaw;

    #endregion

    #region Construction

    /// <summary>Creates a table with explicit storage. Used by factories and tests for miss paths.</summary>
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
    /// there is no mapping (including empty slots in dense arrays).
    /// </summary>
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
