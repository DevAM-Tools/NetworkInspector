// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Represents a parsed packet with a flat field tree.
/// Fields are stored in an embedded <see cref="FieldStorage"/> with <see cref="ushort"/> linked-list indices:
/// a first chunk of <see cref="FieldStorage.FirstChunkSize"/> <see cref="FieldBody"/> slots owned by the packet, then
/// follow-up chunks of <see cref="FieldStorage.ChunkSize"/> slots for larger trees. A recycle keeps the chunks within
/// the retention policy of <see cref="FieldStorage.ResetForReuse"/>, so recycled packets stop allocating field storage.
/// <para>
/// All per-packet arrays (FieldBody chunks, lazy populator delegates) are
/// allocated from thread-local <see cref="SlabAllocator{T}"/> instances — a generic slab
/// allocator. Multiple packets share the same backing array; when a slab is full, a new
/// one is created and the old slab stays alive as long as any packet references its buffer.
/// Sharing one array across many packets replaces one small heap array per packet and per chunk, which keeps the number of
/// objects the GC must trace and reclaim low when a trace holds millions of packets.
/// </para>
/// <para>
/// <b>Recycling:</b> For high-throughput loops over packets owned by one thread (initial trace
/// scans, profiling, per-listener re-parsing), use the <c>TryParse(Packet recycle, …)</c>
/// overloads to reuse an existing, sealed Packet object instead of allocating a new one. The
/// internal slab storage is cleared and reused in place, eliminating the heap allocation and its
/// associated GC pressure entirely. The <see cref="PrepareForReuse"/> method performs this reset. The recycle packet may
/// belong to another stack of the same <see cref="FrameInterfaceRegistry"/> (a restart replaces the stack but keeps the
/// registry): it is rebound to the stack of the new parse, which saves one allocation per buffer slot per restart.
/// A recycle target belongs exclusively to the thread that passes it in — never recycle a packet
/// another thread might still be reading. Holding a <see cref="Field"/> across
/// <c>TryParse(recycle)</c> is unsupported; <see cref="Field"/> storage indexes refer to
/// the new parse after a successful recycle.
/// </para>
/// <para>
/// <b>Skip-field-tree:</b> Pass <see cref="FieldTreeMode.Skip"/> as the last argument of a parse
/// factory to omit FieldBody slab writes. The sealed packet is throwaway: do not publish, filter,
/// or pass it to <see cref="ValueCache.RecordPacket"/>. Packet index and ValueCache record still run.
/// Default parse behavior is <see cref="FieldTreeMode.Build"/>.
/// </para>
/// <para>
/// <b>Thread-safety contract:</b> After <see cref="Seal"/> completes, a finalized packet is safe
/// for concurrent reads from multiple threads. Lazy field materialization uses per-field
/// CAS on <see cref="FieldBody.LazyIndex"/> (materializing marker bit) so unrelated lazy
/// branches can populate concurrently after <see cref="Seal"/>.
/// All cross-thread visibility is ensured via <see cref="Volatile"/> reads/writes — no locks.
/// <see cref="FieldBody"/> tree links are published with <see cref="Volatile"/> load/store.
/// Additional buffers freeze at <see cref="Seal"/>. Recycle is exclusive.
/// </para>
/// <para>
/// <b>Parse concurrency:</b> The single-threaded statements throughout this type are per packet
/// <i>instance</i>: one packet is parsed by exactly one thread up to its <see cref="Seal"/>.
/// Different packets may be parsed on different threads at the same time. Whether that is safe is
/// decided by the protocols on the stack, not here: a protocol that carries state across packets
/// requires the <i>first</i> parse of each packet id to be ordered, single-threaded, and to use
/// dense ids <c>0, 1, 2, …</c> (a jump returns <see cref="ParseFailure.ParseIdGap"/>). It then
/// detects and replays any later parse of an already-parsed id lock-free (see
/// <c>PROTOCOL_GUIDE.md</c>, "First parse vs. re-parse"). Consequently
/// <see cref="TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/> called with an id that this stack has already
/// first-parsed is a re-parse and is safe from any thread, while calling it with a fresh id from an
/// arbitrary thread is not.
/// </para>
/// </summary>
public sealed class Packet
{
    #region Shared state and construction

    /// <summary>
    /// Limit of fields per packet. Format fact, so a constant and not a setting: field links are <see cref="ushort"/> storage
    /// indices and 0xFFFF (<c>NullIndex</c>) and 0xFFFE (<c>SkipStorageIndex</c>) are reserved sentinels, so a count of 0xFFFE
    /// keeps the highest slot index at 0xFFFD. Exceeding it is an expected failure that is reported as a <c>packet.error</c>
    /// field, never silently dropped.
    /// </summary>
    private const int _MaxFieldCount = ushort.MaxValue - 1;

    /// <summary>
    /// Field limit for every append except a recorded caught exception. The last slot below
    /// <see cref="_MaxFieldCount"/> stays free for <see cref="RecordCaughtError"/>, so a parse that fills the
    /// tree still records the resulting <c>FieldAppendException</c> instead of throwing out of <c>TryParse</c>.
    /// </summary>
    private const int _RegularFieldLimit = _MaxFieldCount - 1;

    /// <summary>
    /// Field slots: an embedded struct, so a field access needs no extra object hop. Build mode only; a Skip-only packet
    /// never initializes it.
    /// </summary>
    private FieldStorage _Fields;

    /// <summary>
    /// <see langword="true"/> when the parse builds a field tree (<see cref="FieldTreeMode.Build"/>); <see langword="false"/>
    /// in Skip mode, which omits the field slab. Not volatile: one thread parses a given instance, and a recycle needs
    /// exclusive access.
    /// </summary>
    private bool _HasFieldTree = true;

    /// <summary>
    /// Number of fields a reader may see. Range [0, <see cref="_MaxFieldCount"/>]. Published (volatile store) only after the
    /// parent and sibling links of the new field are written, so a reader that sees a count also sees consistent links. Uses
    /// <see cref="int"/> because <see cref="ushort"/> has no <see cref="Volatile"/> overload. Readers are
    /// <see cref="FieldCount"/>, <see cref="TryGetFieldAt"/>, and the enumerators.
    /// </summary>
    private volatile int _FieldCount;

    /// <summary>
    /// Reservation watermark: unique <see cref="FieldBody"/> slot indexes for concurrent post-Seal appends. Distinct from
    /// <see cref="_FieldCount"/> so the interlocked increment does not publish a slot to readers before its parent and sibling
    /// links are written. Always at least <see cref="_FieldCount"/>.
    /// </summary>
    private volatile int _AllocatedFieldCount;

    /// <summary>
    /// 0 = not sealed, 1 = sealed. Written with a release store by <see cref="Seal"/>; <see cref="PrepareForReuse"/> stores 0
    /// after it holds <see cref="_RecycleGate"/>.
    /// </summary>
    private volatile int _Finalized;

    /// <summary>
    /// 0 = idle and readable, 1 = recycle in progress. Makes a recycle exclusive against a concurrent
    /// <see cref="MaterializeAll"/> and <see cref="MaterializeLazyField"/>.
    /// </summary>
    private volatile int _RecycleGate;

    /// <summary>
    /// Number of packet-level errors of this packet: dissector errors, exceptions caught at a parse, dispatch, observer, or
    /// materialization boundary. Saturates at <see cref="int.MaxValue"/>. Raised with an interlocked increment because a lazy
    /// field that materializes after Seal records its error from a reader thread; reset only by an exclusive recycle.
    /// </summary>
    private int _ErrorCount;

    /// <summary>
    /// Creates a packet from a captured frame.
    /// <see cref="FrameSourceId"/> is derived from the frame's <see cref="FrameInterfaceId"/>
    /// via the stack's <see cref="FrameInterfaceRegistry"/>.
    /// The frame's registry must match the stack's registry
    /// (validated via reference equality for maximum efficiency on the hot path).
    /// </summary>
    /// <param name="id">The id under which the stack counts this parse (see <see cref="Id"/>).</param>
    /// <param name="stack">The protocol stack that owns the packet; its frame-interface registry must be the frame's registry.</param>
    /// <param name="frame">The captured frame to parse.</param>
    /// <param name="fieldTree">Whether the parse builds a field tree; any other value than the two defined modes throws.</param>
    /// <exception cref="ArgumentException">The frame's registry is not the stack's registry.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldTree"/> is not a defined <see cref="FieldTreeMode"/>.</exception>
    internal Packet(PacketId id, Stack stack, Frame frame, FieldTreeMode fieldTree = FieldTreeMode.Build)
    {
        // Validate registry consistency: reference equality — single pointer comparison
        if (!ReferenceEquals(frame.Registry, stack.FrameInterfaceRegistry))
        {
            _ThrowRegistryMismatch();
        }

        _ThrowIfInvalidFieldTree(fieldTree);
        Id = id;
        Timestamp = frame.Timestamp;
        Stack = stack;
        Frame = frame;
        FrameSourceId = _DeriveFrameSourceId(frame.InterfaceId, stack.FrameInterfaceRegistry);
        _HasFieldTree = fieldTree == FieldTreeMode.Build;
        if (_HasFieldTree)
        {
            _Fields.Initialize();
            _Fields.Slot(0) = new FieldBody(stack.RootFieldId);
        }
        else
        {
            // _Fields stays uninitialized: Skip GetFieldRef / skip append must not read it.
            _SkipRootBody = new FieldBody(stack.RootFieldId);
        }

        _AllocatedFieldCount = 1;
        _FieldCount = 1;
        InfoLazy = LazyString.Empty;
        _InfoFieldIndex = FieldBody.NullIndex;
    }

    /// <summary>
    /// Constructor guard for an undefined <see cref="FieldTreeMode"/> value. <c>TryParse</c> reports the same condition
    /// as <see cref="ParseFailure.InvalidFieldTree"/> before it ever reaches the constructor, so this throw only fires
    /// for an internal caller that bypassed it.
    /// </summary>
    /// <param name="fieldTree">The mode the caller passed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fieldTree"/> is neither Build nor Skip.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ThrowIfInvalidFieldTree(FieldTreeMode fieldTree)
    {
        if (fieldTree is not FieldTreeMode.Build and not FieldTreeMode.Skip)
        {
            ThrowHelpers.ThrowInvalidFieldTreeMode(fieldTree);
        }
    }

