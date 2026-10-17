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
    /// <param name="options">
    /// Connection-level options (ISN per side, MSS, default window).  Omitted or
    /// <c>null</c> means <c>new TcpConnectionOptions()</c> with its documented
    /// defaults.  Explicit values are used as given: a zero window is emitted as 0.
    /// </param>
    /// <typeparam name="TOld">Outer-most carrier layer (must publish a pseudo-header).</typeparam>
    /// <typeparam name="TTail">Inner cons-list tail of the carrier stack.</typeparam>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="TcpConnectionOptions.Mss"/> is 0.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TcpConnection<TOld, TTail> Open<TOld, TTail>(
        in StatelessStack<TOld, TTail> clientCarrier,
        in StatelessStack<TOld, TTail> serverCarrier,
        ushort clientPort,
        ushort serverPort,
        TcpConnectionOptions? options = null)
        where TOld : struct, IStatelessLayer, IInteriorLayer, IProvidesPseudoHeader
        where TTail : struct, IStackNode, IStatelessStack
    {
        // MSS 0 cannot segment anything; reject it here as _WriteStream does for the
        // per-call override.  WindowSize 0 is a legal TCP state (zero window) and is
        // passed through unchanged.
        TcpConnectionOptions effectiveOptions = options ?? new TcpConnectionOptions();
        if (effectiveOptions.Mss == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "TcpConnectionOptions.Mss must be > 0.");
        }

        TcpStreamLayer clientLayer = new(clientPort, serverPort, effectiveOptions.ClientIsn, initialAck: 0u, effectiveOptions.WindowSize);
        TcpStreamLayer serverLayer = new(serverPort, clientPort, effectiveOptions.ServerIsn, initialAck: 0u, effectiveOptions.WindowSize);

        Stack<TcpStreamLayer, StatelessStack<TOld, TTail>> clientStack = clientCarrier.Then(clientLayer);
        Stack<TcpStreamLayer, StatelessStack<TOld, TTail>> serverStack = serverCarrier.Then(serverLayer);

        StatefulCreatedStack<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> clientCreated =
            StatefulFrameStack.CreateForSession(in clientStack);
        StatefulCreatedStack<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> serverCreated =
            StatefulFrameStack.CreateForSession(in serverStack);

        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> clientSession = clientCreated.OpenSession();
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> serverSession = serverCreated.OpenSession();

        return new TcpConnection<TOld, TTail>(
            clientSession, serverSession, effectiveOptions, clientPort, serverPort, clientCarrier, serverCarrier);
    }

    /// <summary>
    /// Largest producer-buffer capacity, in bytes, that a thread keeps between
    /// <see cref="TcpConnection{TOld,TTail}.WriteFromClient{TProducer}"/> /
    /// <c>WriteFromServer</c> calls.  Messages of any size still work; a buffer that
    /// grew beyond this cap is released after its write instead of being retained,
    /// so one large message does not pin memory on the thread for its lifetime.
    /// Constant (not configurable).
    /// </summary>
    internal const int MaxRetainedProducerBufferBytes = 64 * 1024;

    /// <summary>
    /// Thread-local <see cref="ArrayBufferWriter{T}"/> reused across producer writes
    /// on this thread.  Reuse relies on <see cref="TcpConnection{TOld,TTail}"/> not
    /// being reentrant on one thread.
    /// </summary>
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? _SharedProducerBuffer;

    /// <summary>Returns the calling thread's producer buffer, emptied and ready for one message.</summary>
    internal static ArrayBufferWriter<byte> RentProducerBuffer()
    {
        ArrayBufferWriter<byte> writer = _SharedProducerBuffer ??= new ArrayBufferWriter<byte>(initialCapacity: 1024);
        writer.ResetWrittenCount();
        return writer;
    }

    /// <summary>
    /// Ends the use of a buffer from <see cref="RentProducerBuffer"/>: a buffer that grew
    /// beyond <see cref="MaxRetainedProducerBufferBytes"/> is dropped for the GC.
    /// </summary>
    internal static void ReturnProducerBuffer(ArrayBufferWriter<byte> writer)
    {
        if (writer.Capacity > MaxRetainedProducerBufferBytes && ReferenceEquals(writer, _SharedProducerBuffer))
        {
            _SharedProducerBuffer = null;
        }
    }
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
/// produced frame, in wire order, before returning.  Segments are not
/// buffered across calls; the caller may stream arbitrarily large payloads
/// in incremental Write calls.  When the carrier fragments one segment into
/// several frames, those frames are built completely before the first one is
/// handed to the sink.
/// </para>
/// <para>
/// <b>Failure.</b>  A segment the carrier cannot build (for example a frame
/// budget below the segment size with DF set) throws
/// <see cref="InvalidOperationException"/> naming the <see cref="BuildStatus"/>.
/// No frame of that segment reaches the sink and the connection's sequence and
/// acknowledgement state is unchanged, so the caller may continue with a smaller
/// write.
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
    private readonly StatelessStack<TOld, TTail> _ClientCarrier;
    private readonly StatelessStack<TOld, TTail> _ServerCarrier;
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

    /// <summary>Second frame buffer, same size as <see cref="_Scratch"/>, for the fragments after the first.</summary>
    private byte[] _Fragment;

    /// <summary>
    /// Frames of one fragmented segment, held back until the whole segment built
    /// successfully so a late failure never leaves a partial segment in the sink.
    /// Allocated once; only a fragmenting carrier fills it.
    /// </summary>
    private readonly List<byte[]> _PendingFrames = [];

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
        ushort serverPort,
        StatelessStack<TOld, TTail> clientCarrier,
        StatelessStack<TOld, TTail> serverCarrier)
    {
        _ClientSession = clientSession;
        _ServerSession = serverSession;
        _ClientCarrier = clientCarrier;
        _ServerCarrier = serverCarrier;
        _Options = options;
        ClientPort = clientPort;
        ServerPort = serverPort;
        // Size the initial scratch buffer exactly: one full header + one MSS-worth of payload.
        // Both sessions must have the same stack shape (same carrier applied to both sides),
        // but take the max in case caller-supplied asymmetric carriers differ in header size.
        HeaderSize = Math.Max(clientSession.HeaderSize, serverSession.HeaderSize);
        _Scratch = new byte[HeaderSize + options.Mss];
        _Fragment = new byte[HeaderSize + options.Mss];
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

        // SYN and SYN-ACK with options take a separate serialiser (TcpLayerWithOptions)
        // but the same defaults, mutator and sequence bookkeeping as every other segment.
        if (_Options.ClientSynOptions.Data.Length > 0)
        {
            _EmitSynWithOptions(isClient: true, TcpFlags.Syn, _Options.ClientSynOptions, segmentIndex: 0, sink, effective);
        }
        else
        {
            _EmitOneSegment(
                isClient: true,
                phase: TcpLifecycle.Handshake,
                defaultFlags: TcpFlags.Syn,
                payload: ReadOnlySpan<byte>.Empty,
                segmentIndex: 0,
                segmentCount: 3,
                sink: sink,
                mutator: effective);
        }

        if (_Options.ServerSynOptions.Data.Length > 0)
        {
            _EmitSynWithOptions(isClient: false, TcpFlags.SynAck, _Options.ServerSynOptions, segmentIndex: 1, sink, effective);
        }
        else
        {
            _EmitOneSegment(
                isClient: false,
                phase: TcpLifecycle.Handshake,
                defaultFlags: TcpFlags.SynAck,
                payload: ReadOnlySpan<byte>.Empty,
                segmentIndex: 1,
                segmentCount: 3,
                sink: sink,
                mutator: effective);
        }

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

        // ACK (server → client)
        _EmitOneSegment(false, TcpLifecycle.Ack, TcpFlags.Ack, ReadOnlySpan<byte>.Empty, 1, 4, sink, effective);

        // FIN+ACK (server → client)
        _EmitOneSegment(false, TcpLifecycle.Fin, TcpFlags.FinAck, ReadOnlySpan<byte>.Empty, 2, 4, sink, effective);

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

        // The producer serialises the whole message into the thread's reusable buffer
        // (no per-call allocation up to MaxRetainedProducerBufferBytes); the buffer is
        // handed back even when the producer or the sink throws.
        ArrayBufferWriter<byte> writer = TcpConnection.RentProducerBuffer();
        try
        {
            producer.WriteStream(writer);
            _WriteStream(isClient, writer.WrittenSpan, sink, push, mss, mutator);
        }
        finally
        {
            TcpConnection.ReturnProducerBuffer(writer);
        }
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
    /// Fills the values of one segment: the session's current defaults first, then the
    /// mutator (when one is registered) may change any of them.  Every emission path
    /// goes through here, so the mutator contract holds for every segment, including
    /// SYN and SYN-ACK with options.
    /// </summary>
    private TcpSegmentDescriptor _ResolveSegment(
        bool isClient,
        TcpLifecycle phase,
        byte defaultFlags,
        ReadOnlySpan<byte> payload,
        int segmentIndex,
        int segmentCount,
        TcpSegmentMutator? mutator,
        ushort? overrideMss)
    {
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> session = isClient ? _ClientSession : _ServerSession;

        // Defaults the layer uses unless the mutator overrides them.
        TcpSegmentDescriptor descriptor = default;
        descriptor.Sequence = session.PeekTcpStreamNextSeq();
        descriptor.Acknowledgment = session.PeekTcpStreamAck();
        descriptor.Flags = defaultFlags;
        descriptor.WindowSize = session.PeekTcpStreamWindow();
        descriptor.UrgentPointer = 0;
        descriptor.Payload = payload;
        if (mutator is null)
        {
            // Hot path: no context, no delegate call.
            return descriptor;
        }

        TcpSegmentContext ctx = new()
        {
            Direction = isClient ? TcpDirection.ClientToServer : TcpDirection.ServerToClient,
            Phase = phase,
            SegmentIndex = segmentIndex,
            SegmentCount = segmentCount,
            Mss = overrideMss ?? _Options.Mss,
        };
        mutator(ref descriptor, in ctx);
        return descriptor;
    }

    /// <summary>
    /// Emits SYN or SYN-ACK with TCP options.  The stateful <see cref="TcpStreamLayer"/>
    /// writes a fixed 20-byte header, so this segment is serialised with a stateless
    /// <see cref="TcpLayerWithOptions"/> on the same carrier; defaults, mutator and the
    /// sequence bookkeeping are the same as in <see cref="_EmitOneSegment"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The carrier cannot build the segment (status in the message).</exception>
    private void _EmitSynWithOptions(
        bool isClient, byte defaultFlags, TcpOptions options, int segmentIndex, FrameSink sink, TcpSegmentMutator? mutator)
    {
        Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> session = isClient ? _ClientSession : _ServerSession;
        TcpSegmentDescriptor segment = _ResolveSegment(
            isClient, TcpLifecycle.Handshake, defaultFlags, ReadOnlySpan<byte>.Empty, segmentIndex, segmentCount: 3, mutator, overrideMss: null);

        // Build into a private buffer; nothing reaches the sink or the session state on failure.
        TcpLayerWithOptions layer = new(
            isClient ? ClientPort : ServerPort,
            isClient ? ServerPort : ClientPort,
            options,
            seqNum: segment.Sequence,
            ackNum: segment.Acknowledgment,
            flags: segment.Flags,
            windowSize: segment.WindowSize,
            urgentPointer: segment.UrgentPointer);
        CreatedStack<StatelessStack<TcpLayerWithOptions, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> created =
            (isClient ? _ClientCarrier : _ServerCarrier).Then(layer).CreateWithFixedValues();
        byte[] frame = new byte[created.HeaderSize + segment.Payload.Length];

        // Every fragment is at most as large as the whole frame; the shared fragment
        // buffer is sized for option-less segments and may be smaller than a SYN with options.
        byte[] fragmentBuffer = _Fragment.Length >= frame.Length ? _Fragment : new byte[frame.Length];
        _PendingFrames.Clear();
        int written;
        BuildStatus status;
        {
            using FrameSequence<StatelessStack<TcpLayerWithOptions, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> sequence =
                created.Build(segment.Payload);
            bool built = sequence.MoveNext(frame, out written);

            // A fragmenting carrier emits further frames (for example when a mutator gave
            // the SYN a payload): build them all before delivering any, as _EmitOneSegment does.
            if (built && sequence.MoveNext(fragmentBuffer, out int fragmentLength))
            {
                _PendingFrames.Add(frame.AsSpan(0, written).ToArray());
                _PendingFrames.Add(fragmentBuffer.AsSpan(0, fragmentLength).ToArray());
                while (sequence.MoveNext(fragmentBuffer, out fragmentLength))
                {
                    _PendingFrames.Add(fragmentBuffer.AsSpan(0, fragmentLength).ToArray());
                }
            }

            status = built ? sequence.Status : _FailureStatus(sequence.Status);
        }

        if (status != BuildStatus.Success)
        {
            _PendingFrames.Clear();
            throw new InvalidOperationException(Invariant($"TcpConnection SYN build failed with status {status}."));
        }

        if (_PendingFrames.Count == 0)
        {
            sink(frame.AsSpan(0, written));
        }
        else
        {
            foreach (byte[] pending in _PendingFrames)
            {
                sink(pending);
            }

            _PendingFrames.Clear();
        }

        // The stateless serialiser does not advance the stream slots: set the next
        // sequence (payload, plus one each for SYN and FIN) and the peer's ACK here.
        uint next = unchecked(segment.Sequence + _ConsumedSequenceSpace(segment.Flags, segment.Payload.Length));
        session.SetTcpStreamNextSeq(next);
        (isClient ? _ServerSession : _ClientSession).SetTcpStreamAck(next);
    }

    /// <summary>
    /// The single point through which every segment without SYN options flows: resolves
    /// the segment values, stages them into the session's <see cref="SessionState"/>
    /// slots, builds every frame of the segment, delivers them in wire order and updates
    /// the peer's expected ACK.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The carrier cannot build the segment (status in the message).  No frame of the
    /// segment reached the sink and the stream slots are unchanged.
    /// </exception>
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
        TcpSegmentDescriptor segment = _ResolveSegment(isClient, phase, defaultFlags, payload, segmentIndex, segmentCount, mutator, overrideMss);

        // Stage the per-frame values; the layer reads them in WriteHeader.  Keep the
        // previous slots so a failed segment can restore them.
        uint previousSeq = session.PeekTcpStreamNextSeq();
        uint previousAck = session.PeekTcpStreamAck();
        ushort previousWindow = session.PeekTcpStreamWindow();
        session.PrepareTcpStreamFrame(segment.Sequence, segment.Acknowledgment, segment.Flags, segment.WindowSize, segment.UrgentPointer);

        // Build first, deliver second: a failure on fragment N must not leave fragments
        // 0..N-1 in the sink.  A single-frame segment stays in _Scratch; only a
        // fragmenting carrier copies frames into _PendingFrames.
        _EnsureScratch(payloadLength: segment.Payload.Length);
        _PendingFrames.Clear();
        int firstLength;
        BuildStatus status;
        {
            using StatefulFrameSequence<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> sequence =
                session.NextPacket(segment.Payload);
            bool built = sequence.MoveNext(_Scratch, out firstLength);
            if (built && sequence.MoveNext(_Fragment, out int fragmentLength))
            {
                _PendingFrames.Add(_Scratch.AsSpan(0, firstLength).ToArray());
                _PendingFrames.Add(_Fragment.AsSpan(0, fragmentLength).ToArray());
                while (sequence.MoveNext(_Fragment, out fragmentLength))
                {
                    _PendingFrames.Add(_Fragment.AsSpan(0, fragmentLength).ToArray());
                }
            }

            status = built ? sequence.Status : _FailureStatus(sequence.Status);
        }

        if (status != BuildStatus.Success)
        {
            session.SetTcpStreamNextSeq(previousSeq);
            session.SetTcpStreamAck(previousAck);
            session.SetTcpStreamWindow(previousWindow);
            _PendingFrames.Clear();
            throw new InvalidOperationException(Invariant($"TcpConnection segment build failed with status {status}."));
        }

        // Deliver in wire order.
        if (_PendingFrames.Count == 0)
        {
            sink(_Scratch.AsSpan(0, firstLength));
        }
        else
        {
            foreach (byte[] frame in _PendingFrames)
            {
                sink(frame);
            }

            _PendingFrames.Clear();
        }

        // Peer ACK tracks the sender's next sequence after this segment.  An empty ACK
        // consumes no sequence space and does not move the peer.
        if (_ConsumedSequenceSpace(segment.Flags, segment.Payload.Length) > 0)
        {
            Session<Stack<TcpStreamLayer, StatelessStack<TOld, TTail>>, NoTrailer, NoInterceptor> peer =
                isClient ? _ServerSession : _ClientSession;
            peer.SetTcpStreamAck(session.PeekTcpStreamNextSeq());
        }
    }

    /// <summary>Sequence space a segment consumes: its payload, plus one each for SYN and FIN (RFC 9293 §3.4).</summary>
    private static uint _ConsumedSequenceSpace(byte flags, int payloadLength)
    {
        uint consumed = (uint)payloadLength;
        if ((flags & TcpFlags.Syn) != 0)
        {
            consumed++;
        }

        if ((flags & TcpFlags.Fin) != 0)
        {
            consumed++;
        }

        return consumed;
    }

    /// <summary>
    /// Status of a sequence whose first <c>MoveNext</c> returned <c>false</c>.  The engine
    /// always records a failure in that case; the fallback keeps the exception honest
    /// should that invariant ever break.
    /// </summary>
    private static BuildStatus _FailureStatus(BuildStatus status)
        => status == BuildStatus.Success
            ? BuildStatus.InvalidLayerState
            : status;

    /// <summary>
    /// Grows both frame buffers to <c>HeaderSize + payloadLength</c>.  That bound covers
    /// every frame: TcpConnection uses <see cref="NoTrailer"/>, a single frame is exactly
    /// that size and every fragment of a fragmenting carrier is smaller.
    /// </summary>
    private void _EnsureScratch(int payloadLength)
    {
        int needed = HeaderSize + payloadLength;
        if (_Scratch.Length < needed)
        {
            _Scratch = new byte[needed];
            _Fragment = new byte[needed];
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
