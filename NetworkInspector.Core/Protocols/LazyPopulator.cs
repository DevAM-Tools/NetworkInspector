// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Delegate for lazily populating the children of a container field.
/// Called exactly once when the field's children are first accessed.
/// <para>Implementations MUST NOT call protocol dispatch methods (TryCallNextProtocol*).</para>
/// <para>Returns <see cref="ParseResult"/> to propagate errors back to the caller.
/// On success, the returned int value is not significant (convention: return 0).
/// On failure, the returned <see cref="ParseError"/> is attached as a child of the
/// lazy container field by the materialization infrastructure.</para>
/// </summary>
/// <param name="parentField">The lazy parent whose children should be populated.</param>
/// <returns>A <see cref="ParseResult"/> indicating success or describing the error.</returns>
public delegate ParseResult LazyPopulator(in MutField parentField);