    /// <summary>
    /// Derives the <see cref="FrameSourceId"/> from a frame's interface ID via the registry.
    /// O(1) array lookup — called once per packet in the constructor and once per recycle.
    /// </summary>
    /// <param name="interfaceId">The interface id of the frame; may be invalid.</param>
    /// <param name="registry">The registry that owns the interface ids of the frame.</param>
    /// <returns>The source id of the interface; <see cref="FrameSourceId.Invalid"/> when the id is invalid or unregistered.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FrameSourceId _DeriveFrameSourceId(
        FrameInterfaceId interfaceId, FrameInterfaceRegistry registry)
    {
        if (!interfaceId.IsValid)
        {
            return FrameSourceId.Invalid;
        }

        FrameInterfaceInfo? info = registry.Get(interfaceId);
        if (info is not null)
        {
            return info.SourceId;
        }
        return FrameSourceId.Invalid;
    }

    /// <summary>Throws when a frame's registry does not match the stack's registry.</summary>
    /// <exception cref="ArgumentException">Always thrown.</exception>
    [DoesNotReturn]
    private static void _ThrowRegistryMismatch()
    {
        throw new ArgumentException(
            "The frame's FrameInterfaceRegistry does not match the stack's registry. " +
            "Frame and stack must share the same FrameInterfaceRegistry instance.");
    }

    #endregion

    #region Recycle

    /// <summary>
    /// Validates preconditions and resets this packet for reuse with a new frame,
    /// eliminating the heap allocation of <c>new Packet(...)</c> on the hot path.
    /// Existing slab-backed storage (FieldBody chunks and lazy populator slots) is cleared and reused in
    /// place within the retention policy of <see cref="FieldStorage.ResetForReuse"/>, so a recycled packet
    /// that keeps parsing similar frames stops allocating field storage.
    /// <para>
    /// Returns a <see cref="RecycleError"/> code instead of throwing so that the internal
    /// path stays free of exception construction overhead. Callers are responsible for
    /// translating a non-<see langword="null"/> result into an appropriate exception.
    /// </para>
    /// <para>
    /// <b>Thread-safety contract:</b> The caller must ensure exclusive access — no concurrent
    /// readers may hold <see cref="Field"/> or <see cref="MutField"/> references into this
    /// packet while <see cref="PrepareForReuse"/> is executing. The packet must be finalized
    /// (<see cref="IsFinalized"/> == <see langword="true"/>) and no concurrent materialization
    /// must be in progress. Call from a single thread only.
    /// </para>
    /// <para>
    /// <b>Rebinding to another stack:</b> the packet is bound to <paramref name="stack"/>, which may differ from its current
    /// <see cref="Stack"/> when both share the same <see cref="FrameInterfaceRegistry"/>. A session restart replaces the stack
    /// but keeps the registry, and a buffer of recycled packets would otherwise cost one allocation per slot per restart. This
    /// is safe because everything in a packet that is specific to a stack is reset here anyway: the field slots (their
    /// <see cref="FieldId"/>s name fields of the old stack), the root slot (re-seeded with the new stack's root id), the lazy
    /// populators (closures over the old protocol instances), the additional buffers, the first-protocol override and the
    /// active value cache. What stays is the field storage layout (chunks of <c>FieldBody</c>), which is stack-independent.
    /// A stack of another registry is refused (<see cref="RecycleError.StackMismatch"/>): its interface and source ids would
    /// not match the frame. A refused call leaves the packet exactly as it was.
    /// </para>
    /// </summary>
    /// <param name="id">New packet identifier.</param>
    /// <param name="stack">
    /// The stack of the next parse; must use the same <see cref="FrameInterfaceRegistry"/> as the packet's current stack.
    /// </param>
    /// <param name="frame">New frame to parse. Must share the <see cref="FrameInterfaceRegistry"/> of <paramref name="stack"/>.</param>
    /// <param name="fieldTree">Whether the next parse writes FieldBodies. Skip leaves the slab unused.</param>
    /// <returns>
    /// <see langword="null"/> on success; a <see cref="RecycleError"/> value when a
    /// precondition is violated (not finalized, materializer active, registry mismatch, or a stack of another registry).
    /// </returns>
    internal RecycleError? PrepareForReuse(PacketId id, Stack stack, Frame frame, FieldTreeMode fieldTree)
    {
        // Exclusive recycle vs concurrent MaterializeAll: take the gate first so a materializer
        // that observes the gate cannot claim a field while Array.Clear runs.
        // MaterializeAll and MaterializeLazyField increment _ActiveLazyMaterializations before
        // they re-read the gate. Recycle never plain-stores that counter to 0.
        // On success the gate intentionally stays at 1 until Seal clears it at the end of the re-parse, so a second
        // recycle of the same packet fails with MaterializerActive instead of corrupting the parse in progress.
        // Every failure path below must release it again.
        if (Interlocked.CompareExchange(ref _RecycleGate, 1, 0) != 0)
        {
            return RecycleError.MaterializerActive;
        }

        if (_ActiveLazyMaterializations != 0 || _Finalized == 0)
        {
            RecycleError err = _Finalized == 0
                ? RecycleError.NotFinalized
                : RecycleError.MaterializerActive;
            if (_ActiveLazyMaterializations != 0)
            {
                // Hold the gate briefly so other MaterializeLazyField calls observe it and abort.
                // Do not Array.Clear: Active is non-zero.
                SpinWait gateHold = default;
                for (int i = 0; i < 8; i++)
                {
                    gateHold.SpinOnce();
                }
            }

            _RecycleGate = 0;
            return err;
        }

        // Registry checks against the NEW stack, before anything is reset: a refused rebind leaves the packet exactly as it
        // was (same id, same stack, same fields). The common case, the packet's own stack (every ingest and read loop of a
        // session between two restarts), costs one reference compare and writes nothing: the registry comparison and the
        // rebind below are only reached when the stack really changed.
        bool rebind = !ReferenceEquals(stack, Stack);
        if (rebind && !ReferenceEquals(stack.FrameInterfaceRegistry, Stack.FrameInterfaceRegistry))
        {
            // A stack of another registry cannot be adopted: its interface and source ids would not match the frame.
            _RecycleGate = 0;
            return RecycleError.StackMismatch;
        }

        // Then the frame against that stack (same check as the constructor).
        if (!ReferenceEquals(frame.Registry, stack.FrameInterfaceRegistry))
        {
            _RecycleGate = 0;
            return RecycleError.RegistryMismatch;
        }

        // Rebind BEFORE the first use of Stack below (RootFieldId for slot 0, FrameInterfaceRegistry for the source id): slot 0
        // must carry the new stack's root id, and every Stack read from here on is by inspection the new stack. An old stack
        // read after this line would put the old root field id into slot 0 and show the old field names in the tree.
        // Rejected: leaving Stack alone and allocating a fresh packet in the session (one allocation per slot per restart), and
        // storing the stack unconditionally (a reference store costs a GC write barrier on every recycled parse for no change).
        // Residual risk: a stack-specific cache added to Packet later must be reset above, or a rebound packet would carry the
        // old stack's state; the field layout (chunks of FieldBody) is the only thing deliberately kept.
        if (rebind)
        {
            Stack = stack;
        }

        // FieldTreeMode is validated by TryParse before this method; constructor still throws.

        // Step 1, clear used slots; FieldStorage keeps chunks within its retention policy.
        // Array.Clear zeroes entire FieldBody structs (FieldValue and LazyString references included) so the GC does
        // not retain stale references, and only the used slots are touched. Skip packets that never built a tree have
        // no storage. After a Skip parse _AllocatedFieldCount is 1: the Build slots were cleared by the recycle that
        // switched to Skip.
        if (_Fields.IsInitialized)
        {
            _Fields.ResetForReuse(_AllocatedFieldCount);
        }

        // Step 2, clear lazy populator references.
        // Unconsumed populators are closures over the previous frame's parse state; clearing them lets the GC reclaim that
        // state. The table itself (a slab slice) is kept: _EnsureLazyCapacity reuses it while its capacity suffices.
        if (_LazyTable is not null && _LazyPopulatorCount > 0)
        {
            LazyPopulatorTable lazyTable = _LazyTable;
            Array.Clear(lazyTable.Buffer, lazyTable.Offset, _LazyPopulatorCount);
        }

        _LazyPopulatorCount = 0;
        _AllocatedLazyCount = 0;
        _PendingLazyCount = 0;

        // Step 3, drop additional buffers and skip scratch; reset the field tree root.
        // Additional buffers hold reassembled bytes of the previous parse. A reparse of the same packet binds them again.
        if (_AdditionalBufferCount > 0 && _AdditionalBuffers is not null)
        {
            Array.Clear(_AdditionalBuffers, 0, _AdditionalBufferCount);
            _AdditionalBufferCount = 0;
        }

        // The byte counter describes the buffers just dropped, so a reparse of the same packet starts at 0 and a protocol that
        // applies its own total reaches the same decisions as the first parse (they depend only on the bound bytes and their order).
        _AdditionalBufferBytes = 0;

        _HasFieldTree = fieldTree == FieldTreeMode.Build;
        if (_SkipScratchDepth > 0 && _SkipScratchStack is not null)
        {
            Array.Clear(_SkipScratchStack, 0, _SkipScratchDepth);
            _SkipScratchDepth = 0;
        }

        _SkipRootBody = default;
        if (_HasFieldTree)
        {
            if (!_Fields.IsInitialized)
            {
                _Fields.Initialize();
            }

            _Fields.Slot(0) = new FieldBody(Stack.RootFieldId);
        }
        else
        {
            // Leave _Fields as it is (uninitialized on never-Build; allocated-but-unused on Build→Skip).
            _SkipRootBody = new FieldBody(Stack.RootFieldId);
        }

        _AllocatedFieldCount = 1;
        _FieldCount = 1;
        _Finalized = 0;
        _ErrorCount = 0;
        _LazyDepthReported = 0;
        InfoLazy = LazyString.Empty;
        _InfoFieldIndex = FieldBody.NullIndex;
        _ActiveValueCache = null;
        FirstProtocolOverride = ProtocolId.Invalid;
        Id = id;
        Timestamp = frame.Timestamp;
        Frame = frame;
        FrameSourceId = _DeriveFrameSourceId(frame.InterfaceId, Stack.FrameInterfaceRegistry);
        return null;
    }

    #endregion

    #region Identity and status

    // Id, Timestamp, Frame and FrameSourceId have a private setter because PrepareForReuse replaces them.
    // Stack is rebound only by PrepareForReuse, and only onto a stack of the same frame registry (see there).

    /// <summary>
    /// The id under which the stack counted this parse; dense per stack, starting at 0, reused by a recycle.
    /// </summary>
    public PacketId Id { get; private set; }

    /// <summary>
    /// The capture time of the frame, copied from <see cref="Frame"/> when the packet was parsed or recycled, so a reader
    /// does not need to touch the frame.
    /// </summary>
    public Timestamp Timestamp { get; private set; }

    /// <summary>
    /// The protocol stack that owns this packet. Rebound by a recycling parse onto a stack of the same frame registry (a
    /// restart of a session replaces the stack); never changes while the packet is read, because a recycle needs exclusive
    /// access to the packet.
    /// </summary>
    public Stack Stack { get; private set; }

    /// <summary>
    /// The captured frame this packet was parsed from. Its bytes are buffer 0 of the packet (see <see cref="Buffer(int)"/>).
    /// Replaced by a recycle.
    /// </summary>
    public Frame Frame { get; private set; }

    /// <summary>
    /// The source of the frame's capture interface, derived through the stack's <see cref="FrameInterfaceRegistry"/> when
    /// the packet was parsed or recycled. <see cref="FrameSourceId.Invalid"/> when the frame has no valid interface id or the
    /// registry does not know it.
    /// </summary>
    public FrameSourceId FrameSourceId { get; private set; }

    /// <summary>
    /// Whether the parse of this packet is finished and sealed. A sealed packet is safe for concurrent reads; a recycle
    /// resets it to <see langword="false"/> until the next parse seals it.
    /// </summary>
    public bool IsFinalized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Finalized != 0;
    }

    /// <summary>False when this packet was parsed with <see cref="FieldTreeMode.Skip"/>.</summary>
    public bool HasFieldTree => _HasFieldTree;

    /// <summary>
    /// How many packet-level errors the parse (and any later lazy materialization) of this packet recorded. Counts also when
    /// the parse kept no field tree (<see cref="FieldTreeMode.Skip"/>) or the stack has no <c>packet.error</c> field, where the
    /// error text itself is not stored. Zero means no dissector, observer, or populator failed. Thread-safe (one volatile read).
    /// </summary>
    public int ErrorCount => Volatile.Read(ref _ErrorCount);

    /// <summary>
    /// Protocol override for dispatch. When valid, PacketProtocol dispatches here
    /// instead of to <see cref="Stack.FrameProtocolId"/>.
    /// </summary>
    internal ProtocolId FirstProtocolOverride { get; set; } = ProtocolId.Invalid;

    #endregion

    #region Field slot access and skip storage

    /// <summary>Synthetic root slot of a Skip parse, which omits the field storage. Unused on Build.</summary>
    private FieldBody _SkipRootBody;

    /// <summary>
    /// Scratch stack of throwaway container bodies for skip-tree lazy populators that must read <c>container.Value</c>.
    /// Allocated on first use and recycled in place.
    /// </summary>
    private FieldBody[]? _SkipScratchStack;

    /// <summary>Number of live entries of <see cref="_SkipScratchStack"/>; 0 outside a skip-mode populator.</summary>
    private int _SkipScratchDepth;

    /// <summary>
    /// Returns the number of fields currently materialized in the tree.
    /// When <paramref name="materialize"/> is <see langword="true"/>, all pending lazy fields
    /// are materialized first so the returned value reflects the complete field count.
    /// </summary>
    /// <param name="materialize">
    /// <see langword="true"/> to populate all pending lazy fields first (waits for populators running on other threads after
    /// the seal); <see langword="false"/> to count only the fields linked so far.
    /// </param>
    /// <returns>The reader-visible field count, including the root; at least 1.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FieldCount(bool materialize)
    {
        if (materialize)
        {
            MaterializeAll();
        }
        // `_FieldCount` is volatile, so every read is already a volatile read.
        // Seal publishes the pre-Seal bulk parse; post-Seal appends publish via `_PublishFieldCount`.
        return _FieldCount;
    }

    /// <summary>
    /// Returns a mutable reference to the field body at the given storage index. Build mode reads
    /// <see cref="FieldStorage.Slot"/> (first or follow-up chunk); Skip mode routes the root and the
    /// skip index to <see cref="_GetSkipBody"/>. All stored <see cref="ushort"/> indices in <see cref="FieldBody"/>
    /// are storage indices.
    /// </summary>
    /// <param name="index">Storage index of the slot; must be below the allocated count (not checked in Build mode).</param>
    /// <returns>A reference to the slot; stays valid because chunks never move.</returns>
    /// <exception cref="InvalidOperationException">Skip mode and <paramref name="index"/> is neither the root nor the skip index.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref FieldBody GetFieldRef(int index)
    {
        if (!_HasFieldTree)
        {
            return ref _GetSkipBody(index);
        }

        return ref _Fields.Slot(index);
    }

    /// <summary>
    /// Skip-mode slot: index 0 is the synthetic root, <see cref="FieldBody.SkipStorageIndex"/> the top of the lazy
    /// scratch stack; any other index (or the skip index without scratch) throws. Kept out of line so the Build path
    /// of <see cref="GetFieldRef"/> stays small.
    /// </summary>
    /// <param name="index">0 for the synthetic root, or <see cref="FieldBody.SkipStorageIndex"/> for the scratch top.</param>
    /// <returns>A reference to the root body or to the top scratch body.</returns>
    /// <exception cref="InvalidOperationException">Any other index, or the skip index while no scratch body is pushed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref FieldBody _GetSkipBody(int index)
    {
        if (index == FieldBody.SkipStorageIndex && _SkipScratchDepth > 0)
        {
            FieldBody[] scratch = _SkipScratchStack!; // Invariant: _SkipScratchDepth > 0 only after _PushSkipScratch allocated the stack.
            return ref scratch[_SkipScratchDepth - 1];
        }

        if (index != 0)
        {
            ThrowHelpers.ThrowSkipFieldTreeValue();
        }

        return ref _SkipRootBody;
    }

    /// <summary>
    /// Pushes a throwaway container body for a skip-mode lazy populator (see <see cref="RegisterLazyPopulator"/>). The
    /// stack array is allocated on first use and then recycled in place, so a skip parse allocates only for nesting
    /// deeper than before. Parse-thread only.
    /// </summary>
    /// <param name="body">The container body the populator reads through <see cref="FieldBody.SkipStorageIndex"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PushSkipScratch(FieldBody body)
    {
        if (_SkipScratchStack is null)
        {
            _SkipScratchStack = new FieldBody[4];
        }
        else if (_SkipScratchDepth >= _SkipScratchStack.Length)
        {
            Array.Resize(ref _SkipScratchStack, _SkipScratchStack.Length * 2);
        }

        _SkipScratchStack[_SkipScratchDepth] = body;
        _SkipScratchDepth++;
    }

    /// <summary>
    /// Pops the top skip scratch body and clears its slot so the value and text references are not retained.
    /// Parse-thread only; each call pairs with one <see cref="_PushSkipScratch"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PopSkipScratch()
    {
        _SkipScratchDepth--;
        Array.Clear(_SkipScratchStack!, _SkipScratchDepth, 1); // Invariant: a pop follows a push, which allocated the stack.
    }

    /// <summary>
    /// Spins until <paramref name="index"/> is covered by the reader-visible <see cref="_FieldCount"/>
    /// so a tree walker that observed a volatile child/sibling pointer cannot read a default
    /// <see cref="FieldBody"/> before the matching <see cref="_PublishFieldCount"/>.
    /// </summary>
    /// <param name="index">A storage index read from a link of another field, or <see cref="FieldBody.NullIndex"/> for none.</param>
    /// <returns><paramref name="index"/>, once it is published (immediately for the null index).</returns>
    /// <remarks>
    /// Cost: one comparison when the index is published already. The wait is bounded by the writer between its link stores and
    /// its publish (a few stores), so the spin is short; an insert that throws in between publishes a tombstone, so it ends.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal ushort WaitUntilFieldPublished(ushort index)
    {
        if (index == FieldBody.NullIndex || index < _FieldCount)
        {
            return index;
        }

        return _WaitUntilFieldPublishedSlow(index);
    }

    /// <summary>
    /// Spins until <paramref name="index"/> is covered by <see cref="_FieldCount"/>.
    /// Slow path of <see cref="WaitUntilFieldPublished"/>; not inlined so coverage and the
    /// wait stay on this method.
    /// </summary>
    /// <param name="index">A storage index that is not yet below <see cref="_FieldCount"/>.</param>
    /// <returns><paramref name="index"/>, after it became published.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ushort _WaitUntilFieldPublishedSlow(ushort index)
    {
        SpinWait spin = default;
        while (index >= _FieldCount)
        {
            spin.SpinOnce();
        }

        return index;
    }

    #endregion

    #region Field cursors

    /// <summary>Gets the root of the field tree. Navigate from here with Field parent/child/sibling APIs or iterators.</summary>
    /// <returns>A read cursor on the root field (storage index 0); also valid on a Skip parse, where only the root exists.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Field RootField() => new(this, 0);

    /// <summary>
    /// Tries to wrap the field body at a storage index. Returns false if out of range.
    /// Storage indexes are packet-owned; this is not a public navigation API.
    /// External callers use <see cref="RootField"/>, Field tree navigation, or
    /// <see cref="IterFieldsDfs"/> / <see cref="IterFieldsFlat"/>.
    /// </summary>
    /// <param name="index">Storage index of the field.</param>
    /// <param name="field">The read cursor on success; <see langword="default"/> when the index is not published.</param>
    /// <returns><see langword="true"/> when <paramref name="index"/> is below the reader-visible field count.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetFieldAt(ushort index, out Field field)
    {
        if (index >= _FieldCount)
        {
            field = default;
            return false;
        }
        field = new Field(this, index);
        return true;
    }

    /// <summary>Gets a mutable field reference to the root field for protocol parsing.</summary>
    /// <returns>A write cursor on the root field (storage index 0).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutField RootFieldMut() => new(this, 0, GetFieldRef(0).FieldId);

    /// <summary>Tries to get a mutable field reference by index. Returns false if out of range.</summary>
    /// <param name="index">Storage index of the field.</param>
    /// <param name="field">The write cursor on success; <see langword="default"/> when the index is not published.</param>
    /// <returns><see langword="true"/> when <paramref name="index"/> is below the reader-visible field count.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetFieldMutAt(ushort index, out MutField field)
    {
        if (index >= _FieldCount)
        {
            field = default;
            return false;
        }
        field = new MutField(this, index, GetFieldRef(index).FieldId);
        return true;
    }

    #endregion

    #region Buffers and effect layer keys

    /// <summary>
    /// Table of the additional buffers (reassembled or decoded bytes), allocated on the first bound buffer. Entries
    /// [0, <see cref="_AdditionalBufferCount"/>) are valid; written by one thread until <see cref="Seal"/>, frozen afterwards.
    /// </summary>
    private ReadOnlyMemory<byte>[]? _AdditionalBuffers;

    /// <summary>Number of stored additional buffers, at most 255 (the effect-layer key packs the index in 8 bits).</summary>
    private int _AdditionalBufferCount;

    /// <summary>
    /// Sum of the lengths of the additional buffers (see <see cref="AdditionalBufferBytes"/>). One counter and no owner: the
    /// packet does not know which protocol bound what, a protocol applies its own per-packet total against this sum. Perf: one
    /// add per bound buffer, nothing per packet that binds nothing. Single writer until Seal, like the buffer table.
    /// </summary>
    private ulong _AdditionalBufferBytes;

    /// <summary>
    /// Adds an additional data buffer (e.g., reassembled data). Returns the 1-based buffer index of the stored buffer.
    /// After <see cref="Seal"/> or when the table of 255 buffers is full, nothing is stored and the unchanged count is
    /// returned, so a caller detects the miss by comparing <see cref="BufferCount"/> before and after (see
    /// <see cref="TryBindBuffer"/>, the checked entry for protocols). The only place that stores a buffer: it also adds the
    /// length to <see cref="AdditionalBufferBytes"/>. Not thread-safe: one writer until Seal.
    /// </summary>
    /// <param name="buffer">The bytes to store; kept by reference, not copied, so they must outlive the packet.</param>
    /// <returns>
    /// The 1-based index of the stored buffer; the unchanged <see cref="_AdditionalBufferCount"/> when nothing was stored.
    /// </returns>
    internal int AddBuffer(ReadOnlyMemory<byte> buffer)
    {
        if (_Finalized != 0)
        {
            return _AdditionalBufferCount;
        }

        // Effect-layer keys pack the buffer index in 8 bits (0 = frame, 1..255 = additional).
        if (_AdditionalBufferCount >= 255)
        {
            return _AdditionalBufferCount;
        }

        // The table is allocated lazily on the first bound buffer (packets without reassembly never pay for it) and grows
        // by doubling from a small start.
        if (_AdditionalBuffers is null)
        {
            _AdditionalBuffers = new ReadOnlyMemory<byte>[2];
        }
        else if (_AdditionalBufferCount >= _AdditionalBuffers.Length)
        {
            Array.Resize(ref _AdditionalBuffers, _AdditionalBuffers.Length * 2);
        }
        _AdditionalBuffers[_AdditionalBufferCount] = buffer;
        _AdditionalBufferCount++;

        // Invariant: _AdditionalBufferBytes is the sum of the stored lengths. No overflow check: at most 255 buffers of at most
        // int.MaxValue bytes each stay far below ulong.MaxValue.
        _AdditionalBufferBytes += (ulong)buffer.Length;
        return _AdditionalBufferCount;
    }

    /// <summary>
    /// Sum of the lengths of all additional buffers stored so far, whichever protocol stored them; 0 for a packet without
    /// additional buffers and again 0 after the packet is recycled. A protocol that derives bytes from untrusted input reads it
    /// to apply its own per-packet total (see <see cref="TryBindBuffer"/>).
    /// </summary>
    /// <remarks>Thread-safety follows the packet parse contract: one writer until <see cref="Seal"/>; reading is cheap.</remarks>
    internal ulong AdditionalBufferBytes => _AdditionalBufferBytes;

    /// <summary>
    /// Stores <paramref name="buffer"/> as an additional packet buffer and returns that stored slice, or names why it could not.
    /// Nested <see cref="IProtocol.Parse"/> calls must use the returned slice so <see cref="TryGetEffectLayerKey"/> can
    /// identify the layer.
    /// </summary>
    /// <param name="buffer">Reassembled, decoded, or otherwise owned bytes that outlive this parse (not a pooled or stack buffer).</param>
    /// <param name="bound">
    /// The stored slice (the same memory as <paramref name="buffer"/>, no copy), suitable as the <c>data</c> argument of a nested
    /// parse; empty when the method returns <see langword="false"/>.
    /// </param>
    /// <param name="failure">
    /// <see cref="BufferBindFailure.Sealed"/> after <see cref="Seal"/>, <see cref="BufferBindFailure.TableFull"/> when 255
    /// buffers are stored, else <see cref="BufferBindFailure.None"/>.
    /// </param>
    /// <returns><see langword="true"/> when the buffer was stored; a refused bind changes nothing.</returns>
    /// <remarks>
    /// <para><b>What the packet decides.</b> Only whether a slot is free. The cap of 255 is a constant, not a setting: the
    /// effect-layer key packs the buffer index in bits 31–24 (index 0 is the frame, 1..255 are additional). The packet knows no
    /// protocol and has no byte limit; a protocol that derives bytes from untrusted input applies its own per-packet total by
    /// reading <see cref="AdditionalBufferBytes"/> before it binds (the bytes of every protocol are counted together).</para>
    /// <para><b>Replay.</b> A reparse of the same packet binds the same bytes in the same order on the new packet (recycle clears
    /// the table and the byte counter), so buffer indices and layer keys repeat. An empty buffer is stored like any other, so
    /// the indices of later buffers do not shift.</para>
    /// <para>Thread-safety follows the packet parse contract: one writer until <see cref="Seal"/>. Cost: one array store; the
    /// table is allocated on the first bound buffer of a packet object and reused when the packet is recycled.</para>
    /// </remarks>
    /// <seealso cref="AdditionalBufferBytes"/>
    /// <seealso cref="BufferBindFailure"/>
    internal bool TryBindBuffer(ReadOnlyMemory<byte> buffer, out ReadOnlyMemory<byte> bound, out BufferBindFailure failure)
    {
        bound = ReadOnlyMemory<byte>.Empty;

        // Sealed first: after Seal the table is frozen whatever it holds.
        if (_Finalized != 0)
        {
            failure = BufferBindFailure.Sealed;
            return false;
        }

        // Effect-layer keys pack the buffer index in 8 bits (0 = frame, 1..255 = additional); AddBuffer would refuse as well,
        // the explicit check names the failure.
        if (_AdditionalBufferCount >= 255)
        {
            failure = BufferBindFailure.TableFull;
            return false;
        }

        _ = AddBuffer(buffer);
        bound = buffer;
        failure = BufferBindFailure.None;
        return true;
    }

    /// <summary>
    /// Gets a buffer by index. 0 = frame data, 1+ = additional buffers.
    /// Negative indexes and indexes at or beyond <see cref="BufferCount"/> return <see langword="null"/>.
    /// </summary>
    /// <param name="index">0 for the frame data, 1 to <c>BufferCount - 1</c> for the additional buffers in bind order.</param>
    /// <returns>The buffer (no copy), or <see langword="null"/> when <paramref name="index"/> is out of range.</returns>
    public ReadOnlyMemory<byte>? Buffer(int index)
    {
        if (index < 0)
        {
            return null;
        }
        if (index == 0)
        {
            return Frame.Data;
        }
        int additional = index - 1;
        if (_AdditionalBuffers is not null
            && (uint)additional < (uint)_AdditionalBufferCount)
        {
            return _AdditionalBuffers[additional];
        }
        return null;
    }

    /// <summary>Total buffer count (1 for frame + additional).</summary>
    public int BufferCount => 1 + _AdditionalBufferCount;

    /// <summary>
    /// Packs the location of <paramref name="data"/> inside this packet's frame or additional
    /// buffers into an effect-store layer key (buffer index in bits 31–24, byte offset in bits 23–0).
    /// <para>
    /// <paramref name="data"/> must be a slice of <see cref="Frame.Data"/> or of a memory previously
    /// passed to <see cref="TryBindBuffer"/> — the <c>data</c> argument of
    /// <see cref="IProtocol.Parse"/>, not a heap copy.
    /// Buffers are scanned from index 0 (the frame) upward; the first match wins.
    /// A slice of <see cref="Frame.Data"/> that was also passed to
    /// <see cref="TryBindBuffer"/> therefore packs as the frame, not as that additional buffer.
    /// Nested parsers of reassembled bytes must see a memory that is not a frame slice
    /// (pass an owned copy into <see cref="TryBindBuffer"/>).
    /// A zero-length slice of a packet buffer is
    /// valid and packs that cursor; <see cref="ReadOnlyMemory{T}.Empty"/> is not a packet slice.
    /// Location is resolved from the shared array segment or memory manager of the two memories
    /// (no pointer arithmetic).
    /// </para>
    /// <para>
    /// Thread-safety follows the packet parse contract: one writer until Seal; reparse uses the same
    /// frame bytes and the same additional buffers the first parse recorded.
    /// Miss (not a packet slice) or packed-key overflow returns <see langword="false"/>.
    /// </para>
    /// </summary>
    /// <param name="data">Slice of a packet buffer, typically the <c>data</c> argument of Parse.</param>
    /// <param name="key">Packed layer key on success; 0 on miss.</param>
    /// <returns><see langword="true"/> when <paramref name="data"/> is a packable packet slice.</returns>
    internal bool TryGetEffectLayerKey(ReadOnlyMemory<byte> data, out int key)
    {
        int bufferCount = BufferCount;
        for (int i = 0; i < bufferCount; i++)
        {
            ReadOnlyMemory<byte>? buffer = Buffer(i);
            if (buffer is null)
            {
                continue;
            }

            if (_TryGetSliceOffset(buffer.Value, data, out int offset)
                && (uint)i <= 0xFFu
                && (uint)offset <= 0xFFFFFFu)
            {
                key = (i << 24) | offset;
                return true;
            }
        }

        key = 0;
        return false;
    }

    /// <summary>
    /// Finds the byte offset of <paramref name="slice"/> inside <paramref name="owner"/> when both views cover the same
    /// backing store. <see cref="ReadOnlyMemory{T}"/> exposes no address, so identity is established through the shared
    /// array segment or the shared memory manager instead of pointer arithmetic. Bounds are compared in
    /// <see cref="long"/> so offset plus length cannot overflow.
    /// </summary>
    /// <param name="owner">A packet buffer.</param>
    /// <param name="slice">The memory to locate.</param>
    /// <param name="offset">Byte offset of <paramref name="slice"/> inside <paramref name="owner"/> on success; 0 on a miss.</param>
    /// <returns><see langword="true"/> when <paramref name="slice"/> lies completely inside <paramref name="owner"/>.</returns>
    private static bool _TryGetSliceOffset(ReadOnlyMemory<byte> owner, ReadOnlyMemory<byte> slice, out int offset)
    {
        if (slice.Length > owner.Length)
        {
            offset = 0;
            return false;
        }

        if (MemoryMarshal.TryGetArray(owner, out ArraySegment<byte> ownerSeg)
            && MemoryMarshal.TryGetArray(slice, out ArraySegment<byte> sliceSeg)
            && ReferenceEquals(ownerSeg.Array, sliceSeg.Array)
            && sliceSeg.Offset >= ownerSeg.Offset
            && (long)sliceSeg.Offset + sliceSeg.Count <= (long)ownerSeg.Offset + ownerSeg.Count)
        {
            offset = sliceSeg.Offset - ownerSeg.Offset;
            return true;
        }

        if (MemoryMarshal.TryGetMemoryManager(owner, out MemoryManager<byte>? ownerMgr, out int ownerStart, out int ownerLength)
            && MemoryMarshal.TryGetMemoryManager(slice, out MemoryManager<byte>? sliceMgr, out int sliceStart, out int sliceLength)
            && ReferenceEquals(ownerMgr, sliceMgr)
            && sliceStart >= ownerStart
            && (long)sliceStart + sliceLength <= (long)ownerStart + ownerLength)
        {
            offset = sliceStart - ownerStart;
            return true;
        }

        offset = 0;
        return false;
    }

    #endregion

    #region Lazy fields

    /// <summary>Number of lazy populator slots per initial allocation (covers typical protocol stacks).</summary>
    private const int _LazyPopulatorChunkSize = 8;

    /// <summary>Default slab capacity for lazy populators: 512 × 8 B = 4 KB.</summary>
    private const int _LazyPopulatorSlabCapacity = 512;

    /// <summary>
    /// Maximum number of targeted materialization iterations before giving up, and the nesting limit of skip-mode lazy
    /// populators: the live value of <see cref="CoreLimits.MaxLazyDepth"/> (<c>packet.maxLazyDepth</c>). Protects against
    /// infinite loops from buggy populators that recursively create self-referencing lazy fields. Read once per decision.
    /// </summary>
    private int _MaxMaterializationDepth => CoreLimitSet.IntCeiling(Stack.Limits.MaxLazyDepth);

    /// <summary>
    /// 1 once this packet reported that a targeted lookup stopped at <see cref="CoreLimits.MaxLazyDepth"/>; the report is made
    /// once per parse so repeated lookups do not append an error field each. Interlocked; reset by a recycle.
    /// </summary>
    private int _LazyDepthReported;

    /// <summary>
    /// Per-thread bump allocator of lazy populator slots: multiple packets share a single backing array. When a slab is
    /// full, a new one is created; the old one stays alive via the references of the packets that use it.
    /// </summary>
    [ThreadStatic]
    private static SlabAllocator<LazyPopulator>? _LazyPopulatorSlab;

    /// <summary>
    /// How many lazy populators the current thread is executing right now (nesting counts). A thread inside a populator never
    /// waits for another lazy field that is being populated: the field in progress may be the one it is populating itself
    /// (post-Seal a populator that reads its own container would spin for ever), and two populators waiting for each other's
    /// field would deadlock. Such a read sees the children linked so far, without waiting. Thread-static
    /// because the answer is about the calling thread; read only on the rare "someone else is populating" path, written once
    /// per populator call. Rejected: a thread id in the field body (a layout change on the hottest struct for a rare case) and
    /// a try/finally-free counter (an exception in a populator would leave the thread "inside" for good).
    /// </summary>
    [ThreadStatic]
    private static int _PopulatorDepth;

    /// <summary>
    /// LazyPopulator storage: slab-backed like <see cref="FieldBody"/>. Published as one object so concurrent nested
    /// <see cref="MutField.AppendLazy"/> cannot tear (array, offset) on growth.
    /// </summary>
    private volatile LazyPopulatorTable? _LazyTable;

    /// <summary>High-water mark of valid populator slots; bounds the <c>Array.Clear</c> of a recycle.</summary>
    private volatile int _LazyPopulatorCount;

    /// <summary>Next populator slot to hand out (the number of reserved slots); at least <see cref="_LazyPopulatorCount"/>.</summary>
    private volatile int _AllocatedLazyCount;

    /// <summary>
    /// How many lazy populators have not yet been invoked (<see cref="int"/> for <see cref="Volatile"/> compatibility).
    /// Incremented by <see cref="RegisterLazyPopulator"/>, decremented by <see cref="MaterializeLazyField"/>. Enables O(1)
    /// <see cref="HasUnpopulatedLazyFields"/> and the fast exit in all materialization paths. Always mutated via
    /// <see cref="Interlocked"/>; plain volatile reads serve the fast-path checks.
    /// </summary>
    private volatile int _PendingLazyCount;

    /// <summary>
    /// Number of lazy materializations in flight after the seal (<see cref="Interlocked"/>). A recycle refuses to run while it
    /// is not 0, so no populator reads storage that is cleared.
    /// </summary>
    private volatile int _ActiveLazyMaterializations;

    /// <summary>
    /// Whether any lazy fields exist that have not been populated yet.
    /// O(1) volatile load of the pending-populator count for cross-thread visibility.
    /// </summary>
    public bool HasUnpopulatedLazyFields
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _PendingLazyCount > 0;
    }

    /// <summary>
    /// Registers a lazy populator for the given field index.
    /// <para>
    /// Called during single-threaded parsing (pre-<see cref="Seal"/>) AND from within lazy
    /// populators during materialization (post-Seal). Post-Seal callers hold per-field
    /// materialization claims via <see cref="FieldBody.TryClaimLazyMaterialization"/>.
    /// </para>
    /// <para>
    /// In Skip mode no tree exists: the populator runs at once on a scratch body when the value cache or an observer needs
    /// the children, and is dropped otherwise.
    /// </para>
    /// </summary>
    /// <param name="fieldId">The id of the container field.</param>
    /// <param name="fieldIndex">Storage index of the container field (ignored in Skip mode).</param>
    /// <param name="value">The container's value; used only for the scratch body in Skip mode.</param>
    /// <param name="customText">The container's custom text; used only for the scratch body in Skip mode.</param>
    /// <param name="populator">Appends the children when the field is materialized; called at most once; must not be <see langword="null"/>.</param>
    /// <remarks>
    /// Failures (a null populator, slot table exhausted, re-registration of a container that is being materialized, materialization depth
    /// exceeded in Skip mode) are recorded as a <c>packet.error</c> field and the populator is dropped; nothing is thrown for them.
    /// </remarks>
    internal void RegisterLazyPopulator(
        FieldId fieldId, ushort fieldIndex, in FieldValue value, LazyString customText, LazyPopulator populator)
    {
        // A null delegate is a bug of the dissector, found at the registration call. It is reported like the other registration
        // failures of this method (a recorded packet error, nothing thrown): the dissector keeps running, the failure is
        // counted, and the container stays empty. The check also protects the materializer, which waits for a registered
        // populator: an empty slot would otherwise read as a pending table growth and wait for ever.
        if (populator is null)
        {
            SetFieldError(
                _HasFieldTree
                    ? fieldIndex
                    : FieldBody.SkipStorageIndex,
                "Lazy populator must not be null.");
            return;
        }

        // Skip mode: no tree exists, so a deferred container would never be read later. Run the populator right now on a
        // scratch body, but only when the value cache or a watch needs the children; otherwise drop it and pay nothing.
        // The scratch stack is bounded by the same depth limit as targeted materialization because populators can nest.
        if (!_HasFieldTree)
        {
            bool cacheWants = _ActiveValueCache is not null && _ActiveValueCache.ShouldMaterialize(fieldId);
            bool watchWants = _WatchExpands(fieldId);
            if (!cacheWants && !watchWants)
            {
                return;
            }

            int maxDepth = _MaxMaterializationDepth;
            if (_SkipScratchDepth >= maxDepth)
            {
                SetFieldError(
                    FieldBody.SkipStorageIndex,
                    CoreLimits.MaxLazyDepth.HitMessage(
                        Invariant($"Skip-tree lazy populator nesting reached {_SkipScratchDepth} levels"),
                        Stack.Limits.MaxLazyDepth.Value));
                return;
            }

            _PushSkipScratch(new FieldBody(fieldId, value));
            if (!customText.IsNull)
            {
                _SkipScratchStack![_SkipScratchDepth - 1].SetCustomText(customText); // Invariant: _PushSkipScratch above allocated the stack.
            }

            try
            {
                MutField skipContainer = new(this, FieldBody.SkipStorageIndex, fieldId);
                ParseResult result = populator(in skipContainer);
                if (result.TryGetError(out ParseError parseError))
                {
                    SetFieldError(FieldBody.SkipStorageIndex, parseError.Message);
                }
            }
            finally
            {
                _PopSkipScratch();
            }

            return;
        }

        // Build mode: reserve a slot, make sure the table covers it, store the delegate, then link it to the field.
        // The populator is stored before the field's LazyIndex is set, so a materializer that sees the index finds it.
        int slot = _ReserveLazyPopulatorSlot();
        if (slot < 0)
        {
            SetFieldError(fieldIndex, "Lazy populator table exceeded maximum slot count.");
            return;
        }

        _EnsureLazyCapacity(slot);
        // Grow publishes a new table after CopyTo. Store, then retry if this write landed in
        // a table that is no longer reader-visible so the populator cannot vanish into the
        // discarded buffer.
        while (true)
        {
            LazyPopulatorTable lazyTable = _LazyTable!; // Invariant: _EnsureLazyCapacity published a table before this loop.
            lazyTable.Buffer[lazyTable.Offset + slot] = populator;
            if (ReferenceEquals(lazyTable, _LazyTable))
            {
                break;
            }
        }

        // Registering on a container that is being materialized right now would overwrite the in-flight populator index.
        ref FieldBody target = ref GetFieldRef(fieldIndex);
        if ((target.ReadLazyIndexVolatile() & FieldBody.LazyIndexMaterializingBit) != 0)
        {
            SetFieldError(fieldIndex, "Lazy populator must not re-register the container being materialized.");
            return;
        }

        // LazyIndex is 1-based so that 0 means "not lazy". The pending count is raised last: a reader that sees a
        // non-zero count can always find the registered field.
        target.LazyIndex = (ushort)(slot + 1);
        _PublishLazyPopulatorCount(slot + 1);
        Interlocked.Increment(ref _PendingLazyCount);

        // A watched id in this container's index group is produced only by running the populator.
        // Materialize now so OnField sees the child during this parse, not on a later walk.
        if (_WatchExpands(fieldId))
        {
            MaterializeLazyField(fieldIndex);
        }
    }

    /// <summary>
    /// Reserves a unique lazy-populator slot. Pre-Seal is a plain increment; post-Seal uses CAS
    /// so concurrent nested <see cref="MutField.AppendLazy"/> calls cannot share a slot.
    /// Returns -1 when the 1-based <see cref="FieldBody.LazyIndex"/> range is exhausted.
    /// </summary>
    /// <returns>The 0-based slot, unique for this packet until the next recycle; -1 when no slot number is left.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ReserveLazyPopulatorSlot()
    {
        // Format limit, a constant: the 1-based slot number is stored in the low 15 bits of FieldBody.LazyIndex, because bit 15
        // is the materializing marker. A slot number of 0x8000 or more would look like "being materialized": the field would
        // never be populated, no claim would succeed and a waiting reader would spin forever. Such a slot must not be handed out.
        // Exceeding it is reported as a field error by the caller (return -1), never dropped silently.
        const int maxSlots = FieldBody.LazyIndexMaterializingBit - 1;

        // Post-Seal several materializers can register nested containers at once: reserve with a CAS loop (lock-free, no
        // allocation). Pre-Seal exactly one thread parses this instance, so a plain read and write suffice.
        if (_Finalized != 0)
        {
            int current = _AllocatedLazyCount;
            while (true)
            {
                if (current >= maxSlots)
                {
                    return -1;
                }

                int updated = Interlocked.CompareExchange(ref _AllocatedLazyCount, current + 1, current);
                if (updated == current)
                {
                    return current;
                }

                current = updated;
            }
        }

        int count = _AllocatedLazyCount;
        if (count >= maxSlots)
        {
            return -1;
        }

        _AllocatedLazyCount = count + 1;
        return count;
    }

    /// <summary>
    /// Ensures the lazy-populator array can hold <paramref name="slot"/>. The thread whose slot
    /// equals the current capacity grows; others spin until capacity covers the slot.
    /// </summary>
    /// <param name="slot">A slot reserved by <see cref="_ReserveLazyPopulatorSlot"/>; the table must cover it on return.</param>
    private void _EnsureLazyCapacity(int slot)
    {
        // Growth is lock-free by designating one grower: slot numbers are unique, so exactly the thread that reserved the
        // slot equal to the current capacity (or slot 0 for the first table) allocates; everyone else spins until the
        // new table is published (a reader of _LazyTable never sees a half-built table: it is one immutable object).
        SpinWait waiter = default;
        while (true)
        {
            LazyPopulatorTable? lazyTable = _LazyTable;
            if (lazyTable is not null && slot < lazyTable.Capacity)
            {
                return;
            }

            if (lazyTable is null)
            {
                if (slot != 0)
                {
                    waiter.SpinOnce();
                    continue;
                }

                AllocateFromSlab(
                    ref _LazyPopulatorSlab, _LazyPopulatorSlabCapacity,
                    _LazyPopulatorChunkSize, out LazyPopulator[] buffer, out int offset);

                // The volatile _LazyTable write is the publish. A spin does not order it.
                _LazyTable = new LazyPopulatorTable(buffer, offset, _LazyPopulatorChunkSize);
                return;
            }

            int oldCapacity = lazyTable.Capacity;
            if (slot == oldCapacity)
            {
                int newCapacity = oldCapacity * 2;
                LazyPopulator[] grown = new LazyPopulator[newCapacity];
                lazyTable.Buffer.AsSpan(lazyTable.Offset, oldCapacity).CopyTo(grown);
                _LazyTable = new LazyPopulatorTable(grown, offset: 0, newCapacity);
                // Stores that landed in the old buffer after CopyTo are invisible in `grown` until this backfill; a
                // materializer waits for the slot (see MaterializeLazyField). Writers that already observed the new table retry.
                for (int i = 0; i < oldCapacity; i++)
                {
                    LazyPopulator fromOld = lazyTable.Buffer[lazyTable.Offset + i];
                    if (fromOld is not null && grown[i] is null)
                    {
                        grown[i] = fromOld;
                    }
                }

                return;
            }

            waiter.SpinOnce();
        }
    }

    /// <summary>
    /// Publishes the high-water mark of valid lazy-populator slots for recycle Array.Clear.
    /// Concurrent nested registers must not shrink the count.
    /// </summary>
    /// <param name="newCount">Highest reserved slot number plus one; a smaller value than the current count is ignored.</param>
    private void _PublishLazyPopulatorCount(int newCount)
    {
        int observed = _LazyPopulatorCount;
        while (observed < newCount)
        {
            int updated = Interlocked.CompareExchange(ref _LazyPopulatorCount, newCount, observed);
            if (updated == observed)
            {
                break;
            }

            observed = updated;
        }
    }

    /// <summary>
    /// Materializes a lazy field's children if not already populated.
    /// <para>
    /// <b>Pre-Seal (single-threaded):</b> Makes one claim attempt on <see cref="FieldBody.LazyIndex"/> (the same CAS
    /// that marks the field as materializing, so a nested re-registration is detected). It neither counts the
    /// materializer in <c>_ActiveLazyMaterializations</c> nor consults the recycle gate nor spins, because no other thread
    /// reads or recycles the packet before <see cref="Seal"/>.
    /// </para>
    /// <para>
    /// <b>Post-Seal (concurrent):</b> Counts the materializer in
    /// <c>_ActiveLazyMaterializations</c> before re-reading the recycle gate, then per-field CAS
    /// so exactly one thread executes each populator; other threads spin until the field's lazy
    /// index clears, so every caller returns with a finished child list. Callers must therefore enter this method while the
    /// field is merely <i>in progress</i> as well (<see cref="FieldBody.IsLazyPending"/>, not
    /// <see cref="FieldBody.NeedsMaterialization"/>). A thread that is itself inside a populator does not wait (it could be
    /// waiting for its own field).
    /// </para>
    /// </summary>
    /// <param name="fieldIndex">Storage index of the container field.</param>
    /// <returns>
    /// <see langword="true"/> when this call ran the populator to its end; <see langword="false"/> when there was nothing to do,
    /// another thread populated the field (this call then waited for it), or the populator reported or threw an error (the
    /// error is recorded under the container and counted in <see cref="ErrorCount"/>).
    /// </returns>
    internal bool MaterializeLazyField(ushort fieldIndex)
    {
        bool postSeal = _Finalized != 0;
        if (_PendingLazyCount == 0)
        {
            return false;
        }

        // Publish Active before re-reading the gate so PrepareForReuse cannot sample zero
        // under a live materializer and Array.Clear cannot run under GetFieldRef.
        if (postSeal)
        {
            Interlocked.Increment(ref _ActiveLazyMaterializations);
        }

        try
        {
            LazyPopulatorTable? lazyTable = _LazyTable;
            if (lazyTable is null || _PendingLazyCount == 0 || (postSeal && _RecycleGate != 0))
            {
                return false;
            }

            ref FieldBody body = ref GetFieldRef(fieldIndex);

            // Per-field pre-check before claiming (cheap filter)
            if (!body.NeedsMaterialization)
            {
                if (body.ReadLazyIndexVolatile() == 0)
                {
                    return false;
                }

                // Another thread is materializing this field — spin until complete, so the caller reads a finished child
                // list. Not from inside a populator (see _PopulatorDepth): there the field in progress may be our own.
                if (_PopulatorDepth == 0 && body.IsLazyMaterializationInProgress())
                {
                    SpinWait spin = default;
                    while (body.IsLazyMaterializationInProgress())
                    {
                        spin.SpinOnce();
                    }
                }
                return false;
            }

            ushort lazyPopulatorIndex = 0;
            bool claimed = false;
            if (postSeal)
            {
                SpinWait spin = default;
                while (true)
                {
                    if (body.TryClaimLazyMaterialization(out lazyPopulatorIndex))
                    {
                        claimed = true;
                        break;
                    }

                    if (body.ReadLazyIndexVolatile() == 0)
                    {
                        break;
                    }

                    // Another thread won the claim in the meantime and is populating. Wait for it, except from inside a
                    // populator of our own (see _PopulatorDepth).
                    if (_PopulatorDepth != 0)
                    {
                        break;
                    }

                    spin.SpinOnce();
                }
            }
            else
            {
                claimed = body.TryClaimLazyMaterialization(out lazyPopulatorIndex);
            }

            if (!claimed)
            {
                return false;
            }

            // A populator is stored before its field is published, and this thread holds the claim, so the slot of a claimed
            // field is filled exactly once and cleared only below. An empty slot therefore means: a table growth published the
            // new table and has not backfilled this slot yet (see _EnsureLazyCapacity; the grower publishes first so that no
            // late store is lost). Wait for the backfill instead of invoking null. Re-read the table on every turn: a nested
            // register may have grown it again since the snapshot above.
            SpinWait backfillWait = default;
            int arrayIndex;
            LazyPopulator? populator;
            while (true)
            {
                LazyPopulatorTable? publishedTable = _LazyTable;
                if (publishedTable is not null)
                {
                    lazyTable = publishedTable;
                }

                arrayIndex = lazyTable.Offset + lazyPopulatorIndex - 1;
                populator = lazyTable.Buffer[arrayIndex];
                if (populator is not null)
                {
                    break;
                }

                backfillWait.SpinOnce();
            }

            try
            {
                // Clear the slot so the captured state can be collected. Only the claim holder clears, and it claims once.
                lazyTable.Buffer[arrayIndex] = null!; // Invariant: the slot is never read again after the claim; null marks it consumed.

                MutField containerField = new(this, fieldIndex, GetFieldRef(fieldIndex).FieldId);
                _PopulatorDepth++;
                try
                {
                    ParseResult result = populator(in containerField);
                    if (result.TryGetError(out ParseError populateError))
                    {
                        // Attach the error under the lazy container field (not at root)
                        SetFieldError(fieldIndex, Invariant($"Lazy field population failed: {populateError}"));
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Error policy of every parse boundary (this is its home; the other catches refer here): everything a
                    // dissector, an observer, or a populator throws is contained as a packet error, except
                    // OutOfMemoryException. OOM must propagate: catching it would hide a fatal process condition, and the
                    // handler itself allocates (message text, error field) in the very situation in which allocation failed.
                    // Catch protocol/parser failures (FieldAppendException, NullReferenceException, etc.)
                    // and attach the error under the lazy container field (not at root).
                    RecordCaughtError(fieldIndex, BuildCaughtExceptionText(ex, includeStackTrace: false));
                    return false;
                }
                finally
                {
                    _PopulatorDepth--;

                    // Decrement pending count AFTER the populator has finished appending all
                    // child fields. Always Interlocked so the volatile field never uses compound RMW.
                    Interlocked.Decrement(ref _PendingLazyCount);
                }
                return true;
            }
            finally
            {
                body.FinishLazyMaterialization();
            }
        }
        finally
        {
            if (postSeal)
            {
                Interlocked.Decrement(ref _ActiveLazyMaterializations);
            }
        }
    }

    /// <summary>
    /// Materializes all lazy fields that have not been populated yet.
    /// <para>
    /// <b>Pre-Seal:</b> Called single-threaded during parse. Takes no recycle-gate or active-materializer accounting
    /// and does not spin; <see cref="MaterializeLazyField"/> makes a single claim attempt per field.
    /// </para>
    /// <para>
    /// <b>Post-Seal:</b> Thread-safe. Multiple threads may call concurrently; each waits
    /// until all lazy fields are fully populated before returning. A thread that is itself inside a populator does not
    /// wait for the fields that are still in progress (one of them may be its own container).
    /// </para>
    /// </summary>
    public void MaterializeAll()
    {
        if (_PendingLazyCount == 0)
        {
            return;
        }

        // Branch on Seal state: pre-Seal is single-threaded (no Volatile reads needed
        // in the scan loop); post-Seal requires volatile reads and spin-wait for
        // concurrent materialization in progress by other threads.
        if (_Finalized == 0)
        {
            _MaterializeAllPreSeal();
        }
        else
        {
            // Count the whole scan so PrepareForReuse cannot Array.Clear between fields
            // while GetFieldRef runs with _ActiveLazyMaterializations at 0.
            Interlocked.Increment(ref _ActiveLazyMaterializations);
            try
            {
                if (_RecycleGate == 0)
                {
                    _MaterializeAllPostSeal();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _ActiveLazyMaterializations);
            }
        }
    }

    /// <summary>
    /// Pre-Seal materialization: single-threaded, no spin-wait and no recycle-gate accounting.
    /// Delegates to <see cref="MaterializeLazyField"/>, which makes a single claim attempt per field when pre-Seal.
    /// </summary>
    private void _MaterializeAllPreSeal()
    {
        // Outer loop handles nested lazy fields registered during population
        while (_PendingLazyCount > 0)
        {
            int count = _FieldCount;
            for (int i = 0; i < count; i++)
            {
                if (_PendingLazyCount == 0)
                {
                    break;
                }

                if (GetFieldRef(i).NeedsMaterialization)
                {
                    MaterializeLazyField((ushort)i);
                }
            }
        }
    }

    /// <summary>
    /// Post-Seal materialization: thread-safe, uses Volatile reads and SpinWait to
    /// coordinate with concurrent threads also materializing fields on the same packet.
    /// </summary>
    private void _MaterializeAllPostSeal()
    {
        // Outer loop handles nested lazy fields: materializing one container may
        // register new lazy containers. Each pass re-reads _FieldCount so newly
        // appended lazy fields are included in the scan.
        while (_PendingLazyCount > 0)
        {
            int count = _FieldCount;
            bool progress = false;

            for (int i = 0; i < count; i++)
            {
                if (_PendingLazyCount == 0)
                {
                    break;
                }

                if (GetFieldRef(i).NeedsMaterialization)
                {
                    MaterializeLazyField((ushort)i);
                    progress = true;
                }
            }

            // If this pass materialized nothing but lazy fields remain, another
            // thread is currently inside MaterializeLazyField (it already cleared
            // LazyIndex but has not yet finished the populator). Spin until either
            // _PendingLazyCount reaches zero or _FieldCount grows (nested lazy fields
            // added by the concurrent populator need a new outer-loop pass).
            if (!progress && _PendingLazyCount > 0)
            {
                // Stop rather than freeze the caller when (a) a nested RegisterLazyPopulator leaked a pending count
                // without a live field materializer (this session holds the remaining 1), or (b) the calling thread is
                // itself inside a populator (see _PopulatorDepth): the remaining pending count may be its own container,
                // which only finishes after this call returns. It leaves with what is linked so far, as
                // MaterializeLazyField does for a populator thread.
                if (_ActiveLazyMaterializations <= 1 || _PopulatorDepth != 0)
                {
                    break;
                }

                SpinWait spin = default;
                while (_PendingLazyCount > 0
                    && _FieldCount == count)
                {
                    spin.SpinOnce();
                }
            }
        }
    }

    /// <summary>
    /// Generic slab allocation helper. Tries to allocate <paramref name="count"/> slots from
    /// the thread-local <see cref="SlabAllocator{T}"/>. If the current slab is full or null,
    /// creates a new slab with <paramref name="slabCapacity"/> and retries.
    /// <para>
    /// Shared by the lazy-populator table and <see cref="FieldStorage"/>. It lives here and not on
    /// <see cref="SlabAllocator{T}"/> because that file is link-compiled into <c>NetworkInspector.Sources</c>, which
    /// has no <see cref="ParseError"/>. The JIT specializes it per <typeparamref name="T"/>, so there is no boxing or
    /// virtual dispatch overhead.
    /// </para>
    /// <para>
    /// Thread-safety: the caller passes its own thread-static slab by reference, so no synchronization is needed.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The slot type of the slab.</typeparam>
    /// <param name="slab">The caller's thread-static slab; replaced by a new slab when the current one cannot serve the request.</param>
    /// <param name="slabCapacity">Slot capacity of a new slab; raised to <paramref name="count"/> when smaller.</param>
    /// <param name="count">Number of consecutive slots to allocate.</param>
    /// <param name="buffer">The backing array that holds the slots.</param>
    /// <param name="offset">Index of the first allocated slot in <paramref name="buffer"/>.</param>
    /// <exception cref="FieldAppendException">No slab could serve the request after the bounded number of growth attempts.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AllocateFromSlab<T>(
        ref SlabAllocator<T>? slab, int slabCapacity,
        int count, out T[] buffer, out int offset)
    {
        if (slab is not null && slab.TryAllocate(count, out buffer, out offset))
        {
            return;
        }

        // Current slab is full or doesn't exist — create a new slab large enough for count.
        // The old slab's backing array stays alive via consumer references.
        // The capacity is at least count, so the first attempt cannot fail (TryAllocate fails only when count exceeds the
        // free space). The bounded retry with doubling is a defensive guard that turns an impossible state into a reported
        // error instead of an endless loop.
        const int _MaxSlabGrowAttempts = 8;
        int capacity = Math.Max(slabCapacity, count);
        for (int attempt = 0; attempt < _MaxSlabGrowAttempts; attempt++)
        {
            slab = new SlabAllocator<T>(capacity);
            if (slab.TryAllocate(count, out buffer, out offset))
            {
                return;
            }

            capacity = Math.Max(capacity * 2, count);
        }

        buffer = null!; // Invariant: the throw below never returns, so no caller sees this null buffer.
        offset = 0;
        ThrowHelpers.ThrowFieldAppend(ParseError.Custom(
            "packet",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Slab allocation failed after {_MaxSlabGrowAttempts} attempts (requested {count} slots).")));
    }

    /// <summary>
    /// Immutable snapshot of the lazy-populator array plus its slab offset and capacity.
    /// Published as a single reference so nested post-Seal <see cref="RegisterLazyPopulator"/>
    /// cannot observe a torn (array, offset) pair while the table grows.
    /// </summary>
    private sealed class LazyPopulatorTable
    {
        /// <summary>Backing array (slab slice or heap copy after growth).</summary>
        internal readonly LazyPopulator[] Buffer;

        /// <summary>Index of this packet's first populator slot in <see cref="Buffer"/>.</summary>
        internal readonly int Offset;

        /// <summary>Number of populator slots reserved for this packet in <see cref="Buffer"/>.</summary>
        internal readonly int Capacity;

        /// <summary>Creates a published lazy-populator table snapshot.</summary>
        /// <param name="buffer">The backing array.</param>
        /// <param name="offset">Index of this packet's first slot in <paramref name="buffer"/>.</param>
        /// <param name="capacity">Number of slots reserved for this packet.</param>
        internal LazyPopulatorTable(LazyPopulator[] buffer, int offset, int capacity)
        {
            Buffer = buffer;
            Offset = offset;
            Capacity = capacity;
        }
    }

    #endregion

    #region Packet info text

    /// <summary>
    /// Storage index of the <c>packet.info</c> field once <see cref="Protocols.PacketProtocol"/> appended it;
    /// <see cref="FieldBody.NullIndex"/> before that and in Skip mode. Cold parse-only state.
    /// </summary>
    private ushort _InfoFieldIndex = FieldBody.NullIndex;

    /// <summary>
    /// The packet info/summary string.
    /// <para>
    /// When <see cref="Protocols.PacketProtocol"/> has finished parsing, reads from the
    /// <c>packet.info</c> field whose value is a boxed <see cref="LazyString"/> —
    /// the summary string is evaluated and cached in-heap on first access.
    /// Before the field exists (during Parse) falls back to the side-channel
    /// <see cref="InfoLazy"/> LazyString directly.
    /// </para>
    /// </summary>
    public string Info
    {
        get
        {
            if (_InfoFieldIndex != FieldBody.NullIndex)
            {
                // Read from the packet.info field. The boxed LazyString stored in
                // FieldValueData is accessed via Unsafe.Unbox so the CAS-based caching
                // persists to the heap-resident struct on first call.
                FieldValueData data = GetFieldRef(_InfoFieldIndex).Value.Data;
                if (data.TryGetAsString(out string infoStr))
                {
                    return infoStr;
                }
            }
            return InfoLazy.AsString;
        }
    }

    /// <summary>
    /// The raw <see cref="LazyString"/> set by sub-protocols during parsing.
    /// Read by <see cref="Protocols.PacketProtocol"/> to create the <c>packet.info</c> field
    /// value (boxed <see cref="LazyString"/>) without evaluating the factory.
    /// </summary>
    internal LazyString InfoLazy { get; set; }

    /// <summary>
    /// Records the storage index of the <c>packet.info</c> FieldBody.
    /// Called by <see cref="Protocols.PacketProtocol"/> after the field is appended so that
    /// subsequent reads of <see cref="Info"/> go through the field's
    /// <see cref="LazyString"/> for in-heap cached lazy evaluation. Ignored in Skip mode, where no field is stored.
    /// </summary>
    /// <param name="index">Storage index of the appended <c>packet.info</c> field.</param>
    internal void SetInfoFieldIndex(ushort index)
    {
        if (!_HasFieldTree)
        {
            return;
        }

        _InfoFieldIndex = index;
    }

    /// <summary>
    /// Sets the packet info/summary string. Once <c>packet.info</c> was appended, <see cref="Info"/> reads that field and
    /// a later change of this string is not seen.
    /// </summary>
    /// <param name="info">The deferred summary text; evaluated on first read of <see cref="Info"/>.</param>
    internal void SetInfo(LazyString info) => InfoLazy = info;

    /// <summary>Appends to the packet info/summary string. Same timing as <see cref="SetInfo"/>.</summary>
    /// <param name="suffix">The deferred text to add after the current summary.</param>
    internal void AppendToInfo(LazyString suffix) => InfoLazy = InfoLazy.Append(suffix);

    /// <summary>Prepends to the packet info/summary string. Same timing as <see cref="SetInfo"/>.</summary>
    /// <param name="prefix">The deferred text to add before the current summary.</param>
    internal void PrependToInfo(LazyString prefix) => InfoLazy = prefix.Append(InfoLazy);

    #endregion

    #region Field lookup and iteration

    /// <summary>
    /// Searches the flat field array for a field with the given ID and returns its value.
    /// Convenience wrapper over <see cref="TryGetNextField"/> that always starts
    /// from the beginning of the field array (first occurrence wins). Copies the value (32 bytes); to read a value
    /// repeatedly or to see custom text, use <see cref="TryGetNextField"/> and read <c>field.Value</c> by reference.
    /// </summary>
    /// <param name="fieldId">The field ID to search for.</param>
    /// <param name="value">Receives the field value if found.</param>
    /// <param name="materialize">
    /// When <see langword="true"/>, triggers targeted lazy materialization if the
    /// field is not found in eagerly-populated fields.
    /// When <see langword="false"/>, only searches already-materialized fields.
    /// </param>
    /// <returns><see langword="true"/> if the field was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetFieldValue(FieldId fieldId, out FieldValue value, bool materialize)
    {
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        if (!TryGetNextField(fieldId, ref cookie, out Field field, materialize))
        {
            value = FieldValue.None;
            return false;
        }

        value = field.Value;
        return true;
    }

    /// <summary>
    /// Searches the flat field array for the next occurrence of a field with the given ID, starting from the position encoded in
    /// <paramref name="cookie"/>, and returns it as a <see cref="Field"/>: its value (<c>field.Value</c>, a reference to the
    /// field's own storage) and its custom text are readable. Returns each occurrence on successive calls, enabling iteration over
    /// multi-occurrence fields (e.g., tunneled packets with inner and outer IP headers, repeated options).
    /// </summary>
    /// <remarks>
    /// <para><b>Cookie semantics:</b> The cookie is an opaque continuation token. Use <see cref="FieldLookupCookie.Start"/> for the
    /// first call. After each successful call it is updated so that the next call continues after the found field; after an
    /// unsuccessful call it is parked at the end of the searched range.</para>
    /// <para><b>Usage pattern:</b></para>
    /// <code>
    /// FieldLookupCookie cookie = FieldLookupCookie.Start;
    /// while (packet.TryGetNextField(ipSrcFieldId, ref cookie, out Field field, materialize: false))
    /// {
    ///     ref readonly FieldValue value = ref field.Value; // no 32-byte copy; process each occurrence of ip.src
    /// }
    /// // Pass materialize: true when the lookup must wait for lazy containers.
    /// </code>
    /// <para><b>Lazy materialization (when <c>materialize</c> is true):</b> If the field is not found among eagerly-populated
    /// fields and lazy containers exist, only the owning protocol's lazy containers are materialized, not all pending lazy fields,
    /// so the common case (found eagerly) costs nothing extra. Materialization appends new fields at the end of the flat array, so
    /// after the first scan only the newly appended fields are scanned after each step. A populator may register further lazy
    /// containers of the same protocol (FrameProtocol creates a lazy interface container inside its lazy frame container); the
    /// loop handles them until the field is found, no matching container is left, or <see cref="_MaxMaterializationDepth"/> is
    /// reached.</para>
    /// </remarks>
    /// <seealso cref="TryGetFieldValue"/>
    /// <seealso cref="FieldLookupCookie"/>
    /// <param name="fieldId">The field ID to search for.</param>
    /// <param name="cookie">
    /// Opaque continuation token. Use <see cref="FieldLookupCookie.Start"/> for the first call; do not modify it between calls.
    /// </param>
    /// <param name="field">Receives the field wrapper if found.</param>
    /// <param name="materialize">
    /// When <see langword="true"/>, triggers targeted lazy materialization if the
    /// field is not found in eagerly-populated fields.
    /// </param>
    /// <returns><see langword="true"/> if a (next) occurrence was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetNextField(FieldId fieldId, ref FieldLookupCookie cookie, out Field field, bool materialize)
    {
        int count = _FieldCount;

        for (int i = cookie.Position; i < count; i++)
        {
            ref readonly FieldBody body = ref GetFieldRef(i);
            if (body.FieldId == fieldId)
            {
                field = new Field(this, (ushort)i, fieldId);
                cookie.Position = i + 1;
                return true;
            }
        }

        int searchedUpTo = count;

        if (materialize && _PendingLazyCount > 0)
        {
            FieldInfo? targetInfo = Stack.GetField(fieldId);
            if (targetInfo is null)
            {
                cookie.Position = searchedUpTo;
                field = default;
                return false;
            }

            ProtocolId targetProtocolId = targetInfo.ProtocolId;

            int maxDepth = _MaxMaterializationDepth;
            int depth = 0;
            for (; depth < maxDepth; depth++)
            {
                if (!HasUnpopulatedLazyFields)
                {
                    break;
                }

                count = _FieldCount;
                bool materialized = false;

                for (int i = 0; i < count; i++)
                {
                    // A container another thread is populating right now counts as well: MaterializeLazyField waits for it,
                    // so its fields are searched, not skipped (skipping would answer "not found" for a field that exists).
                    ref readonly FieldBody fb = ref GetFieldRef(i);
                    if (!fb.IsLazyPending)
                    {
                        continue;
                    }

                    FieldInfo? containerInfo = Stack.GetField(fb.FieldId);
                    if (containerInfo is not null && containerInfo.ProtocolId == targetProtocolId)
                    {
                        MaterializeLazyField((ushort)i);
                        materialized = true;

                        int newCount = _FieldCount;
                        for (int k = searchedUpTo; k < newCount; k++)
                        {
                            ref readonly FieldBody newBody = ref GetFieldRef(k);
                            if (newBody.FieldId == fieldId)
                            {
                                field = new Field(this, (ushort)k, fieldId);
                                cookie.Position = k + 1;
                                return true;
                            }
                        }

                        searchedUpTo = newCount;
                        break;
                    }
                }

                if (!materialized)
                {
                    break;
                }
            }

            // The loop ended because the depth limit was reached, not because nothing was left to expand: the search is cut, so
            // the packet says so (once) instead of answering "not found" without a reason.
            if (depth >= maxDepth && HasUnpopulatedLazyFields)
            {
                _ReportLazyDepthLimit(depth);
            }
        }

        cookie.Position = searchedUpTo;
        field = default;
        return false;
    }

    /// <summary>
    /// Records, once per parse, that a targeted lookup stopped at <see cref="CoreLimits.MaxLazyDepth"/> while lazy fields were
    /// still unexpanded. A packet error, never an exception: the lookup answers "not found" and the packet stays readable.
    /// </summary>
    /// <param name="depth">The number of expansion rounds that ran.</param>
    private void _ReportLazyDepthLimit(int depth)
    {
        if (Interlocked.Exchange(ref _LazyDepthReported, 1) != 0)
        {
            return;
        }

        RecordCaughtError(
            0,
            CoreLimits.MaxLazyDepth.HitMessage(
                Invariant($"Targeted lazy expansion stopped after {depth} rounds"),
                Stack.Limits.MaxLazyDepth.Value));
    }

    /// <summary>
    /// Iterates all fields in depth-first pre-order (including root).
    /// When <paramref name="materialize"/> is true, lazy fields are materialized during traversal.
    /// </summary>
    /// <param name="materialize">
    /// <see langword="true"/> to populate pending lazy fields as the walk reaches them; <see langword="false"/> to walk only
    /// the fields linked so far.
    /// </param>
    /// <returns>An enumerable over the packet's fields as read cursors; allocation-free.</returns>
    public FieldDfsEnumerable IterFieldsDfs(bool materialize) => new(this, materialize);

    /// <summary>
    /// Iterates all fields in storage order (linear walk over the internal array).
    /// When <paramref name="materialize"/> is true, lazy fields are materialized as encountered.
    /// </summary>
    /// <param name="materialize">
    /// <see langword="true"/> to populate pending lazy fields as the walk reaches them; <see langword="false"/> to walk only
    /// the fields linked so far.
    /// </param>
    /// <returns>An enumerable over the packet's fields as read cursors; allocation-free.</returns>
    public FieldFlatEnumerable IterFieldsFlat(bool materialize) => new(this, materialize);

    #endregion

    #region Field insert and publish

    /// <summary>
    /// Reserves a unique storage slot index without publishing it to readers.
    /// Pre-Seal uses a plain increment of <see cref="_AllocatedFieldCount"/> (single-threaded parse).
    /// Post-Seal uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> so concurrent
    /// lazy materializers cannot share a slot. Callers write the <see cref="FieldBody"/>, fix
    /// parent/sibling pointers, then publish via <see cref="_PublishFieldCount"/>. Throws
    /// <c>FieldAppendException</c> once <paramref name="limit"/> slots are allocated.
    /// </summary>
    /// <returns>The reserved slot index; the caller must publish it or a tombstone, never leave it unpublished.</returns>
    /// <exception cref="FieldAppendException">The limit is reached.</exception>
    /// <param name="limit">Slot limit of this append.</param>
    /// <param name="postSeal">The caller's single read of <see cref="_Finalized"/> for this insert.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ReserveFieldSlot(int limit, bool postSeal)
    {
        if (postSeal)
        {
            int current = _AllocatedFieldCount;
            while (true)
            {
                if (current >= limit)
                {
                    ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
                }

                int updated = Interlocked.CompareExchange(ref _AllocatedFieldCount, current + 1, current);
                if (updated == current)
                {
                    return current;
                }
                current = updated;
            }
        }

        int count = _AllocatedFieldCount;
        if (count >= limit)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }
        _AllocatedFieldCount = count + 1;
        return count;
    }

    /// <summary>
    /// Publishes <paramref name="newCount"/> as the reader-visible <see cref="_FieldCount"/>
    /// after the field body and all parent / sibling linked-list stores. Post-Seal publication
    /// is in-order: this thread waits until the previous reserved index has been published so
    /// readers never observe a hole. Pre-Seal stores directly; <see cref="Seal"/> is the
    /// bulk publication fence for the parse.
    /// </summary>
    /// <param name="newCount">Reserved index + 1.</param>
    /// <param name="postSeal">The caller's single read of <see cref="_Finalized"/> for this insert.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PublishFieldCount(int newCount, bool postSeal)
    {
        if (!postSeal)
        {
            _FieldCount = newCount;
            return;
        }

        SpinWait spin = default;
        while (_FieldCount != newCount - 1)
        {
            spin.SpinOnce();
        }
        // Volatile field write is a release store: linked-list stores happen-before this publish.
        _FieldCount = newCount;
    }

    /// <summary>
    /// Publishes a reserved slot as an unlinked tombstone so <see cref="_PublishFieldCount"/>
    /// cannot hang after a throw between reserve and the happy-path publish.
    /// Parent/sibling links are not written: tree walkers skip the hole; flat readers see a dead slot.
    /// Cold path: reads <see cref="_Finalized"/> itself (Seal cannot change while the insert that failed runs).
    /// </summary>
    /// <param name="reservedIndex">The slot reserved by <see cref="_ReserveFieldSlot"/> that was not published.</param>
    /// <param name="fieldId">The id of the field that failed to insert; used for the dead slot when the stack has no error field.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _PublishReservedOrTombstone(int reservedIndex, FieldId fieldId)
    {
        bool postSeal = _Finalized != 0;
        FieldId tombstoneId = Stack.PacketErrorFieldId.IsValid
            ? Stack.PacketErrorFieldId
            : fieldId;
        _Fields.EnsureSlot(reservedIndex, postSeal);
        _Fields.Slot(reservedIndex) = new FieldBody(tombstoneId, FieldValue.None);
        _PublishFieldCount(reservedIndex + 1, postSeal);
    }

    /// <summary>Where <see cref="_InsertField"/> links the new field relative to its anchor.</summary>
    private enum InsertKind : byte
    {
        /// <summary>Last child of the anchor.</summary>
        Append,

        /// <summary>First child of the anchor.</summary>
        Prepend,

        /// <summary>Next sibling of the anchor (same parent).</summary>
        After,
    }

    /// <summary>
    /// Appends a child field to the given parent. <paramref name="limit"/> is the field limit for this append; only
    /// <see cref="RecordCaughtError"/> passes <see cref="_MaxFieldCount"/>.
    /// </summary>
    /// <param name="parentIndex">Storage index of the parent; the new field becomes its last child.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="limit">Slot limit of this append: <c>_RegularFieldLimit</c> by default, <c>_MaxFieldCount</c> for an error record.</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The packet holds <paramref name="limit"/> fields.</exception>
    internal ushort AppendChild(ushort parentIndex, FieldId fieldId, in FieldValue value, int limit = _RegularFieldLimit)
        => _InsertField(InsertKind.Append, parentIndex, fieldId, in value, default, limit);

    /// <summary>Appends a child field with custom display text (attached before the field is published).</summary>
    /// <param name="parentIndex">Storage index of the parent; the new field becomes its last child.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">The display text; a default text means "no custom text".</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The packet holds the regular maximum of fields.</exception>
    internal ushort AppendChildWithCustomText(ushort parentIndex, FieldId fieldId, in FieldValue value, LazyString customText)
        => _InsertField(InsertKind.Append, parentIndex, fieldId, in value, customText, _RegularFieldLimit);

    /// <summary>Prepends a child field with custom display text (attached before the field is published).</summary>
    /// <param name="parentIndex">Storage index of the parent; the new field becomes its first child.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">The display text; a default text means "no custom text".</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The packet holds the regular maximum of fields.</exception>
    internal ushort PrependChildWithCustomText(ushort parentIndex, FieldId fieldId, in FieldValue value, LazyString customText)
        => _InsertField(InsertKind.Prepend, parentIndex, fieldId, in value, customText, _RegularFieldLimit);

    /// <summary>Prepends a child field (inserts before all existing children).</summary>
    /// <param name="parentIndex">Storage index of the parent; the new field becomes its first child.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The packet holds the regular maximum of fields.</exception>
    internal ushort PrependChild(ushort parentIndex, FieldId fieldId, in FieldValue value)
        => _InsertField(InsertKind.Prepend, parentIndex, fieldId, in value, default, _RegularFieldLimit);

    /// <summary>
    /// Inserts a field after the given sibling with custom display text. The root has no siblings: inserting after
    /// it throws in Build and in Skip mode.
    /// </summary>
    /// <param name="siblingIndex">Storage index of the anchor; the new field becomes its next sibling.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <param name="customText">The display text; a default text means "no custom text".</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The anchor is the root, or the packet holds the regular maximum of fields.</exception>
    internal ushort InsertAfterWithCustomText(ushort siblingIndex, FieldId fieldId, in FieldValue value, LazyString customText)
    {
        _ThrowIfInsertAfterRoot(siblingIndex);
        return _InsertField(InsertKind.After, siblingIndex, fieldId, in value, customText, _RegularFieldLimit);
    }

    /// <summary>
    /// Inserts a field after the given sibling. The root has no siblings: inserting after it throws in Build and in
    /// Skip mode.
    /// </summary>
    /// <param name="siblingIndex">Storage index of the anchor; the new field becomes its next sibling.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the packet.</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode, where nothing is stored.</returns>
    /// <exception cref="FieldAppendException">The anchor is the root, or the packet holds the regular maximum of fields.</exception>
    internal ushort InsertAfter(ushort siblingIndex, FieldId fieldId, in FieldValue value)
    {
        _ThrowIfInsertAfterRoot(siblingIndex);
        return _InsertField(InsertKind.After, siblingIndex, fieldId, in value, default, _RegularFieldLimit);
    }

    /// <summary>
    /// Shared insert of every append, prepend, and insert-after variant. A default <paramref name="customText"/>
    /// means "no custom text": the slot starts with default text and the value cache ignores a null text. All parent /
    /// sibling linked-list stores happen <b>before</b> the field count is published, so a concurrent reader after Seal
    /// never sees the new count with stale links. A throw between reserve and publish publishes a tombstone instead.
    /// The cache record runs before the links are written, so the tombstone is never linked.
    /// </summary>
    /// <param name="kind">Where the field is linked relative to <paramref name="anchorIndex"/>.</param>
    /// <param name="anchorIndex">The parent for append and prepend, the sibling for insert-after.</param>
    /// <param name="fieldId">The registered field to store.</param>
    /// <param name="value">The value to store; copied into the slot.</param>
    /// <param name="customText">The display text; default means none.</param>
    /// <param name="limit">Slot limit of this insert.</param>
    /// <returns>Storage index of the new field; <see cref="FieldBody.SkipStorageIndex"/> in Skip mode.</returns>
    /// <exception cref="FieldAppendException">The limit is reached, or an insert-after anchor has no parent.</exception>
    /// <remarks>
    /// Thread safety: the parse thread before the seal; any number of lazy materializers after it (slot reservation and in-order
    /// publication make them safe). A value cache or observer exception propagates after the tombstone is published.
    /// </remarks>
    private ushort _InsertField(InsertKind kind, ushort anchorIndex, FieldId fieldId, in FieldValue value, LazyString customText, int limit)
    {
        // Skip mode stores nothing; hooks still see the value (and the flag keeps the common no-hook parse cheap).
        if (!_HasFieldTree)
        {
            if (_HasParseHooks)
            {
                _RecordValueCache(fieldId, in value, in customText);
                // No slab slot exists in a tree-free parse; the argument is the only location of the text.
                _ObserveProducedField(fieldId, in value, in customText, FieldBody.SkipStorageIndex, hasField: false);
            }

            return FieldBody.SkipStorageIndex;
        }

        // Parent: the anchor itself, or the sibling's parent for insert-after (validated before any reservation).
        ushort parentIndex = kind == InsertKind.After
            ? _ParentForInsertAfter(anchorIndex)
            : anchorIndex;

        // Seal cannot change during this call: pre-Seal only the parse thread inserts and seals afterwards;
        // post-Seal stays post-Seal until an exclusive recycle. So one read serves reserve, ensure, and publish.
        bool postSeal = _Finalized != 0;
        int reservedIndex = _ReserveFieldSlot(limit, postSeal);
        ushort newIndex = (ushort)reservedIndex;
        bool published = false;
        try
        {
            _Fields.EnsureSlot(reservedIndex, postSeal);
            ref FieldBody created = ref _Fields.Slot(reservedIndex);
            created = new FieldBody(fieldId, value, customText, parentIndex);

            // Cache first, links second. The record can throw (an abandoned cache, a deferred representation whose formatter
            // throws). No link points at the slot yet, so the tombstone that the finally publishes is an unlinked slot: tree
            // walkers never reach it, flat readers see a dead slot. The cache gets the slab slot of the value and the text, not
            // the argument copies (see the comment at the observer call below).
            // Invariant: created refers into a slab chunk, and chunks never move, so it is still this field's slot here.
            if (_HasParseHooks)
            {
                _RecordValueCache(fieldId, in created.Value, in created.CustomText);
            }

            // From here to the publish nothing throws: slot accesses of validated indexes, one counter, the publish itself.
            ref FieldBody parent = ref _Fields.Slot(parentIndex);
            switch (kind)
            {
                case InsertKind.Append:
                {
                    // New last child. The previous last child points forward to it, it points back; an empty parent gets
                    // both ends. The child is linked in before the count is published (see the method summary).
                    ushort last = parent.LastChildIndex;
                    if (last == FieldBody.NullIndex)
                    {
                        parent.FirstChildIndex = newIndex;
                    }
                    else
                    {
                        _Fields.Slot(last).NextIndex = newIndex;
                        created.PrevIndex = last;
                    }

                    parent.LastChildIndex = newIndex;
                    break;
                }
                case InsertKind.Prepend:
                {
                    // New first child: mirror image of Append. An empty parent gets both ends, so FirstChildIndex and
                    // LastChildIndex are never out of step with ChildCount.
                    ushort first = parent.FirstChildIndex;
                    if (first == FieldBody.NullIndex)
                    {
                        parent.LastChildIndex = newIndex;
                    }
                    else
                    {
                        _Fields.Slot(first).PrevIndex = newIndex;
                        created.NextIndex = first;
                    }

                    parent.FirstChildIndex = newIndex;
                    break;
                }
                default:
                {
                    // Insert after a sibling: splice between it and its old next sibling; the new field becomes the
                    // parent's last child when the sibling was the last one.
                    ref FieldBody sibling = ref _Fields.Slot(anchorIndex);
                    ushort next = sibling.NextIndex;
                    sibling.NextIndex = newIndex;
                    created.PrevIndex = anchorIndex;
                    created.NextIndex = next;
                    if (next != FieldBody.NullIndex)
                    {
                        _Fields.Slot(next).PrevIndex = newIndex;
                    }
                    else
                    {
                        parent.LastChildIndex = newIndex;
                    }

                    break;
                }
            }

            parent.IncrementChildCount();

            _PublishFieldCount(reservedIndex + 1, postSeal);
            published = true;

            // Observers run after the publish (they may read the published field). They get the slab slot of the value and the
            // text, not the argument copies: a deferred representation or text that an observer evaluates through them caches
            // its string where the tree, the exporters, and every later reader look. A text the cache keeps for the end of the
            // parse is a copy of the slot's text (see _TryKeepCacheText).
            if (_HasParseHooks)
            {
                _ObserveProducedField(fieldId, in created.Value, in created.CustomText, newIndex, hasField: true);
            }

            return newIndex;
        }
        finally
        {
            if (!published)
            {
                _PublishReservedOrTombstone(reservedIndex, fieldId);
            }
        }
    }

    /// <summary>The root has no siblings; checked before the skip-mode branch, so Build and Skip mode fail alike.</summary>
    /// <param name="siblingIndex">The anchor of an insert-after.</param>
    /// <exception cref="FieldAppendException"><paramref name="siblingIndex"/> is the root (index 0).</exception>
    private static void _ThrowIfInsertAfterRoot(ushort siblingIndex)
    {
        if (siblingIndex == 0)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }
    }

    /// <summary>Parent of <paramref name="siblingIndex"/>; a field without a parent cannot get a sibling.</summary>
    /// <param name="siblingIndex">The anchor of an insert-after.</param>
    /// <returns>Storage index of the parent of the anchor.</returns>
    /// <exception cref="FieldAppendException">The anchor has no parent.</exception>
    private ushort _ParentForInsertAfter(ushort siblingIndex)
    {
        ushort parentIndex = _Fields.Slot(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        return parentIndex;
    }

    #endregion

    #region Observers and value cache hooks

    /// <summary>
    /// The value cache that records the fields of the running parse; <see langword="null"/> when the parse records nothing.
    /// Set and cleared only by <c>_ParseAndSeal</c> (besides the temporary swap of <see cref="RecordCaughtError"/>), so it is
    /// <see langword="null"/> after the seal. Cold parse-only state. Parse-thread only.
    /// </summary>
    private ValueCache? _ActiveValueCache;

    /// <summary>
    /// The observers of the running parse; the memory is the caller's array. Cleared before <c>TryParse</c> returns, including
    /// on failure, and before the first end call of <see cref="_EndObservers"/>.
    /// </summary>
    private ReadOnlyMemory<ParseObserver> _Observers;

    /// <summary>
    /// Set when an observer threw: all further observer calls for this packet are skipped and the packet ends incomplete.
    /// Parse-thread only; reset per parse.
    /// </summary>
    private bool _ObserverFailed;

    /// <summary>
    /// Number of leading entries of <see cref="_Observers"/> whose <c>BeginPacket</c> was attempted (ran or threw).
    /// <c>EndPacket</c> goes to exactly these (minus the None watches, which never get <c>BeginPacket</c>), so every begin has
    /// one end. An <see cref="int"/> instead of a per-observer flag array: <c>BeginPacket</c> runs in order and stops at the
    /// first throw, so a prefix length describes the set without allocating. Parse-thread only, reset per parse.
    /// </summary>
    private int _ObserversBegun;

    /// <summary>
    /// <see langword="true"/> when any observer of this parse has a DisplayText watch. Computed once per parse so
    /// <see cref="WantsCustomText"/> pays one bool test when no observer wants texts (the common case). Parse-thread only.
    /// </summary>
    private bool _ObserversWantDisplayText;

    /// <summary>
    /// Custom texts the active value cache records but cannot take yet. A deferred text can be completed later in the same
    /// parse (SOME/IP variable arrays learn their count after the elements), and the cache evaluates a text when it records
    /// it. Once one text is kept, every later text the cache records is kept too, so the texts of one field reach the cache in
    /// append and change order (FirstOccurrence keeps the first record of a packet). An entry holds a copy: it shares the one
    /// build with the slot (the deferred object remembers its string), and re-reading the slot would record a replaced text
    /// twice. Created on first use because most parses keep none; parse-thread only; recorded by
    /// <see cref="_RecordDeferredCacheTexts"/> when the parse ended.
    /// </summary>
    private List<(FieldId FieldId, LazyString Text)>? _DeferredCacheTexts;

    /// <summary>
    /// <see langword="true"/> while a value cache or an observer may be active for this parse (set and cleared only by
    /// <c>_ParseAndSeal</c>, the only writer of <see cref="_ActiveValueCache"/> and <see cref="_Observers"/> besides
    /// <see cref="RecordCaughtError"/>'s temporary cache swap). False for most parses, so inserts skip both hook checks with
    /// one test. Parse-thread only, like the hooks themselves.
    /// </summary>
    private bool _HasParseHooks;

    /// <summary>
    /// Whether custom display text should be built for <paramref name="fieldId"/>.
    /// True when a field tree will store it, when the attached value cache asks for display text of this field, or when an
    /// observer's <see cref="ParseObserver.DisplayText"/> watch accepts the field.
    /// Callers use it to skip building text nobody reads (a Skip parse without a display-text cache or observer).
    /// </summary>
    /// <param name="fieldId">The field whose custom text the caller is about to build.</param>
    /// <returns>
    /// <see langword="true"/> when the packet stores the text (a tree packet), the value cache records display text of the
    /// field, or an observer of the parse asks for display text of the field; <see langword="false"/> when nobody reads it.
    /// </returns>
    /// <remarks>
    /// The observer test is last and guarded by one bool, so a parse without display-text observers (the common case) pays
    /// one extra predictable branch. After the parse the observers and the value cache are detached, so a lazy populator that
    /// runs after the seal gets <see langword="true"/> for a tree packet and <see langword="false"/> for a Skip packet.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WantsCustomText(FieldId fieldId)
        => _HasFieldTree
            || (_ActiveValueCache?.WantsDisplayText(fieldId) == true)
            || (_ObserversWantDisplayText && _ObserverWantsDisplayText(fieldId));

    /// <summary>Slow part of <see cref="WantsCustomText"/>: scans the observers' DisplayText watches (only when one exists).</summary>
    /// <param name="fieldId">The field whose custom text is asked for.</param>
    /// <returns><see langword="true"/> when the DisplayText watch of any observer accepts <paramref name="fieldId"/>.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool _ObserverWantsDisplayText(FieldId fieldId)
    {
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        for (int i = 0; i < observers.Length; i++)
        {
            if (observers[i].DisplayText.Accepts(fieldId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any observer asks for display text; computed once per parse.</summary>
    /// <param name="observers">The observers of the parse.</param>
    /// <returns><see langword="true"/> when at least one observer has a DisplayText watch other than none.</returns>
    private static bool _AnyDisplayText(ReadOnlySpan<ParseObserver> observers)
    {
        for (int i = 0; i < observers.Length; i++)
        {
            if (!observers[i].DisplayText.IsNone)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A deferred container expands when a watch names it or a field in its index group.
    /// Empty observers and <see cref="FieldWatch.None"/> do not expand.
    /// </summary>
    /// <param name="fieldId">The id of the deferred container.</param>
    /// <returns><see langword="true"/> when an observer's watch needs the children of the container; always false once muted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool _WatchExpands(FieldId fieldId)
    {
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        int count = observers.Length;
        if (count == 0 || _ObserverFailed)
        {
            return false;
        }

        if (count == 1)
        {
            return observers[0].Watch.Expands(Stack, fieldId);
        }

        for (int i = 0; i < count; i++)
        {
            if (observers[i].Watch.Expands(Stack, fieldId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tells each non-none watch that a frame is starting. A throw is stored on the packet and
    /// stops later field reports. The parse itself still returns success.
    /// </summary>
    private void _BeginObservers()
    {
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        int count = observers.Length;
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            _ObserversBegun = 1;
            _BeginOne(observers[0]);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            // Counted before the call: an observer whose BeginPacket throws has begun and gets EndPacket(complete: false).
            _ObserversBegun = i + 1;
            _BeginOne(observers[i]);
            if (_ObserverFailed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Calls <c>EndPacket</c> on every observer whose <c>BeginPacket</c> was attempted, in order, before the packet is sealed.
    /// <c>complete</c> is false when the observers were muted or an exception escaped the dissector boundaries (it was caught by
    /// <c>_ParseAndSeal</c>); a contained dissector error leaves it true. A throwing <c>EndPacket</c> is recorded like
    /// any observer failure and does not stop the remaining end calls: every begun observer must be able to close what it
    /// staged. Observers with a None watch never got <c>BeginPacket</c> and get no end call either. Observers are detached
    /// before the first end call, so fields an end call expands reach the tree and the cache but no observer.
    /// <para>
    /// <b>Why before the seal:</b> observers may still read the packet (the cursor of a tree parse) and evaluate deferred
    /// custom texts while the packet is private to the parse thread; after the seal other threads may read it. <b>Why after
    /// the parse also on failure:</b> a consumer that staged fields needs the end call to discard them; skipping it would leave
    /// a half-staged packet that the next <c>BeginPacket</c> would have to guess about.
    /// </para>
    /// </summary>
    private void _EndObservers()
    {
        if (_ObserversBegun == 0)
        {
            return;
        }

        // Detach the observers before the first end call. An end call may expand lazy fields (a tree read with
        // materialize: true); those appends must not reach OnField of an observer whose packet ended or is ending. The local span
        // keeps the end calls going; _ParseAndSeal resets the remaining observer fields when the parse is over.
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        int begun = _ObserversBegun;
        _Observers = default;
        _ObserversWantDisplayText = false;

        // The flag is read once: a failure inside an end call must not turn the remaining observers' packets "incomplete".
        bool complete = !_ObserverFailed;
        for (int i = 0; i < begun && i < observers.Length; i++)
        {
            ref readonly ParseObserver entry = ref observers[i];
            if (entry.Watch.IsNone)
            {
                continue;
            }

            try
            {
                entry.Observer!.EndPacket(Id, complete); // Invariant: _ThrowIfNullObserver rejected null observers before the parse.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) // Error policy: see MaterializeLazyField.
            {
                // Mute first: the error field recorded below is a produced field and must not be reported to observers that
                // are about to end their packet.
                _ObserverFailed = true;
                RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
            }
        }
    }

    /// <summary>
    /// Calls <c>BeginPacket</c> on one observer unless its watch is none. An observer is user code at a trust boundary:
    /// its exception is caught and stored on the packet (the parse must still succeed), and the failed flag mutes all
    /// further observer calls for this packet.
    /// </summary>
    /// <param name="entry">The observer and its watches.</param>
    private void _BeginOne(in ParseObserver entry)
    {
        if (entry.Watch.IsNone || _ObserverFailed)
        {
            return;
        }

        try
        {
            entry.Observer!.BeginPacket(Id, Timestamp); // Invariant: _ThrowIfNullObserver rejected null observers before the parse.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // Error policy: see MaterializeLazyField.
        {
            _ObserverFailed = true;
            RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
        }
    }

    /// <summary>
    /// Reports one produced field. The synthetic root is never appended through this path.
    /// A throw is stored on the packet and later fields of this packet are not reported.
    /// </summary>
    /// <param name="fieldId">The id of the produced field.</param>
    /// <param name="value">The value as stored (the slab slot's value in a tree parse).</param>
    /// <param name="customText">The custom text as stored; default when none.</param>
    /// <param name="storageIndex">Storage index of the field; <see cref="FieldBody.SkipStorageIndex"/> when no field is stored.</param>
    /// <param name="hasField"><see langword="true"/> when a stored field exists for the cursor of the visit.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ObserveProducedField(
        FieldId fieldId, in FieldValue value, in LazyString customText, ushort storageIndex, bool hasField)
    {
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        int count = observers.Length;
        if (count == 0 || _ObserverFailed)
        {
            return;
        }

        if (count == 1)
        {
            _ObserveOne(observers[0], fieldId, in value, in customText, storageIndex, hasField);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            _ObserveOne(observers[i], fieldId, in value, in customText, storageIndex, hasField);
            if (_ObserverFailed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Delivers one produced field to one observer when its watch accepts the field id. Same containment as
    /// <see cref="_BeginOne"/>: a throwing observer is recorded once and then muted for this packet. In skip mode there is
    /// no stored field, so the visit carries the value only (<paramref name="hasField"/> is false).
    /// </summary>
    /// <param name="entry">The observer and its watches.</param>
    /// <param name="fieldId">The id of the produced field.</param>
    /// <param name="value">The value as stored.</param>
    /// <param name="customText">The custom text as stored; default when none.</param>
    /// <param name="storageIndex">Storage index of the field; used for the cursor when <paramref name="hasField"/> is true.</param>
    /// <param name="hasField"><see langword="true"/> when a stored field exists.</param>
    private void _ObserveOne(
        in ParseObserver entry,
        FieldId fieldId,
        in FieldValue value,
        in LazyString customText,
        ushort storageIndex,
        bool hasField)
    {
        if (_ObserverFailed || !entry.Watch.Accepts(fieldId))
        {
            return;
        }

        Field cursor = hasField
            ? new Field(this, storageIndex, fieldId)
            : default;
        FieldVisit visit = new(Id, fieldId, in value, in customText, cursor, hasField);
        try
        {
            entry.Observer!.OnField(in visit); // Invariant: _ThrowIfNullObserver rejected null observers before the parse.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // Error policy: see MaterializeLazyField.
        {
            _ObserverFailed = true;
            RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
        }
    }

    /// <summary>Records a produced field into the active value cache, if any. Parse-thread only.</summary>
    /// <param name="fieldId">The id of the produced field.</param>
    /// <param name="value">The value to record.</param>
    /// <param name="customText">The custom text to record; default when none.</param>
    /// <exception cref="InvalidOperationException">The cache was abandoned by a restart; the caller publishes a tombstone.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _RecordValueCache(FieldId fieldId, in FieldValue value, in LazyString customText)
    {
        // Unrecorded parse: predicted not-taken. The slow stub is NoInlining so Record / slot
        // probe cannot inflate AppendChild native size (I-cache and inline budget).
        ValueCache? cache = _ActiveValueCache;
        if (cache is not null)
        {
            _RecordValueCacheSlow(this, cache, fieldId, in value, in customText);
        }
    }

    /// <summary>
    /// Records the value of a produced field now and its custom text now or, when <see cref="_TryKeepCacheText"/> keeps it,
    /// when the parse ended.
    /// </summary>
    /// <param name="packet">The packet being parsed.</param>
    /// <param name="cache">The active value cache.</param>
    /// <param name="fieldId">The id of the produced field.</param>
    /// <param name="value">The value to record.</param>
    /// <param name="customText">The custom text; default when none.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _RecordValueCacheSlow(Packet packet, ValueCache cache, FieldId fieldId, in FieldValue value, in LazyString customText)
    {
        // A kept text reaches the cache later; the value goes in now with an absent text, which the cache ignores.
        if (packet._TryKeepCacheText(cache, fieldId, in customText))
        {
            LazyString absent = default;
            cache.Record(packet.Id.Value, packet.Timestamp.AsNanos, fieldId, in value, in absent);
            return;
        }

        cache.Record(packet.Id.Value, packet.Timestamp.AsNanos, fieldId, in value, in customText);
    }

    /// <summary>
    /// Keeps a copy of <paramref name="customText"/> for <see cref="_RecordDeferredCacheTexts"/> when the cache records display
    /// text of <paramref name="fieldId"/> and the text is deferred or an earlier text of this parse is kept already.
    /// Parse-thread only.
    /// </summary>
    /// <param name="cache">The cache of the parse.</param>
    /// <param name="fieldId">The field the text belongs to.</param>
    /// <param name="customText">The text at the append or change; an absent text is never kept (the cache ignores it).</param>
    /// <returns><see langword="true"/> when the text was kept; the caller then must not record it now.</returns>
    /// <remarks>
    /// Perf: the <see cref="ValueCache.WantsDisplayText"/> probe runs last, only for a present text that would be kept, so a field
    /// the cache does not record never enters the list. The list is reused by a recycled packet; no allocation once warm.
    /// </remarks>
    private bool _TryKeepCacheText(ValueCache cache, FieldId fieldId, in LazyString customText)
    {
        List<(FieldId FieldId, LazyString Text)>? kept = _DeferredCacheTexts;
        if (customText.IsNull
            || (customText.RawValue is string && (kept is null || kept.Count == 0))
            || !cache.WantsDisplayText(fieldId))
        {
            return false;
        }

        if (kept is null)
        {
            kept = new(4);
            _DeferredCacheTexts = kept;
        }

        kept.Add((fieldId, customText));
        return true;
    }

    /// <summary>
    /// Records the texts kept by <see cref="_TryKeepCacheText"/>, in the order they were kept, now that the parse completed them.
    /// A text whose formatter throws is recorded as <c>packet.error</c> and skipped; the other texts, the index entry, and the
    /// observers' <c>complete</c> flag are kept.
    /// </summary>
    /// <param name="cache">The cache of the parse.</param>
    private void _RecordDeferredCacheTexts(ValueCache cache)
    {
        List<(FieldId FieldId, LazyString Text)>? kept = _DeferredCacheTexts;
        if (kept is null || kept.Count == 0)
        {
            return;
        }

        // Invariant: RecordCaughtError below appends a field without custom text, so it never adds to this list while the loop
        // runs (_TryKeepCacheText keeps only present texts); the span therefore stays the list's backing store.
        Span<(FieldId FieldId, LazyString Text)> entries = CollectionsMarshal.AsSpan(kept);
        for (int i = 0; i < entries.Length; i++)
        {
            // A restart evicts the cache from another thread; every later record would throw the same way, so stop.
            if (cache.IsAbandoned)
            {
                break;
            }

            // The formatter is dissector code. Its throw is contained per text, like a throw at append is contained by the
            // dispatch boundary of PacketProtocol. Rejected: letting it reach _ParseAndSeal's catch, which rolls back the index
            // entry and ends every observer incomplete because of one faulty display text.
            try
            {
                cache.RecordCustomText(Id.Value, Timestamp.AsNanos, entries[i].FieldId, in entries[i].Text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
            }
        }

        kept.Clear();
    }

    /// <summary>
    /// Records a custom-text change into the active value cache, if any. Same inline-stub plus out-of-line-slow-path split
    /// as <see cref="_RecordValueCache"/>, so the unrecorded common case costs one null test.
    /// </summary>
    /// <param name="fieldId">The id of the field whose text changed.</param>
    /// <param name="customText">The text after the change; default after a clear.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _RecordValueCacheCustomText(FieldId fieldId, in LazyString customText)
    {
        ValueCache? cache = _ActiveValueCache;
        if (cache is not null)
        {
            _RecordValueCacheCustomTextSlow(this, cache, fieldId, in customText);
        }
    }

    /// <summary>Records a custom-text change now or, when <see cref="_TryKeepCacheText"/> keeps it, when the parse ended.</summary>
    /// <param name="packet">The packet being parsed.</param>
    /// <param name="cache">The active value cache.</param>
    /// <param name="fieldId">The id of the field whose text changed.</param>
    /// <param name="customText">The text after the change; default after a clear.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _RecordValueCacheCustomTextSlow(Packet packet, ValueCache cache, FieldId fieldId, in LazyString customText)
    {
        // A change follows the same rule as an append, so it lands after the append's text of the same field.
        if (packet._TryKeepCacheText(cache, fieldId, in customText))
        {
            return;
        }

        cache.RecordCustomText(packet.Id.Value, packet.Timestamp.AsNanos, fieldId, in customText);
    }

    #endregion

    #region Custom text changes

    // Custom text is final at append (rule for all three methods below). Field observers and value caches receive a field when
    // it is appended and are not told about a later text change, so a tree-free export would show the text of the append while the
    // field tree shows the changed one. After seal the rule is enforced: a sealed packet is read by other threads, and the text
    // store is a plain, unsynchronized write into the field slab, so a change would race with those readers. Rejected alternative:
    // an interlocked store, which would make every text read pay for a rare, discouraged write. A change BEFORE seal stays
    // possible (a value cache records it), because refusing it would break dissectors that set a text after the append; it
    // is only discouraged.

    /// <summary>
    /// Sets custom display text and records it when a value cache is attached. Discouraged: text should be final at append.
    /// Refused after seal.
    /// </summary>
    /// <param name="index">Storage index of the field.</param>
    /// <param name="fieldId">The id of the field at <paramref name="index"/>; names the field in the error and in the cache record.</param>
    /// <param name="text">The new display text; replaces the current one.</param>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetFieldCustomText(ushort index, FieldId fieldId, LazyString text)
    {
        // One volatile read; the throw helper keeps the cold path out of this inlined method.
        if (_Finalized != 0)
        {
            ThrowHelpers.ThrowCustomTextAfterSeal(fieldId);
        }

        GetFieldRef(index).SetCustomText(text);
        _RecordValueCacheCustomText(fieldId, in GetFieldRef(index).CustomText);
    }

    /// <summary>
    /// Clears custom display text and records the null text when a value cache is attached. Discouraged: text should be final
    /// at append. Refused after seal.
    /// </summary>
    /// <param name="index">Storage index of the field.</param>
    /// <param name="fieldId">The id of the field at <paramref name="index"/>; names the field in the error and in the cache record.</param>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearFieldCustomText(ushort index, FieldId fieldId)
    {
        if (_Finalized != 0)
        {
            ThrowHelpers.ThrowCustomTextAfterSeal(fieldId);
        }

        GetFieldRef(index).ClearCustomText();
        _RecordValueCacheCustomText(fieldId, in GetFieldRef(index).CustomText);
    }

    /// <summary>
    /// Appends to custom display text and records the combined text when a value cache is attached. Discouraged: text should be
    /// final at append. Refused after seal.
    /// </summary>
    /// <param name="index">Storage index of the field.</param>
    /// <param name="fieldId">The id of the field at <paramref name="index"/>; names the field in the error and in the cache record.</param>
    /// <param name="suffix">The deferred text appended to the current display text.</param>
    /// <exception cref="InvalidOperationException">The packet is sealed.</exception>
    internal void AppendFieldCustomText(ushort index, FieldId fieldId, LazyString suffix)
    {
        if (_Finalized != 0)
        {
            ThrowHelpers.ThrowCustomTextAfterSeal(fieldId);
        }

        ref FieldBody body = ref GetFieldRef(index);
        body.AppendCustomText(suffix);
        _RecordValueCacheCustomText(fieldId, in body.CustomText);
    }

    #endregion

    #region Error recording

    /// <summary>
    /// Records a packet-level error as a <c>packet.error</c> child of the root. Does nothing when the stack has no
    /// packet-error field. Uses the regular field limit, so it throws like any append when the tree is full; boundaries
    /// that catch exceptions use <see cref="RecordCaughtError"/> instead.
    /// </summary>
    /// <param name="message">The error text stored in the field.</param>
    /// <exception cref="FieldAppendException">The tree holds the regular maximum of fields.</exception>
    /// <remarks>The error is counted in <see cref="ErrorCount"/> before the append, so the count holds even when it throws.</remarks>
    internal void SetError(string message)
    {
        // Count first: the count must not depend on the tree mode or on the stack having a packet.error field.
        _ = Concurrency.SaturatingInterlocked.Increment(ref _ErrorCount);
        FieldId errorFieldId = Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }

        AppendChild(0, errorFieldId, FieldValue.NewString(message));
    }

    /// <summary>
    /// Records an exception caught at a parse, observer, dispatch, or materialization boundary as a
    /// <c>packet.error</c> child of <paramref name="parentFieldIndex"/>. Unlike <see cref="SetError"/> it does
    /// not throw: the append may use the slot that <see cref="_RegularFieldLimit"/> keeps free, and when the
    /// active value cache fails again (for example because it was abandoned), the error is appended once
    /// more without the cache. A failed attempt leaves an unlinked tombstone, so the tree stays consistent.
    /// When even the reserved slot is used up, the earlier recorded error stands and this one is dropped.
    /// </summary>
    /// <param name="parentFieldIndex">Storage index of the field that receives the error as a child (0 for the root).</param>
    /// <param name="message">The error text stored in the field.</param>
    /// <remarks>Counted once in <see cref="ErrorCount"/>, whichever attempt stores it and even when nothing can be stored.</remarks>
    internal void RecordCaughtError(ushort parentFieldIndex, string message)
    {
        // Counted once at entry, not per attempt: the retry below stores the same error again, it is not a second error.
        _ = Concurrency.SaturatingInterlocked.Increment(ref _ErrorCount);
        FieldId errorFieldId = Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }

        // First attempt: the normal append, including the value cache.
        try
        {
            AppendChild(parentFieldIndex, errorFieldId, FieldValue.NewString(message), _MaxFieldCount);
            return;
        }
        catch (FieldAppendException)
        {
            // The reserved slot is used up; nothing more can be stored.
            return;
        }
        catch (Exception) when (_Finalized == 0 && _ActiveValueCache is not null)
        {
            // The cache failed while recording the error; retry without it below.
        }

        // Second attempt without the cache. Before Seal the parse thread is the only writer of the
        // cache reference, so clearing it for one append is safe.
        ValueCache? cache = _ActiveValueCache;
        _ActiveValueCache = null;
        try
        {
            AppendChild(parentFieldIndex, errorFieldId, FieldValue.NewString(message), _MaxFieldCount);
        }
        catch (FieldAppendException)
        {
            // The first attempt's tombstone took the reserved slot; nothing more can be stored.
        }
        finally
        {
            _ActiveValueCache = cache;
        }
    }

    /// <summary>
    /// Records a parse error as a child field under the specified parent field.
    /// Used by lazy materialization to attach errors directly under the lazy container
    /// rather than at the root packet level.
    /// </summary>
    /// <param name="parentFieldIndex">Storage index of the container that receives the error as a child.</param>
    /// <param name="message">The error text stored in the field.</param>
    /// <exception cref="FieldAppendException">The tree holds the regular maximum of fields.</exception>
    /// <remarks>Counted in <see cref="ErrorCount"/> before the append, so the count holds even when it throws.</remarks>
    internal void SetFieldError(ushort parentFieldIndex, string message)
    {
        _ = Concurrency.SaturatingInterlocked.Increment(ref _ErrorCount);
        FieldId errorFieldId = Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }

        AppendChild(parentFieldIndex, errorFieldId, FieldValue.NewString(message));
    }

    #endregion

    #region Seal

    /// <summary>
    /// Seals the packet, publishing all parsing results for concurrent readers.
    /// After this call, the packet is safe for concurrent reads from multiple threads.
    /// <para>
    /// There is no unused capacity to trim: the chunks are slab slices that a recycle reuses
    /// (<see cref="FieldStorage.ResetForReuse"/>).
    /// </para>
    /// </summary>
    /// <remarks>Idempotent: a sealed packet is left as it is. Parse-thread only; the seal is the publication point for readers.</remarks>
    internal void Seal()
    {
        if (_Finalized != 0)
        {
            return;
        }

        // Release fence via Volatile.Write: ensures all stores from parsing are
        // globally visible before any reader observes _Finalized == 1.
        // On x86-64 this is a plain store (TSO provides release ordering natively).
        // On ARM64 this emits STLR (store-release) — no expensive DSB/DMB barrier.
        _Finalized = 1;

        // Release the gate that PrepareForReuse left held for the duration of a re-parse (no-op for a first parse).
        // It is cleared after _Finalized so a recycle that gets in sees a sealed packet.
        _RecycleGate = 0;
    }

    #endregion

    #region Parse entry points

    // Internal parse pipeline

    /// <summary>
    /// Dispatches to the stack's packet protocol and writes all parsed fields into
    /// <paramref name="packet"/>. An optional <paramref name="context"/> activates
    /// index recording for indexed parses.
    /// </summary>
    /// <param name="packet">The packet that receives the fields; not sealed yet.</param>
    /// <param name="frame">The frame whose bytes are parsed.</param>
    /// <param name="context">The parse context; a default context is replaced by one that only carries the stack.</param>
    private static void _ParseFrameInternal(Packet packet, Frame frame, ParseContext context = default)
    {
        ProtocolId packetProtocolId = packet.Stack.PacketProtocolId;
        if (!packetProtocolId.IsValid)
        {
            return;
        }

        // Ensure the context carries the stack — needed by dispatch methods on MutField.
        // When called without an indexed context (e.g., from _ParseAndSeal), we create a
        // non-indexed context that only carries the stack reference.
        if (!context.HasStack)
        {
            context = new ParseContext(packet.Stack, skipFieldTree: !packet.HasFieldTree);
        }

        MutField rootField = packet.RootFieldMut();
        ParseResult result = packet.Stack.CallProtocol(packetProtocolId, in rootField, frame.Data, in context);
        if (result.TryGetError(out ParseError error))
        {
            packet.SetError(error.ToString());
        }
    }

    /// <summary>
    /// Text recorded in <c>packet.error</c> for a caught exception: the message, or the message, a newline, and the
    /// stack trace when <paramref name="includeStackTrace"/> is set and the runtime provides one. One policy for every
    /// catch boundary (parse, main dispatch in <see cref="Protocols.PacketProtocol"/>, post-parser, observer, lazy
    /// populator), so the same exception yields the same text wherever it is caught.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="includeStackTrace">Whether to append the stack trace (a stack setting, see <c>Stack.IncludeExceptionStackTrace</c>).</param>
    /// <returns>The text for the <c>packet.error</c> field.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string BuildCaughtExceptionText(Exception ex, bool includeStackTrace)
    {
        if (!includeStackTrace || ex.StackTrace is null)
        {
            return ex.Message;
        }

        // One concatenation, one result string. This is an error path (a protocol threw), not a hot path, so the plain
        // string.Concat is the right trade-off: one allocation of exactly the result size. Rejected: formatting through a
        // scratch buffer, which needs a multi-megabyte array for the largest value.
        return string.Concat(ex.Message, "\n", ex.StackTrace);
    }

    // Shared lifecycle helpers

    /// <summary>
    /// Runs parse inside a protocol-exception guard, optionally writes <paramref name="index"/>
    /// and records into <paramref name="cache"/>, then seals the packet. Any protocol exception
    /// is recorded as a field-level error rather than propagating to the caller.
    /// Replays do not write the cache unless <paramref name="recordOnReplay"/> is true (Session PullFill).
    /// </summary>
    /// <param name="packet">The packet to parse into; a fresh or recycled one, not sealed yet.</param>
    /// <param name="frame">The frame to parse.</param>
    /// <param name="index">The packet index to write; <see langword="null"/> for no indexing. Never written by a replay.</param>
    /// <param name="cache">The value cache to record into; <see langword="null"/> for no recording. Must belong to the packet's stack.</param>
    /// <param name="recordOnReplay">Whether a replay (a later parse of an already parsed id) records into <paramref name="cache"/>.</param>
    /// <param name="observers">The observers of this parse; cleared from the packet before the packet is sealed.</param>
    /// <exception cref="ArgumentException"><paramref name="cache"/> belongs to another stack than the packet.</exception>
    /// <exception cref="InvalidOperationException">The packet's stack refused the id (an id jump); the packet is sealed first.</exception>
    private static void _ParseAndSeal(
        Packet packet,
        Frame frame,
        PacketIndex? index,
        ValueCache? cache,
        bool recordOnReplay,
        ReadOnlyMemory<ParseObserver> observers)
    {
        // TryParse already rejected a cache whose stack differs. This throw remains for an
        // internal caller that bypassed TryParse. It is a broken invariant, not a per-frame miss.
        if (cache is not null && !ReferenceEquals(cache.Stack, packet.Stack))
        {
            throw new ArgumentException("ValueCache stack does not match packet stack.", nameof(cache));
        }

        packet._Observers = observers;
        packet._ObserverFailed = false;
        packet._ObserversBegun = 0;
        packet._ObserversWantDisplayText = _AnyDisplayText(observers.Span);
        bool replay = false;
        bool indexing = false;
        try
        {
            // Jump is a caller contract violation, not a parser bug, and must reach the caller.
            // Stay inside the Seal finally so a throw after PrepareForReuse cannot stick
            // _RecycleGate at 1 with _Finalized still 0.
            replay = packet.Stack.ObserveParse(packet.Id);

            // Replays never write the index, including when the first parse did not use one.
            indexing = !replay && index is not null && index.TryBeginPacket(packet.Id.Value);
            bool recording = cache is not null && (!replay || recordOnReplay);
            if (recording)
            {
                packet._ActiveValueCache = cache;
            }

            // One flag for both hooks: false for most parses, so inserts skip the span conversion and the cache load.
            packet._HasParseHooks = recording || !observers.IsEmpty;

            // indexing implies a non-null index; the null test lets the compiler see it.
            ParseContext context = indexing && index is not null
                ? new ParseContext(index, packet.Stack, skipFieldTree: !packet.HasFieldTree, isReplay: replay)
                : new ParseContext(packet.Stack, skipFieldTree: !packet.HasFieldTree, isReplay: replay);

            // True once the completed parse recorded its deferred texts; the end calls may keep more (see the finally).
            bool recordedTexts = false;
            try
            {
                packet._BeginObservers();
                if (!packet._ObserverFailed)
                {
                    _ParseFrameInternal(packet, frame, context);
                }

                if (recording)
                {
                    cache!.EnsureMaterialized(packet); // Invariant: recording is true only when cache is not null.
                    packet._RecordDeferredCacheTexts(cache);
                    recordedTexts = true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) // Error policy: see MaterializeLazyField.
            {
                packet._ObserverFailed = true;
                packet.RecordCaughtError(0, BuildCaughtExceptionText(ex, packet.Stack.IncludeExceptionStackTrace));
                if (indexing)
                {
                    index!.RollbackCurrentPacket(); // Invariant: indexing is true only when index is not null.
                }
            }
            finally
            {
                try
                {
                    // End calls first: the cache is still attached, so a failing end call is recorded where an OnField failure
                    // would be. _BeginObservers runs inside this try, so every begun observer passes through here; observers may
                    // still read the packet (cursor) and evaluate deferred texts while it is private to the parse thread.
                    packet._EndObservers();

                    // An end call that expanded lazy fields may have kept new texts for the cache; record them like the others.
                    // Only after a completed parse: a failed parse records no deferred texts (see the outer finally).
                    if (recordedTexts)
                    {
                        packet._RecordDeferredCacheTexts(cache!); // Invariant: recordedTexts is set only when cache is not null.
                    }
                }
                finally
                {
                    // Invariant: the cache is detached, the index packet is ended, and the first parse is completed on every
                    // path, also if an end call let an unexpected exception through; the next parse of the stack relies on it.
                    packet._ActiveValueCache = null;
                    if (indexing)
                    {
                        index!.EndPacket(); // Invariant: indexing is true only when index is not null.
                    }

                    if (!replay)
                    {
                        packet.Stack.CompleteFirstParse(packet.Id);
                    }
                }
            }
        }
        finally
        {
            packet._Observers = default;
            packet._ObserverFailed = false;
            packet._ObserversBegun = 0;
            packet._ObserversWantDisplayText = false;
            // A parse that failed (its catch above ran) records none of the texts it kept for the cache: its packet is an error
            // packet and the texts may be incomplete. Its values were recorded at append. Clearing also drops the kept copies, so
            // a recycled packet does not retain their deferred objects.
            packet._DeferredCacheTexts?.Clear();
            packet._HasParseHooks = false;
            packet.Seal();
        }
    }

    /// <summary>
    /// Rejects a null observer before the packet is constructed or recycled.
    /// <see cref="FieldWatch.None"/> is a legal watch and is not this check.
    /// </summary>
    /// <param name="options">The parse options whose observer entries are checked.</param>
    /// <exception cref="ArgumentException">An entry of <see cref="ParseOptions.Observers"/> has a null observer.</exception>
    private static void _ThrowIfNullObserver(in ParseOptions options)
    {
        ReadOnlySpan<ParseObserver> observers = options.Observers.Span;
        for (int i = 0; i < observers.Length; i++)
        {
            if (observers[i].Observer is null)
            {
                throw new ArgumentException("A parse observer entry must have an observer.", nameof(options));
            }
        }
    }

    /// <summary>
    /// Checks the preconditions shared by both <c>TryParse</c> overloads and reports the first violation as a
    /// <see cref="ParseFailure"/> code. Expected failures are codes, not exceptions, so a recycle loop over bad input
    /// pays no exception cost. Every check runs before any packet is created or mutated.
    /// </summary>
    /// <param name="stack">The stack of the parse.</param>
    /// <param name="frame">The frame to parse.</param>
    /// <param name="options">The parse options; the field tree mode, cache, and index are checked.</param>
    /// <param name="failure">The first violation; <see cref="ParseFailure.None"/> when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when no precondition is violated.</returns>
    private static bool _TryValidateCommon(
        Stack stack,
        Frame frame,
        in ParseOptions options,
        out ParseFailure failure)
    {
        if (!frame.IsValid)
        {
            failure = ParseFailure.InvalidFrame;
            return false;
        }

        if (!ReferenceEquals(frame.Registry, stack.FrameInterfaceRegistry))
        {
            failure = ParseFailure.RegistryMismatch;
            return false;
        }

        if (options.FieldTree is not FieldTreeMode.Build and not FieldTreeMode.Skip)
        {
            failure = ParseFailure.InvalidFieldTree;
            return false;
        }

        if (options.Cache is not null && !ReferenceEquals(options.Cache.Stack, stack))
        {
            failure = ParseFailure.CacheStackMismatch;
            return false;
        }

        if (options.Index is not null && !ReferenceEquals(options.Index.Stack, stack))
        {
            failure = ParseFailure.IndexStackMismatch;
            return false;
        }

        failure = ParseFailure.None;
        return true;
    }

    /// <summary>
    /// Copies the optional first-protocol choice onto the packet. A recycle already reset the override, so a missing
    /// option leaves the stack default in place.
    /// </summary>
    /// <param name="packet">The packet to configure; fresh or recycled (its override is reset already).</param>
    /// <param name="options">The parse options; <see cref="ParseOptions.FirstProtocol"/> is copied when set.</param>
    private static void _ApplyFirstProtocol(Packet packet, in ParseOptions options)
    {
        if (options.FirstProtocol is ProtocolId protocol)
        {
            packet.FirstProtocolOverride = protocol;
        }
    }

    /// <summary>
    /// Parses <paramref name="frame"/> into a new packet.
    /// A null <paramref name="stack"/> throws <see cref="ArgumentNullException"/> because the stack
    /// is a missing dependency, not a per-frame failure. The caller guarantees a stack instance.
    /// An invalid frame, an invalid packet id, a registry mismatch, a bad cache or index, and an
    /// id jump return false and do not throw. A dissector error or an observer exception is counted in
    /// <see cref="ErrorCount"/> and, when the parse keeps a tree, stored as a <c>packet.error</c> field; this method still
    /// returns true, because the parse ran. <see cref="OutOfMemoryException"/> is never contained; it propagates to the
    /// caller after the packet was sealed.
    /// Thread-safety: see the parse concurrency remarks on <see cref="Packet"/>; a fresh id must be parsed in order.
    /// </summary>
    /// <param name="id">Packet id. Must be valid and not jump past the stack's next first-parse id.</param>
    /// <param name="stack">The protocol stack that owns the packet.</param>
    /// <param name="frame">The captured frame. Its registry must be the stack's frame-interface registry.</param>
    /// <param name="options">Field tree mode, cache, index, first protocol, and observers for this parse.</param>
    /// <param name="packet">The sealed packet on success; null when the method returns false.</param>
    /// <param name="failure">Why nothing was parsed; <see cref="ParseFailure.None"/> on success.</param>
    /// <returns><see langword="true"/> when the frame was parsed; <see langword="false"/> with <paramref name="failure"/> set otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    /// <exception cref="ArgumentException">An entry of <see cref="ParseOptions.Observers"/> has a null observer.</exception>
    public static bool TryParse(
        PacketId id,
        Stack stack,
        Frame frame,
        in ParseOptions options,
        [NotNullWhen(true)] out Packet? packet,
        out ParseFailure failure)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _ThrowIfNullObserver(in options);
        packet = null;
        if (!id.IsValid)
        {
            failure = ParseFailure.InvalidPacketId;
            return false;
        }

        if (!_TryValidateCommon(stack, frame, in options, out failure))
        {
            return false;
        }

        if (stack.WouldJump(id))
        {
            failure = ParseFailure.ParseIdGap;
            return false;
        }

        Packet created = new(id, stack, frame, options.FieldTree);
        _ApplyFirstProtocol(created, in options);
        _ParseAndSeal(created, frame, options.Index, options.Cache, options.RecordOnReplay, options.Observers);
        packet = created;
        failure = ParseFailure.None;
        return true;
    }

    /// <summary>
    /// Parses <paramref name="frame"/> into <paramref name="recycle"/>.
    /// On false, <paramref name="recycle"/> is unchanged when it was non-null.
    /// A null recycle returns <see cref="ParseFailure.NullRecycle"/> and does not throw.
    /// See <see cref="TryParse(PacketId, Stack, Frame, in ParseOptions, out Packet?, out ParseFailure)"/>
    /// for the null-stack rule.
    /// The recycle packet must be sealed and exclusively owned by the calling thread; see the recycling remarks on
    /// <see cref="Packet"/>. A failed recycle (for example a gate held by a materializer) leaves the packet untouched.
    /// Error reporting follows the first overload: <see cref="ErrorCount"/> restarts at zero for this parse, and
    /// <see cref="OutOfMemoryException"/> is never contained.
    /// </summary>
    /// <param name="recycle">
    /// A sealed packet whose storage is reused for this parse. It may belong to another stack as long as that stack uses the
    /// same <see cref="FrameInterfaceRegistry"/> as <paramref name="stack"/>; it is then rebound to <paramref name="stack"/>
    /// (see <see cref="Stack"/>). A packet of a stack with another registry is refused with <see cref="ParseFailure.StackMismatch"/>.
    /// </param>
    /// <param name="id">Packet id. Must be valid and not jump past the stack's next first-parse id.</param>
    /// <param name="stack">The protocol stack of this parse; the packet belongs to it afterwards.</param>
    /// <param name="frame">The captured frame. Its registry must be the stack's frame-interface registry.</param>
    /// <param name="options">Field tree mode, cache, index, first protocol, and observers for this parse.</param>
    /// <param name="failure">Why nothing was parsed; <see cref="ParseFailure.None"/> on success.</param>
    /// <returns><see langword="true"/> when the frame was parsed into <paramref name="recycle"/>; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is null.</exception>
    /// <exception cref="ArgumentException">An entry of <see cref="ParseOptions.Observers"/> has a null observer.</exception>
    public static bool TryParse(
        Packet recycle,
        PacketId id,
        Stack stack,
        Frame frame,
        in ParseOptions options,
        out ParseFailure failure)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _ThrowIfNullObserver(in options);
        if (recycle is null)
        {
            failure = ParseFailure.NullRecycle;
            return false;
        }

        // The recycle packet may belong to another stack as long as that stack uses the same frame registry (a session restart
        // replaces the stack and keeps the registry): PrepareForReuse rebinds the packet then. A stack of another registry is
        // refused here, first and cheaply, so the answer for a packet of a
        // foreign registry does not depend on the id or the frame. Why by registry and not by instance: ids and the interface
        // ids inside a frame are registry-relative, so the registry is the real compatibility boundary.
        if (!ReferenceEquals(recycle.Stack, stack)
            && !ReferenceEquals(recycle.Stack.FrameInterfaceRegistry, stack.FrameInterfaceRegistry))
        {
            failure = ParseFailure.StackMismatch;
            return false;
        }

        if (!id.IsValid)
        {
            failure = ParseFailure.InvalidPacketId;
            return false;
        }

        if (!_TryValidateCommon(stack, frame, in options, out failure))
        {
            return false;
        }

        if (stack.WouldJump(id))
        {
            failure = ParseFailure.ParseIdGap;
            return false;
        }

        RecycleError? prepared = recycle.PrepareForReuse(id, stack, frame, options.FieldTree);
        if (prepared is RecycleError recycleError)
        {
            failure = recycleError switch
            {
                RecycleError.NotFinalized => ParseFailure.NotFinalized,
                RecycleError.MaterializerActive => ParseFailure.MaterializerActive,
                RecycleError.RegistryMismatch => ParseFailure.RegistryMismatch,
                RecycleError.StackMismatch => ParseFailure.StackMismatch,
                _ => ParseFailure.NotFinalized,
            };
            return false;
        }

        _ApplyFirstProtocol(recycle, in options);
        _ParseAndSeal(recycle, frame, options.Index, options.Cache, options.RecordOnReplay, options.Observers);
        failure = ParseFailure.None;
        return true;
    }

    #endregion
}
