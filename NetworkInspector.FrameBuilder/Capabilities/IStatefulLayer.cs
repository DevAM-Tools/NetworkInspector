// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker for layers that carry per-frame mutable state across a
/// <see cref="Session{TStack,TTrailer,TInterceptor}"/> (IPv4 Identification counter,
/// TCP sequence number, …).
/// </summary>
/// <remarks>
/// <para>
/// Stateful layers are used exclusively through a session that owns the
/// per-stack <see cref="SessionState"/>; they are not callable through the
/// stateless <see cref="CreatedStack{TStack,TTrailer,TInterceptor}.Build(System.ReadOnlySpan{byte})"/>
/// entry points (compile-time enforced via the <c>IStatelessStack</c> constraint).
/// </para>
/// <para>
/// A single shared <see cref="SessionState"/> struct carries all per-flow state
/// across the cons-list.  Each layer's <see cref="InitializeState"/> sets its
/// slot's <c>Has*</c> flag and seeds the initial values;
/// <see cref="WriteHeader(System.Span{byte},ref SessionState)"/> reads and
/// updates only its own slot.
/// </para>
/// </remarks>
public interface IStatefulLayer : IProtocolLayer
{
    /// <summary>
    /// Initialises this layer's slot inside the shared <paramref name="state"/>.
    /// Called once per <see cref="Session{TStack,TTrailer,TInterceptor}"/> open.
    /// </summary>
    void InitializeState(ref SessionState state);

    /// <summary>
    /// Writes this layer's header bytes into <paramref name="dst"/>, reading
    /// and mutating its slot in <paramref name="state"/> as needed (e.g.
    /// incrementing a counter).
    /// </summary>
    /// <remarks>
    /// Length and checksum fields must be written as zeroes here; they are
    /// patched in subsequent <see cref="FixPhase"/> walks.
    /// </remarks>
    void WriteHeader(scoped Span<byte> dst, ref SessionState state);
}

