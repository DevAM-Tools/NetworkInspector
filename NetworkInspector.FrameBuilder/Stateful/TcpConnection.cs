// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Static factory entry points for opening a
/// <see cref="TcpConnection{TCarrierOld,TCarrierTail}"/>.  Lives in a
/// non-generic class so callers don't have to spell out the carrier
/// generic parameters.
/// </summary>
public static class TcpConnection
{
    /// <summary>
    /// Opens a bidirectional TCP connection on top of two stateless carrier
    /// stacks (one per direction).  The carriers must end at a layer that
    /// implements <see cref="IProvidesPseudoHeader"/> (an IP layer), and
    /// must NOT contain a TCP layer themselves — the connection appends
    /// its own internal <see cref="TcpStreamLayer"/> per direction.
    /// </summary>
    /// <param name="clientCarrier">Carrier stack used for client→server segments.</param>
    /// <param name="serverCarrier">Carrier stack used for server→client segments.</param>
    /// <param name="clientPort">Source port the client uses (= destination port the server sees).</param>
    /// <param name="serverPort">Source port the server uses (= destination port the client sees).</param>
    /// <param name="options">Connection-level options (ISN per side, MSS, default window).</param>
    /// <typeparam name="TOld">Outer-most carrier layer (must publish a pseudo-header).</typeparam>
    /// <typeparam name="TTail">Inner cons-list tail of the carrier stack.</typeparam>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TcpConnection<TOld, TTail> Open<TOld, TTail>(
        in StatelessStack<TOld, TTail> clientCarrier,
        in StatelessStack<TOld, TTail> serverCarrier,
        ushort clientPort,
        ushort serverPort,
        TcpConnectionOptions options = default)
        where TOld : struct, IStatelessLayer, IInteriorLayer, IProvidesPseudoHeader
        where TTail : struct, IStackNode, IStatelessStack
    {
        if (options.Mss == 0)
        {
            options = options with
            {
                Mss = 1460,
            };
        }
        if (options.WindowSize == 0)
        {
            options = options with
            {
                WindowSize = 65535,
            };
        }

        TcpStreamLayer clientLayer = new(clientPort, serverPort, options.ClientIsn, initialAck: 0u, options.WindowSize);
        TcpStreamLayer serverLayer = new(serverPort, clientPort, options.ServerIsn, initialAck: 0u, options.WindowSize);

        Stack<TcpStreamLayer, StatelessStack<TOld, TTail>> clientStack = clientCarrier.Then(clientLayer);
        Stack<TcpStreamLayer, StatelessStack<TOld, TTail>> serverStack = serverCarrier.Then(serverLayer);

        StatefulCreatedStack<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> clientCreated =
            StatefulFrameStack.CreateForSession(in clientStack);
        StatefulCreatedStack<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> serverCreated =
            StatefulFrameStack.CreateForSession(in serverStack);

        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> clientSession = clientCreated.OpenSession();
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> serverSession = serverCreated.OpenSession();

        return new TcpConnection<TOld, TTail>(clientSession, serverSession, options, clientPort, serverPort);
    }

    /// <summary>
    /// Thread-local <see cref="ArrayBufferWriter{T}"/> reused across all
    /// <see cref="TcpConnection{TOld,TTail}.WriteFromClient{TProducer}"/> /
    /// <see cref="TcpConnection{TOld,TTail}.WriteFromServer{TProducer}"/> calls
    /// on this thread.  Safe to reuse because <see cref="TcpConnection{TOld,TTail}"/>
    /// is not reentrant and all calls on the same thread are serialised.
    /// </summary>
    [ThreadStatic]
    internal static ArrayBufferWriter<byte>? _SharedProducerBuffer;
}

