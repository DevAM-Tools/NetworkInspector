// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Jobs;

/// <summary>
/// What the work of a job needs from its session: the token that ends it, the stack generation it started in, and the
/// reader to pull data from. Handing the <see cref="StackView"/> over (instead of letting the job read
/// <see cref="ISessionReader.Stack"/> later) pins the pair the job resolved its field ids on, so it can compare it with
/// <see cref="StackView.IsCurrent"/> before it uses them again.
///
/// <para>Thread-safety: immutable value; safe to copy. The token and the reader are themselves thread-safe.</para>
/// </summary>
/// <param name="Stack">The stack and generation current when the job's thread started.</param>
/// <param name="Session">The session to read from.</param>
/// <param name="CancellationToken">Fires when the job is cancelled (by a caller, a restart or the shutdown).</param>
public readonly record struct JobContext(StackView Stack, ISessionReader Session, CancellationToken CancellationToken);
