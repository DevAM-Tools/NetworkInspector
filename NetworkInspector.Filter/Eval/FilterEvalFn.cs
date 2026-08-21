// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// The compiled form of a filter predicate: one delegate that evaluates the packet currently
/// bound to <paramref name="context"/>.
/// <para>
/// Runtime problems are reported through <see cref="FilterEvalContext.SetError"/> rather than by
/// throwing, so the hot path contains no exception handling.
/// </para>
/// </summary>
internal delegate bool FilterEvalFn(FilterEvalContext context);
