// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Sort key accessor for packed entries of <see cref="ChunkedAppendOnlyStore{T}"/>.
/// </summary>
public interface ISortKeyed
{
    /// <summary>
    /// Key compared by <see cref="ChunkedAppendOnlyStoreExtensions.BinarySearch{T}"/>.
    /// Entries must be appended in strictly ascending <see cref="SortKey"/> order for the
    /// search to be valid.
    /// </summary>
    int SortKey { get; }
}
