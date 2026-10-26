// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Delegate for lazily populating the children of a container field.
/// Called at most once per container: when the field's children are first accessed, or immediately at registration when
/// a watch (or, for a skip-tree parse, the value cache) needs the children. A container nobody reads is never populated.
/// After the packet is sealed it may run on any thread that reads the packet; the packet guarantees that exactly one
/// thread executes each populator, so an implementation needs no synchronization of its own for its own output.
/// The populator can run after <c>Parse</c> returned, so it may capture only state that outlives the parse
/// (for example <see cref="ReadOnlyMemory{T}"/>), never the stack-only <see cref="ParseContext"/>.
/// <para>Implementations MUST NOT call protocol dispatch methods (TryCallNextProtocol*) and
/// MUST NOT mutate the packet index. The index is finalized eagerly in <c>Parse</c>.</para>
/// <para>
/// The parameter is an <see cref="MutField"/>, but no <see cref="ParseContext"/> is available
/// at materialization time. Because every <see cref="MutField"/> dispatch method requires an
/// explicit <c>in</c> <see cref="ParseContext"/>, a populator is structurally incapable of
/// dispatching or recording index state — the lazy-path contract is enforced by the type system.
/// </para>
/// <para>Returns <see cref="ParseResult"/> to propagate errors back to the caller.
/// On success, the returned int value is not significant (convention: return 0).
/// On failure, the returned <see cref="ParseError"/> is attached as a child of the
/// lazy container field by the materialization infrastructure.</para>
/// </summary>
/// <param name="parentField">The lazy parent whose children should be populated.</param>
/// <returns>A <see cref="ParseResult"/> indicating success or describing the error.</returns>
public delegate ParseResult LazyPopulator(in MutField parentField);
