// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Cache;

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Weigher for BLF container cache entries.
/// Returns the byte array length as the weight (memory cost).
/// </summary>
internal sealed class ContainerWeigher : IWeigher<long, byte[]>
{
    #region Public API

    /// <summary>Singleton instance.</summary>
    internal static readonly ContainerWeigher Instance = new();

    /// <inheritdoc/>
    public int Weigh(long key, byte[] value) => value.Length;

    #endregion
}
