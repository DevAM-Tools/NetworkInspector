// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
