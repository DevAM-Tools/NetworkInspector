// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Collections;

/// <summary>
/// Extracts the integer sort key from a packed <see cref="ChunkedAppendOnlyStore{T}"/> entry.
/// Pass a cached static delegate into <see cref="ChunkedAppendOnlyStore{T}.BinarySearch"/>.
/// </summary>
/// <typeparam name="T">Entry type stored in the packed prefix.</typeparam>
/// <param name="item">Published entry.</param>
/// <returns>Sort key used by binary search.</returns>
public delegate int GetSortKey<T>(in T item);

/// <summary>
/// Returns a by-ref class field of a packed slot so <see cref="ChunkedAppendOnlyStore{T}.ReadVolatileRefField{TField}"/>
/// can <see cref="Volatile"/>-read the published location.
/// </summary>
/// <typeparam name="T">Packed entry type.</typeparam>
/// <typeparam name="TField">Class field type.</typeparam>
/// <param name="item">Live slot.</param>
/// <returns>Reference to the field inside <paramref name="item"/>.</returns>
public delegate ref TField GetRefField<T, TField>(ref T item) where TField : class?;
