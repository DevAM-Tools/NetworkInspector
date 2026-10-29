// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// The first-parse versus replay decision of a stateful protocol.
/// <para>
/// A protocol that keeps state across packets (a TCP tracker, a fragment reassembler, a timing table) may change that state
/// only while a packet is parsed for the <em>first</em> time, in packet order, on one thread. Every later parse of the same
/// packet id (a re-parse on any number of threads, also while later packets are first-parsed) must read the facts the first
/// parse recorded and must not touch the state again. This type is that decision: <see cref="IsReplay"/> says which path a
/// parse takes, <see cref="Enter"/> opens the first-parse scope, and ending the outermost scope raises the watermark that
/// turns the packet into a replay and publishes everything recorded inside the scope.
/// </para>
/// <para>
/// <b>Usage:</b>
/// <code>
/// if (_Gate.IsReplay(packet.Id)) { return _Replay(...); }
/// using IngestScope scope = _Gate.Enter(packet.Id);
/// // mutate state, record the effects of this packet
/// </code>
/// </para>
/// <para>
/// <b>Why a watermark and not a per-packet flag:</b> packet ids are first parsed in dense ascending order, so "id is at most
/// the highest completed id" is one volatile read with no per-packet memory. <b>Why the outermost scope raises it:</b> a
/// nested parse of the same packet (a tunnel parses a protocol twice) must still take the first-parse path, so only the
/// outermost scope may make the packet a replay. <b>Why the raise is in <see cref="IngestScope.Dispose"/>:</b> the scope ends
/// also when the parse throws, otherwise a later parse of the same id would mutate the state a second time.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="IsReplay"/> and <see cref="Watermark"/> may be called from any thread (one volatile read).
/// <see cref="Enter"/> and the scope belong to the single ordered first-parse thread; the nesting counter is a plain
/// <see cref="int"/> because no other thread touches it (a replay returns before it). The watermark is written with a volatile
/// write after the effects of the packet were recorded, so a reader that sees <see cref="IsReplay"/> true also sees every
/// effect of that packet (release/acquire). No lock or <see cref="Interlocked"/> maximum is used because there is no second
/// writer to race with.
/// </para>
/// </summary>
public sealed class IngestGate
{
    #region Fields

    /// <summary>Highest packet id whose first parse completed; -1 until the first packet was parsed. Volatile: the release/acquire pair of the publication.</summary>
    private volatile int _Watermark = -1;

    /// <summary>
    /// Number of open first-parse scopes. Plain int, no volatile or Interlocked: only the serialized first-parse thread reads
    /// or writes it (a replay returns before touching it), so there is no concurrent access to protect.
    /// </summary>
    private int _Nesting;

    #endregion

    #region Properties

    /// <summary>The highest packet id whose first parse completed; -1 until a packet was parsed. Readable from any thread.</summary>
    public int Watermark => _Watermark;

    #endregion

    #region Public API

    /// <summary>
    /// Whether the packet was already parsed once and must therefore replay its recorded effects instead of mutating state.
    /// </summary>
    /// <param name="id">The packet being parsed.</param>
    /// <returns>True for a replay. Packet ids are first parsed in dense ascending order, so <c>id &lt;= watermark</c> means this id (or a later one) completed its first parse.</returns>
    public bool IsReplay(PacketId id) => id.Value <= _Watermark;

    /// <summary>
    /// Opens the first-parse scope of a packet. Dispose the scope (a <c>using</c> declaration) when the parse ends, also on
    /// an error or exception.
    /// </summary>
    /// <param name="id">The packet being parsed for the first time.</param>
    /// <returns>The scope; ending the outermost one raises the watermark to <paramref name="id"/>.</returns>
    /// <exception cref="InvalidOperationException">The packet was already parsed once. Entering for a replayed id is a bug of the caller: it would mutate shared state on a re-parse.</exception>
    public IngestScope Enter(PacketId id)
    {
        if (IsReplay(id))
        {
            throw new InvalidOperationException("Enter called for a packet that was already parsed once.");
        }

        _Nesting++;
        return new IngestScope(this, id);
    }

    #endregion

    #region Internal API

    /// <summary>Ends one scope; the outermost one raises the watermark monotonically.</summary>
    /// <param name="id">The packet of the scope that ends.</param>
    internal void Leave(PacketId id)
    {
        _Nesting--;
        if (_Nesting == 0 && id.Value > _Watermark)
        {
            // The volatile write is the release that publishes every effect recorded inside the scope to replaying threads.
            _Watermark = id.Value;
        }
    }

    #endregion
}

/// <summary>
/// An open first-parse scope of an <see cref="IngestGate"/>. Disposing the outermost scope raises the watermark.
/// <para>
/// <b>Thread-safety:</b> a stack-only value of the first-parse thread. <see cref="Dispose"/> must be called exactly once; the
/// default value is an inert scope (a replay holds one so the call site needs no branch around its <c>finally</c>).
/// </para>
/// </summary>
public readonly ref struct IngestScope
{
    private readonly IngestGate? _Gate;
    private readonly PacketId _Id;

    internal IngestScope(IngestGate gate, PacketId id)
    {
        _Gate = gate;
        _Id = id;
    }

    /// <summary>Ends the scope; the outermost scope of a packet raises the watermark and thereby publishes its effects.</summary>
    public void Dispose() => _Gate?.Leave(_Id);
}
