// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Owns the mutable per-stack state for a stream of frames built from a
/// <see cref="StatefulCreatedStack{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// <para>
/// One session ⇔ one logical "flow" (e.g. a single TCP conversation, a single
/// IPv4 sender's IPID counter).  <see cref="NextPacket(System.ReadOnlySpan{byte})"/>
/// returns a <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/>
/// that emits one frame per <c>MoveNext</c> while updating the shared
/// <see cref="SessionState"/> in place — counters advance from frame to frame
/// transparently to the caller.
/// </para>
/// <para>
/// To keep allocation pressure near zero on the steady-state path the mutable
/// per-session state is carried in a pooled <see cref="Internals"/> object.
/// <see cref="Open"/> rents an <see cref="Internals"/> from the pool (or
/// allocates one) and wraps it in a fresh <see cref="Session{TStack,TTrailer,TInterceptor}"/>
/// handle.  <see cref="Dispose"/> increments the <see cref="Internals"/> version counter
/// and returns the internals to the pool, while the lightweight handle is
/// left for the GC.  The pool is bounded by <see cref="PoolCapacity"/>; once
/// full, <see cref="Dispose"/> drops the internals for the GC instead.
/// </para>
/// <para>
/// Stale-handle detection: each <see cref="Session{TStack,TTrailer,TInterceptor}"/>
/// handle captures the <see cref="Internals"/> version counter at open time.
/// When the session is disposed the version is incremented in the
/// <see cref="Internals"/> object before it is returned to the pool.
/// Any caller that retains an old handle will see the version
/// mismatch in <see cref="ThrowIfDisposed"/> and receive an
/// <see cref="ObjectDisposedException"/> — even if the internals have been
/// re-rented by a new caller.
/// </para>
/// <para>Thread safety: instances are not thread-safe.  The pool itself is.</para>
/// </remarks>
/// <typeparam name="TStack">Cons-list shape (mixed stateful/stateless allowed).</typeparam>
/// <typeparam name="TTrailer">Trailer type (use <see cref="NoTrailer"/> for none).</typeparam>
/// <typeparam name="TInterceptor">Interceptor type (use <see cref="NoInterceptor"/> for none).</typeparam>
public sealed class Session<TStack, TTrailer, TInterceptor> : IDisposable
    where TStack : struct, IStackNode
    where TTrailer : struct, ITrailerLayer
    where TInterceptor : struct, IFrameInterceptor
{
    /// <summary>Maximum number of pooled internals instances kept alive per concrete generic instantiation.</summary>
    public const int PoolCapacity = 8;

    // -----------------------------------------------------------------
    // Pooled mutable state — the object that travels through the pool.
    // The Session handle is never pooled; only Internals is.
    // -----------------------------------------------------------------

    /// <summary>
    /// Holds all mutable per-session fields.  Pooled to avoid allocation on
    /// the steady-state path while keeping the outer Session handle immutable
    /// after construction (enabling version-based stale-handle detection).
    /// </summary>
    private sealed class Internals
    {
        /// <summary>
        /// Lock-free pool of reusable internals instances.
        /// Per-instantiation static field ensures each generic shape has its own pool.
        /// </summary>
        internal static readonly Internals?[] Pool = new Internals?[PoolCapacity];

        internal TStack Values;
        internal TTrailer Trailer;
        internal TInterceptor Interceptor;
        internal SessionState State;

        /// <summary>
        /// Monotonically increasing counter that is incremented each time this
        /// internals object is returned to the pool via <see cref="Session{TStack,TTrailer,TInterceptor}.Dispose"/>.
        /// The outer Session handle captures this value in <see cref="_OpenVersion"/>
        /// at construction time; a mismatch means the handle is stale.
        /// </summary>
        internal uint Version;
    }

    // -----------------------------------------------------------------
    // Handle fields — set once at construction, never mutated.
    // -----------------------------------------------------------------

    private readonly Internals _I;

    /// <summary>Version of <see cref="_I"/> captured when this handle was created.</summary>
    private readonly uint _OpenVersion;

    private bool _Disposed;

    private Session(Internals internals)
    {
        _I = internals;
        _OpenVersion = internals.Version;
    }

    /// <summary>Opens a session — internal use only; called via <see cref="StatefulCreatedStack{TStack,TTrailer,TInterceptor}.OpenSession"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Session<TStack, TTrailer, TInterceptor> Open(
        in TStack values,
        in TTrailer trailer,
        in TInterceptor interceptor)
    {
        Internals internals = TryRentFromPool() ?? new Internals();
        internals.Values = values;
        internals.Trailer = trailer;
        internals.Interceptor = interceptor;
        internals.State = default;
        internals.Values.InitializeStatefulState(ref internals.State);
        return new Session<TStack, TTrailer, TInterceptor>(internals);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Internals? TryRentFromPool()
    {
        // Lock-free linear scan.  The cheap volatile read filters out empty
        // slots so that Interlocked.Exchange (the expensive fenced operation)
        // is only attempted on slots that look populated; the Exchange itself
        // resolves any race between concurrent renters of the same slot.
        for (int i = 0; i < PoolCapacity; i++)
        {
            if (Volatile.Read(ref Internals.Pool[i]) is null)
            {
                continue;
            }
            Internals? candidate = Interlocked.Exchange(ref Internals.Pool[i], null);
            if (candidate is not null)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this session has been disposed
    /// or if the underlying internals object has since been re-rented by another caller
    /// (stale-handle detection via version mismatch).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_Disposed || _I.Version != _OpenVersion)
        {
            ThrowDisposed();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDisposed() =>
        throw new ObjectDisposedException(GetType().Name);



    /// <summary>
    /// Sum of every layer's header size in bytes.  Use this to size destination
    /// buffers: <c>HeaderSize + payloadLength</c> is the exact wire-frame size
    /// for unfragmented traffic.
    /// </summary>
    public int HeaderSize => _I.Values.TotalHeaderSize;

    /// <summary>Trailer size in bytes (0 when <typeparamref name="TTrailer"/> is <see cref="NoTrailer"/>).</summary>
    public int TrailerSize => _I.Trailer.TrailerSize;

    /// <summary>
    /// Smallest MTU asserted along the cons-list, in bytes; <see cref="int.MaxValue"/>
    /// when no layer asserts an MTU.  Frames larger than this value will be
    /// fragmented by <see cref="NextPacket(System.ReadOnlySpan{byte})"/>.
    /// </summary>
    public int MaxFrameSize => _I.Values.MaxFrameLength;

    /// <summary>
    /// Begins a build for the next packet in this session.  The returned
    /// iterator yields one frame per <c>MoveNext</c>.  Counters and per-flow
    /// state are advanced as part of the write walk.
    /// </summary>
    /// <param name="payload">Payload for this packet (after every layer's header).</param>
    /// <exception cref="ObjectDisposedException">If the session has been disposed or the handle is stale.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StatefulFrameSequence<TStack, TTrailer, TInterceptor> NextPacket(ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        return new StatefulFrameSequence<TStack, TTrailer, TInterceptor>(
            _I.Values, _I.Trailer, _I.Interceptor, ref _I.State, payload);
    }

    /// <summary>
    /// Updates the TCP acknowledgement number written into subsequent frames
    /// produced by this session.  Has no effect on frames already emitted.
    /// Application code calls this in response to incoming ACKs.
    /// </summary>
    /// <param name="ack">New TCP acknowledgement number (host order).</param>
    /// <exception cref="ObjectDisposedException">If the session has been disposed or the handle is stale.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateAck(uint ack)
    {
        ThrowIfDisposed();
        _I.State.TcpAck = ack;
    }

    // ------------------------------------------------------------------
    // TcpStreamLayer accessors used by TcpConnection.  These intentionally
    // bypass the TcpAck/TcpNextSeq slots owned by TcpLayerWithAutoSequence
    // — TcpStreamLayer uses its own dedicated slot set (TcpStream*).
    // ------------------------------------------------------------------

    /// <summary>Reads the current TcpStream NextSeq slot without advancing it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint PeekTcpStreamNextSeq()
    {
        ThrowIfDisposed();
        return _I.State.TcpStreamNextSeq;
    }

    /// <summary>Reads the current TcpStream Ack slot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint PeekTcpStreamAck()
    {
        ThrowIfDisposed();
        return _I.State.TcpStreamAck;
    }

    /// <summary>Replaces the TcpStream Ack slot with <paramref name="ack"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetTcpStreamAck(uint ack)
    {
        ThrowIfDisposed();
        _I.State.TcpStreamAck = ack;
    }

    /// <summary>Adds <paramref name="delta"/> to the TcpStream Ack slot (wrap-around per uint).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AdvanceTcpStreamAck(uint delta)
    {
        ThrowIfDisposed();
        unchecked
        {
            _I.State.TcpStreamAck += delta;
        }
    }

    /// <summary>Replaces the TcpStream Window slot with <paramref name="window"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetTcpStreamWindow(ushort window)
    {
        ThrowIfDisposed();
        _I.State.TcpStreamWindow = window;
    }

    /// <summary>Reads the current TcpStream Window slot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ushort PeekTcpStreamWindow()
    {
        ThrowIfDisposed();
        return _I.State.TcpStreamWindow;
    }

    /// <summary>Stages all per-frame TcpStream slots in one shot before a NextPacket call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PrepareTcpStreamFrame(uint seq, uint ack, byte flags, ushort window, ushort urgent)
    {
        ThrowIfDisposed();
        _I.State.TcpStreamNextSeq = seq;
        _I.State.TcpStreamAck = ack;
        _I.State.TcpStreamFlags = flags;
        _I.State.TcpStreamWindow = window;
        _I.State.TcpStreamUrgent = urgent;
    }

    /// <summary>
    /// Returns the underlying internals to the pool (or drops them when the pool is full)
    /// and invalidates this handle.  After disposal the session must not be used.
    /// Idempotent: calling <c>Dispose</c> more than once is safe.
    /// </summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;

        // Increment the version so any stale references held by former callers
        // will detect the mismatch in ThrowIfDisposed and receive ObjectDisposedException
        // even if the internals are re-rented by a new caller.
        _I.Version++;

        // Try to return internals to pool.  Find an empty slot; if none, drop.
        for (int i = 0; i < PoolCapacity; i++)
        {
            // CompareExchange keeps the operation lock-free.
            if (Interlocked.CompareExchange(ref Internals.Pool[i], _I, null) is null)
            {
                return;
            }
        }
        // Pool full → fall through; the GC will collect the internals object.
    }
}
