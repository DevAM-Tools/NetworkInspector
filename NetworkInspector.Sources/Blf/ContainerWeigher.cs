// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf;

/// <summary>
/// Weigher for BLF container cache entries.
/// Returns the byte array length as the weight (memory cost).
/// </summary>
/// <remarks>
/// The array length is the dominant cost of a cached container; the few dozen bytes of array header are ignored, so the
/// cache budget is the sum of the payload bytes it holds.
/// Stateless; thread-safe. The cache itself is not thread-safe and is guarded by the owning source's lock.
/// </remarks>
internal sealed class ContainerWeigher : IWeigher<long, byte[]>
{
    #region Public API

    /// <summary>Singleton instance.</summary>
    internal static readonly ContainerWeigher Instance = new();

    /// <inheritdoc/>
    public int Weigh(long key, byte[] value) => value.Length;

    #endregion
}