/// <summary>
/// Bidirectional TCP connection façade over the FrameBuilder.  Composes
/// two <see cref="Session{TStack,TTrailer,TInterceptor}"/> instances (one
/// per direction) on top of an internal <see cref="TcpStreamLayer"/> and
/// emits one or more wire-ready segments per API call directly into a
/// caller-supplied <see cref="FrameSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>On-the-fly emission.</b>  Every Emit/Write method runs the layer
/// machinery synchronously and invokes <c>sink</c> once per
/// produced segment, in wire order, before returning.  No internal
/// segment buffering; the caller may stream arbitrarily large payloads
/// in incremental Write calls.
/// </para>
/// <para>
/// <b>Mutator hook.</b>  An optional <see cref="OnSegment"/> mutator (or
/// per-call override) is invoked AFTER defaults have been populated for
/// the current segment but BEFORE the segment is serialised.  Changes
/// to the descriptor — including SYN/FIN flag flips that re-shape SEQ
/// accounting — feed the writer and the checksum pass transparently.
/// When no mutator is registered the hot-path skips the descriptor
/// dance entirely (no allocation, no branch overhead).
/// </para>
/// <para>
/// <b>Out-of-scope.</b>  Out-of-order delivery, retransmissions, window
/// probing, malformed segments — those continue to be the territory of
/// <see cref="TcpLayer"/> / <see cref="TcpLayerWithAutoSequence"/>.
/// <see cref="TcpConnection{TOld,TTail}"/> models the happy path of a
/// clean bidirectional stream.
/// </para>
/// <para>Thread safety: not thread-safe.  Each instance owns two pooled sessions; concurrent calls across instances are safe.</para>
/// </remarks>
/// <typeparam name="TOld">Outer-most carrier layer (must publish a pseudo-header).</typeparam>
/// <typeparam name="TTail">Inner cons-list tail of the carrier stack.</typeparam>
public sealed class TcpConnection<TOld, TTail> : IDisposable
    where TOld : struct, IStatelessLayer, IInteriorLayer, IProvidesPseudoHeader
    where TTail : struct, IStackNode, IStatelessStack
{
    private readonly Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> _ClientSession;
    private readonly Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> _ServerSession;
    private readonly TcpConnectionOptions _Options;

    /// <summary>Source port the client side uses.</summary>
    public ushort ClientPort { get; }

    /// <summary>Source port the server side uses.</summary>
    public ushort ServerPort { get; }

    /// <summary>
    /// Wire-layer header overhead per segment: carrier headers + TCP header (20 bytes).
    /// Use <c>HeaderSize + payloadLength</c> to compute the exact scratch-buffer size
    /// needed for any given segment payload.
    /// </summary>
    public int HeaderSize { get; }

    /// <summary>Reusable scratch buffer; sized to <c>HeaderSize + MSS</c> at construction and grown on demand.</summary>
    private byte[] _Scratch;

    private bool _Disposed;
    private bool _HandshakeDone;

    /// <summary>
    /// Optional mutator invoked once per emitted segment.  Set to <c>null</c>
    /// (default) for zero-overhead operation.  Per-call <c>mutator:</c>
    /// arguments override this for the duration of a single API call.
    /// </summary>
    public TcpSegmentMutator? OnSegment
    {
        get; set;
    }

    /// <summary>Effective Maximum Segment Size used for splitting Write payloads.</summary>
    public ushort Mss
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Options.Mss;
    }

    /// <summary>Sequence number that the next client→server segment will carry.</summary>
    public uint ClientNextSeq
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ClientSession.PeekTcpStreamNextSeq();
    }

    /// <summary>Sequence number that the next server→client segment will carry.</summary>
    public uint ServerNextSeq
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _ServerSession.PeekTcpStreamNextSeq();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TcpConnection(
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> clientSession,
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> serverSession,
        TcpConnectionOptions options,
        ushort clientPort,
        ushort serverPort)
    {
        _ClientSession = clientSession;
        _ServerSession = serverSession;
        _Options = options;
        ClientPort = clientPort;
        ServerPort = serverPort;
        // Size the initial scratch buffer exactly: one full header + one MSS-worth of payload.
        // Both sessions must have the same stack shape (same carrier applied to both sides),
        // but take the max in case caller-supplied asymmetric carriers differ in header size.
        HeaderSize = Math.Max(clientSession.HeaderSize, serverSession.HeaderSize);
        _Scratch = new byte[HeaderSize + options.Mss];
    }

    #region Lifecycle: Handshake / Teardown / Reset

    /// <summary>
    /// Emits the standard 3-way handshake: SYN (client), SYN+ACK (server),
    /// ACK (client).  Three segments delivered to <paramref name="sink"/>
    /// in wire order.  After return, the connection is in the established
    /// state and ready for Write calls.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the handshake has already been emitted.</exception>
    public void EmitHandshake(FrameSink sink, TcpSegmentMutator? mutator = null)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        if (_HandshakeDone)
        {
            throw new InvalidOperationException("EmitHandshake has already been called on this connection.");
        }
        ArgumentNullException.ThrowIfNull(sink);

        TcpSegmentMutator? effective = mutator ?? OnSegment;

        // SYN (client → server)
        _EmitOneSegment(
            isClient: true,
            phase: TcpLifecycle.Handshake,
            defaultFlags: TcpFlags.Syn,
            payload: ReadOnlySpan<byte>.Empty,
            segmentIndex: 0,
            segmentCount: 3,
            sink: sink,
            mutator: effective);

        // After client SYN: server's expected ACK from client is ClientIsn + 1.
        _ServerSession.SetTcpStreamAck(_Options.ClientIsn + 1u);

        // SYN+ACK (server → client)
        _EmitOneSegment(
            isClient: false,
            phase: TcpLifecycle.Handshake,
            defaultFlags: TcpFlags.SynAck,
            payload: ReadOnlySpan<byte>.Empty,
            segmentIndex: 1,
            segmentCount: 3,
            sink: sink,
            mutator: effective);

        // After server SYN+ACK: client's expected ACK from server is ServerIsn + 1.
        _ClientSession.SetTcpStreamAck(_Options.ServerIsn + 1u);

        // ACK (client → server)
        _EmitOneSegment(
            isClient: true,
            phase: TcpLifecycle.Handshake,
            defaultFlags: TcpFlags.Ack,
            payload: ReadOnlySpan<byte>.Empty,
            segmentIndex: 2,
            segmentCount: 3,
            sink: sink,
            mutator: effective);

        _HandshakeDone = true;
    }

    /// <summary>
    /// Emits a graceful 4-way teardown: FIN+ACK (client), ACK (server),
    /// FIN+ACK (server), ACK (client).  Four segments, in wire order.
    /// </summary>
    public void EmitFinClose(FrameSink sink, TcpSegmentMutator? mutator = null)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        TcpSegmentMutator? effective = mutator ?? OnSegment;

        // FIN+ACK (client → server)
        _EmitOneSegment(true, TcpLifecycle.Fin, TcpFlags.FinAck, ReadOnlySpan<byte>.Empty, 0, 4, sink, effective);
        // After client FIN: server's expected ACK from client increments by 1 (FIN consumes a SEQ).
        _ServerSession.AdvanceTcpStreamAck(1u);

        // ACK (server → client)
        _EmitOneSegment(false, TcpLifecycle.Ack, TcpFlags.Ack, ReadOnlySpan<byte>.Empty, 1, 4, sink, effective);

        // FIN+ACK (server → client)
        _EmitOneSegment(false, TcpLifecycle.Fin, TcpFlags.FinAck, ReadOnlySpan<byte>.Empty, 2, 4, sink, effective);
        _ClientSession.AdvanceTcpStreamAck(1u);

        // ACK (client → server)
        _EmitOneSegment(true, TcpLifecycle.Ack, TcpFlags.Ack, ReadOnlySpan<byte>.Empty, 3, 4, sink, effective);
    }

    /// <summary>Emits a single RST segment from the client side.</summary>
    public void EmitRstFromClient(FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitRst(isClient: true, sink, mutator);

    /// <summary>Emits a single RST segment from the server side.</summary>
    public void EmitRstFromServer(FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitRst(isClient: false, sink, mutator);

    private void _EmitRst(bool isClient, FrameSink sink, TcpSegmentMutator? mutator)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        ArgumentNullException.ThrowIfNull(sink);
        _EmitOneSegment(isClient, TcpLifecycle.Rst, TcpFlags.Rst | TcpFlags.Ack, ReadOnlySpan<byte>.Empty,
            segmentIndex: 0, segmentCount: 1, sink, mutator ?? OnSegment);
    }

    #endregion

    #region Bare ACK / Window-Update helpers

    /// <summary>Emits a bare ACK segment from the client to the server (no payload, no flag changes).</summary>
    public void EmitAckFromClient(FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitBareAck(isClient: true, lifecycle: TcpLifecycle.Ack, window: null, sink, mutator);

    /// <summary>Emits a bare ACK segment from the server to the client (no payload, no flag changes).</summary>
    public void EmitAckFromServer(FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitBareAck(isClient: false, lifecycle: TcpLifecycle.Ack, window: null, sink, mutator);

    /// <summary>Emits a window-update segment from the client (bare ACK with a new advertised window).</summary>
    public void EmitWindowUpdateFromClient(ushort newWindow, FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitBareAck(isClient: true, lifecycle: TcpLifecycle.WindowUpdate, window: newWindow, sink, mutator);

    /// <summary>Emits a window-update segment from the server (bare ACK with a new advertised window).</summary>
    public void EmitWindowUpdateFromServer(ushort newWindow, FrameSink sink, TcpSegmentMutator? mutator = null)
        => _EmitBareAck(isClient: false, lifecycle: TcpLifecycle.WindowUpdate, window: newWindow, sink, mutator);

    private void _EmitBareAck(bool isClient, TcpLifecycle lifecycle, ushort? window, FrameSink sink, TcpSegmentMutator? mutator)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> session = isClient ? _ClientSession : _ServerSession;
        if (window.HasValue)
        {
            session.SetTcpStreamWindow(window.Value);
        }
        _EmitOneSegment(isClient, lifecycle, TcpFlags.Ack, ReadOnlySpan<byte>.Empty,
            segmentIndex: 0, segmentCount: 1, sink, mutator ?? OnSegment);
        if (window.HasValue)
        {
            // Restore default window so subsequent calls keep using the connection-wide value.
            session.SetTcpStreamWindow(_Options.WindowSize);
        }
    }

    #endregion

    #region Stream writes (raw bytes / IStreamProducer)

    /// <summary>Writes <paramref name="payload"/> as one or more client→server data segments.</summary>
    /// <param name="payload">Application bytes; sliced into MSS-sized segments.</param>
    /// <param name="sink">Per-segment delivery callback.</param>
    /// <param name="push">Set the PSH flag on the LAST segment (default <c>true</c>).</param>
    /// <param name="mss">Optional per-call MSS override (must be &gt; 0). Defaults to the connection MSS.</param>
    /// <param name="mutator">Optional per-call mutator override.</param>
    public void WriteFromClient(ReadOnlySpan<byte> payload, FrameSink sink, bool push = true, ushort? mss = null, TcpSegmentMutator? mutator = null)
        => _WriteStream(isClient: true, payload, sink, push, mss, mutator);

    /// <summary>Writes <paramref name="payload"/> as one or more server→client data segments.</summary>
    public void WriteFromServer(ReadOnlySpan<byte> payload, FrameSink sink, bool push = true, ushort? mss = null, TcpSegmentMutator? mutator = null)
        => _WriteStream(isClient: false, payload, sink, push, mss, mutator);

    /// <summary>Convenience overload: produces an <see cref="IStreamProducer"/>'s wire-form into a pooled buffer and Writes it.</summary>
    public void WriteFromClient<TProducer>(in TProducer producer, FrameSink sink, bool push = true, ushort? mss = null, TcpSegmentMutator? mutator = null)
        where TProducer : struct, IStreamProducer
        => _WriteFromProducer(isClient: true, in producer, sink, push, mss, mutator);

    /// <summary>Convenience overload: produces an <see cref="IStreamProducer"/>'s wire-form into a pooled buffer and Writes it.</summary>
    public void WriteFromServer<TProducer>(in TProducer producer, FrameSink sink, bool push = true, ushort? mss = null, TcpSegmentMutator? mutator = null)
        where TProducer : struct, IStreamProducer
        => _WriteFromProducer(isClient: false, in producer, sink, push, mss, mutator);

    private void _WriteFromProducer<TProducer>(bool isClient, in TProducer producer, FrameSink sink, bool push, ushort? mss, TcpSegmentMutator? mutator)
        where TProducer : struct, IStreamProducer
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        // Rent a thread-local buffer to avoid a per-call heap allocation.
        // Safe to reuse on the same thread: TcpConnection is not reentrant,
        // so the buffer is never accessed concurrently from within a single call chain.
        // If a previous call grew the buffer beyond 65536 bytes, reset to a fresh small
        // writer so the thread-static slot does not permanently retain a large allocation.
        ArrayBufferWriter<byte> writer = TcpConnection._SharedProducerBuffer ??= new ArrayBufferWriter<byte>(initialCapacity: 1024);
        if (writer.Capacity > 65536)
        {
            TcpConnection._SharedProducerBuffer = writer = new ArrayBufferWriter<byte>(initialCapacity: 1024);
        }
        writer.ResetWrittenCount();
        producer.WriteStream(writer);
        _WriteStream(isClient, writer.WrittenSpan, sink, push, mss, mutator);
    }

    private void _WriteStream(bool isClient, ReadOnlySpan<byte> payload, FrameSink sink, bool push, ushort? mss, TcpSegmentMutator? mutator)
    {
        ObjectDisposedException.ThrowIf(_Disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        ushort effectiveMss = mss ?? _Options.Mss;
        if (effectiveMss == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mss), "MSS must be > 0.");
        }

        TcpSegmentMutator? effective = mutator ?? OnSegment;

        if (payload.IsEmpty)
        {
            if (push)
            {
                // Empty Write with push:true → emit a single PSH+ACK with no payload.
                _EmitOneSegment(isClient, TcpLifecycle.Data, TcpFlags.PshAck,
                    ReadOnlySpan<byte>.Empty, 0, 1, sink, effective);
            }
            // push:false + empty payload → no segment at all.
            return;
        }

        // Determine number of segments produced by this call.
        int segmentCount = (payload.Length + effectiveMss - 1) / effectiveMss;
        int offset = 0;
        for (int i = 0; i < segmentCount; i++)
        {
            int sliceLength = Math.Min(effectiveMss, payload.Length - offset);
            ReadOnlySpan<byte> slice = payload.Slice(offset, sliceLength);
            bool isLast = i == segmentCount - 1;
            byte flags = (push && isLast) ? TcpFlags.PshAck : TcpFlags.Ack;
            _EmitOneSegment(isClient, TcpLifecycle.Data, flags, slice, i, segmentCount, sink, effective, effectiveMss);
            offset += sliceLength;
        }
    }

    #endregion

    #region Per-segment emission core

    /// <summary>
    /// The single point through which every emitted segment flows.
    /// Builds a <see cref="TcpSegmentDescriptor"/> from the connection's
    /// current state, optionally invokes the mutator, writes the
    /// (possibly mutated) per-frame state into the session's
    /// <see cref="SessionState"/> slots, runs the layer machinery into
    /// the scratch buffer, then forwards the wire bytes to the sink and
    /// updates the peer's expected ACK.
    /// </summary>
    private void _EmitOneSegment(
        bool isClient,
        TcpLifecycle phase,
        byte defaultFlags,
        ReadOnlySpan<byte> payload,
        int segmentIndex,
        int segmentCount,
        FrameSink sink,
        TcpSegmentMutator? mutator,
        ushort? overrideMss = null)
    {
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> session = isClient ? _ClientSession : _ServerSession;

        // Read the defaults the layer will use unless the mutator overrides.
        uint defaultSeq = session.PeekTcpStreamNextSeq();
        uint defaultAck = session.PeekTcpStreamAck();
        ushort defaultWindow = session.PeekTcpStreamWindow();

        uint seq;
        uint ack;
        byte flags;
        ushort window;
        ushort urgent;
        ReadOnlySpan<byte> finalPayload;

        if (mutator is null)
        {
            // Hot path: skip the descriptor-and-mutator dance entirely.
            seq = defaultSeq;
            ack = defaultAck;
            flags = defaultFlags;
            window = defaultWindow;
            urgent = 0;
            finalPayload = payload;
        }
        else
        {
            TcpSegmentDescriptor descriptor = default;
            descriptor.Sequence = defaultSeq;
            descriptor.Acknowledgment = defaultAck;
            descriptor.Flags = defaultFlags;
            descriptor.WindowSize = defaultWindow;
            descriptor.UrgentPointer = 0;
            descriptor.Payload = payload;

            TcpSegmentContext ctx = new()
            {
                Direction = isClient ? TcpDirection.ClientToServer : TcpDirection.ServerToClient,
                Phase = phase,
                SegmentIndex = segmentIndex,
                SegmentCount = segmentCount,
                Mss = overrideMss ?? _Options.Mss,
            };

            mutator(ref descriptor, in ctx);

            seq = descriptor.Sequence;
            ack = descriptor.Acknowledgment;
            flags = descriptor.Flags;
            window = descriptor.WindowSize;
            urgent = descriptor.UrgentPointer;
            finalPayload = descriptor.Payload;
        }

        // Stage the per-frame values into SessionState; the layer reads them in WriteHeader.
        session.PrepareTcpStreamFrame(seq, ack, flags, window, urgent);

        // Build the frame(s) in the scratch buffer.  Fragmentation is rare for TCP
        // (the TcpStreamLayer keeps segments within MSS), but the carrier can in principle
        // produce more than one frame (e.g. an IPv4 fragmenting layer above the TCP session).
        // Drain every frame so none are silently dropped.
        _EnsureScratch(payloadLength: finalPayload.Length);
        StatefulFrameSequence<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> sequence = session.NextPacket(finalPayload);
        bool hasFrame = false;
        while (sequence.MoveNext(_Scratch, out int bytesWritten))
        {
            hasFrame = true;

            // Hand the wire bytes to the sink.
            sink(_Scratch.AsSpan(0, bytesWritten));
        }

        if (!hasFrame)
        {
            throw new InvalidOperationException(
                $"TcpConnection segment build failed with status {sequence.Status}.");
        }

        // Propagate the SEQ delta into the peer's expected ACK:
        //   SYN: managed explicitly by EmitHandshake (SetTcpStreamAck) — skip here.
        //   FIN with no payload: managed explicitly by EmitFinClose (AdvanceTcpStreamAck(1));
        //     isFin && payload.Length == 0 keeps peerAdvance == 0 — skip here.
        //   Data or FIN+data (mutator-driven): auto-advance by payload.Length + (isFin ? 1 : 0)
        //     so the peer knows the full sequence space consumed by this segment.
        bool isSyn = (flags & TcpFlags.Syn) != 0;
        bool isFin = (flags & TcpFlags.Fin) != 0;
        uint peerAdvance = (uint)finalPayload.Length + (isFin && finalPayload.Length > 0 ? 1u : 0u);
        if (!isSyn && peerAdvance > 0)
        {
            Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> peer = isClient ? _ServerSession : _ClientSession;
            peer.AdvanceTcpStreamAck(peerAdvance);
        }
    }

    private void _EnsureScratch(int payloadLength)
    {
        // Exact frame size: carrier headers + TCP header (from HeaderSize) + payload.
        // No trailer: TcpConnection uses NoTrailer exclusively.
        int needed = HeaderSize + payloadLength;
        if (_Scratch.Length < needed)
        {
            _Scratch = new byte[needed];
        }
    }

    #endregion

    /// <summary>Disposes both underlying sessions; idempotent.</summary>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }
        _Disposed = true;
        _ClientSession.Dispose();
        _ServerSession.Dispose();
    }
}
