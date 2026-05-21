// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index.ValueCache;

/// <summary>
/// Holds one or two typed value arrays for a <see cref="ValueCacheSeries"/>.
/// For most types only <see cref="Primary"/> is used. For 128-bit types
/// (IPv6, Uuid), <see cref="Secondary"/> holds the high <see langword="ulong"/>[] array.
/// </summary>
public readonly struct ValueCacheData
{
    #region Fields

    /// <summary>Primary value array (typed: ulong[], long[], double[], uint[], float[], sbyte[], short[], int[], byte[], ushort[]).</summary>
    private readonly object _Primary;

    /// <summary>Secondary value array. Only used for 128-bit types (ulong[] high). Null otherwise.</summary>
    private readonly object? _Secondary;

    #endregion

    #region Constructors

    /// <summary>Creates data with a single value array (most types).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueCacheData(object primary)
    {
        _Primary = primary;
        _Secondary = null;
    }

    /// <summary>Creates data with two value arrays (128-bit types: low + high).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueCacheData(object primary, object secondary)
    {
        _Primary = primary;
        _Secondary = secondary;
    }

    #endregion

    #region Properties

    /// <summary>The primary value array (raw object reference).</summary>
    internal object Primary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Primary;
    }

    /// <summary>The secondary value array (raw object reference, may be null).</summary>
    internal object? Secondary
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Secondary;
    }

    #endregion

    #region Public API

    /// <summary>Gets the primary array cast to <typeparamref name="T"/>[].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] AsArray<T>() where T : unmanaged => (T[])_Primary;

    /// <summary>Gets the primary array as a <see cref="ReadOnlySpan{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> AsSpan<T>() where T : unmanaged => ((T[])_Primary).AsSpan();

    /// <summary>Gets dual <see langword="ulong"/>[] arrays for 128-bit types (low + high).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (ulong[] Low, ulong[] High) AsDualUlong() =>
        ((ulong[])_Primary, (ulong[])_Secondary!);

    /// <summary>Gets the dense bit-packed <see langword="byte"/>[] for Bool storage.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] AsBoolBits() => (byte[])_Primary;

    /// <summary>Estimates memory usage of the stored arrays in bytes.</summary>
    public long EstimateMemoryUsage()
    {
        long size = EstimateArraySize(_Primary);
        if (_Secondary is not null)
        {
            size += EstimateArraySize(_Secondary);
        }
        return size;
    }

    #endregion

    #region Private Helpers

    /// <summary>Estimates the memory usage of a single array or bitmap.</summary>
    private static long EstimateArraySize(object array) => array switch
    {
        ulong[] a => a.Length * sizeof(ulong),
        long[] a => a.Length * sizeof(long),
        double[] a => a.Length * sizeof(double),
        uint[] a => a.Length * sizeof(uint),
        float[] a => a.Length * sizeof(float),
        int[] a => a.Length * sizeof(int),
        short[] a => a.Length * sizeof(short),
        ushort[] a => a.Length * sizeof(ushort),
        sbyte[] a => a.Length * sizeof(sbyte),
        byte[] a => a.Length * sizeof(byte),
        _ => throw new InvalidOperationException($"Unsupported array type: {array.GetType().Name}"),
    };

    #endregion
}
