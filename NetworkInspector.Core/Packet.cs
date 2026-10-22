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
/// </para>
/// <para>
/// <b>Recycling:</b> For high-throughput loops over packets owned by one thread (initial trace
/// scans, profiling, per-listener re-parsing), use the <c>TryParse(Packet recycle, …)</c>
/// overloads to reuse an existing, sealed Packet object instead of allocating a new one. The
/// internal slab storage is cleared and reused in place, eliminating the heap allocation and its
/// associated GC pressure entirely. The <see cref="PrepareForReuse"/> method performs this reset.
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
    #region Constants & Fields

    private const int _MaxFieldCount = ushort.MaxValue - 1;

    /// <summary>
    /// Field limit for every append except a recorded caught exception. The last slot below
    /// <see cref="_MaxFieldCount"/> stays free for <see cref="RecordCaughtError"/>, so a parse that fills the
    /// tree still records the resulting <c>FieldAppendException</c> instead of throwing out of <c>TryParse</c>.
    /// </summary>
    private const int _RegularFieldLimit = _MaxFieldCount - 1;

    /// <summary>Number of lazy populator slots per initial allocation (covers typical protocol stacks).</summary>
    private const int _LazyPopulatorChunkSize = 8;

    /// <summary>Default slab capacity for lazy populators: 512 × 8 B = 4 KB.</summary>
    private const int _LazyPopulatorSlabCapacity = 512;

    // Id, Timestamp, Frame and FrameSourceId use private set to support PrepareForReuse().
    // Stack is get-only: recycling requires the same stack (validated in PrepareForReuse).
    // Per-thread bump allocator: multiple packets share a single backing array.
    // When a slab is full, a new one is created; the old stays alive via consumer references.
    [ThreadStatic]
    private static SlabAllocator<LazyPopulator>? _LazyPopulatorSlab;

    // Field slots: embedded struct, so a field access needs no extra object hop. Build mode only; a Skip-only
    // packet never initializes it.
    private FieldStorage _Fields;

    // Not volatile: parse of a given instance is single-threaded. Skip mode omits the FieldBody slab.
    private bool _HasFieldTree = true;

    // Synthetic root when Skip omits the field storage. Unused on Build.
    private FieldBody _SkipRootBody;

    // Scratch stack for skip-tree lazy populators that must read container.Value. Recycled in place.
    private FieldBody[]? _SkipScratchStack;
    private int _SkipScratchDepth;

    private ReadOnlyMemory<byte>[]? _AdditionalBuffers;
    private int _AdditionalBufferCount;

    // LazyPopulator storage: slab-backed like FieldBody. Published as one object so
    // concurrent nested AppendLazy cannot tear (array, offset) on growth.
    private volatile LazyPopulatorTable? _LazyTable;
    private volatile int _LazyPopulatorCount;
    private volatile int _AllocatedLazyCount;

    // Tracks how many lazy populators have not yet been invoked (int for Volatile compatibility).
    // Incremented by RegisterLazyPopulator, decremented by MaterializeLazyField.
    // Enables O(1) HasUnpopulatedLazyFields and fast-exit in all materialization paths.
    // Always mutated via Interlocked; Volatile.Read used for fast-path checks.
    private volatile int _PendingLazyCount;

    // Tracks in-flight lazy materializations (Interlocked) for PrepareForReuse guard.
    private volatile int _ActiveLazyMaterializations;

    // Reader-visible field count. Readers (FieldCount, TryGetFieldAt, enumerators) use this.
    // Uses int for Volatile.Read/Write compatibility (ushort has no overload).
    // Actual range is [0, _MaxFieldCount]. Published only after parent/sibling linked-list stores.
    private volatile int _FieldCount;

    // Reservation watermark: unique FieldBody slot indexes for concurrent post-Seal appends.
    // Distinct from _FieldCount so Interlocked.Increment does not publish a slot to readers
    // before parent/sibling pointers are linked. Always >= _FieldCount.
    private volatile int _AllocatedFieldCount;

    // 0 = not finalized, 1 = finalized.
    // Volatile release store in Seal; PrepareForReuse stores 0 after _RecycleGate is held.
    private volatile int _Finalized;

    // 0 = idle/readable, 1 = recycle in progress. Exclusive recycle vs concurrent MaterializeAll.
    private volatile int _RecycleGate;

    // Side-channel info LazyString set by sub-protocols during Parse().
    // Used as the source for the lazy packet.info field value. After PacketProtocol.Parse
    // creates the packet.info field and calls SetInfoFieldIndex(), InfoFieldIndex is valid
    // and Packet.Info reads directly from the boxed LazyString field for in-heap caching.

    #endregion

    #region Constructors
    /// <summary>
    /// Creates a packet from a captured frame.
    /// <see cref="FrameSourceId"/> is derived from the frame's <see cref="FrameInterfaceId"/>
    /// via the stack's <see cref="FrameInterfaceRegistry"/>.
    /// The frame's registry must match the stack's registry
    /// (validated via reference equality for maximum efficiency on the hot path).
    /// </summary>
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
    /// The <see cref="Stack"/> is not a parameter: a recycled packet always belongs to the
    /// same stack as its previous parse. Passing a frame from a different stack's registry
    /// is detected via <see cref="RecycleError.RegistryMismatch"/>.
    /// </para>
    /// </summary>
    /// <param name="id">New packet identifier.</param>
    /// <param name="frame">New frame to parse. Must share the same <see cref="FrameInterfaceRegistry"/> as this packet's stack.</param>
    /// <param name="fieldTree">Whether the next parse writes FieldBodies. Skip leaves the slab unused.</param>
    /// <returns>
    /// <see langword="null"/> on success; a <see cref="RecycleError"/> value when a
    /// precondition is violated (not finalized, materializer active, or registry mismatch).
    /// </returns>
    internal RecycleError? PrepareForReuse(PacketId id, Frame frame, FieldTreeMode fieldTree)
    {
        // Exclusive recycle vs concurrent MaterializeAll: take the gate first so a materializer
        // that observes the gate cannot claim a field while Array.Clear runs.
        // MaterializeAll and MaterializeLazyField increment _ActiveLazyMaterializations before
        // they re-read the gate. Recycle never plain-stores that counter to 0.
        if (Interlocked.CompareExchange(ref _RecycleGate, 1, 0) != 0)
        {
            return RecycleError.MaterializerActive;
        }

        if (_ActiveLazyMaterializations != 0 || _Finalized == 0)
        {
            RecycleError err = _Finalized == 0 ? RecycleError.NotFinalized : RecycleError.MaterializerActive;
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

        // Validate registry consistency (same check as the constructor).
        if (!ReferenceEquals(frame.Registry, Stack.FrameInterfaceRegistry))
        {
            _RecycleGate = 0;
            return RecycleError.RegistryMismatch;
        }

        // FieldTreeMode is validated by TryParse before this method; constructor still throws.

        // ── 1. Clear used slots; FieldStorage keeps chunks within its retention policy ──
        // Array.Clear zeroes entire FieldBody structs (FieldValue and LazyString references included) so the GC does
        // not retain stale references, and only the used slots are touched. Skip packets that never built a tree have
        // no storage. After a Skip parse _AllocatedFieldCount is 1: the Build slots were cleared by the recycle that
        // switched to Skip.
        if (_Fields.IsInitialized)
        {
            _Fields.ResetForReuse(_AllocatedFieldCount);
        }

        // ── 2. Clear lazy populator references ───────────────────────────────────────
        if (_LazyTable is not null && _LazyPopulatorCount > 0)
        {
            LazyPopulatorTable lazyTable = _LazyTable;
            Array.Clear(lazyTable.Buffer, lazyTable.Offset, _LazyPopulatorCount);
        }

        _LazyPopulatorCount = 0;
        _AllocatedLazyCount = 0;
        _PendingLazyCount = 0;
        if (_AdditionalBufferCount > 0 && _AdditionalBuffers is not null)
        {
            Array.Clear(_AdditionalBuffers, 0, _AdditionalBufferCount);
            _AdditionalBufferCount = 0;
        }

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

    #region Public Properties
    /// <summary>Unique packet identifier.</summary>
    public PacketId Id { get; private set; }

    /// <summary>Packet timestamp.</summary>
    public Timestamp Timestamp { get; private set; }

    /// <summary>The protocol stack that owns this packet.</summary>
    public Stack Stack { get; }

    /// <summary>The captured frame.</summary>
    public Frame Frame { get; private set; }

    /// <summary>Frame source identifier.</summary>
    public FrameSourceId FrameSourceId { get; private set; }

    /// <summary>Whether the packet has been finalized.</summary>
    public bool IsFinalized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Finalized != 0;
    }

    /// <summary>False when this packet was parsed with <see cref="FieldTreeMode.Skip"/>.</summary>
    public bool HasFieldTree => _HasFieldTree;

    /// <summary>
    /// The packet info/summary string.
    /// <para>
    /// When <see cref="PacketProtocol"/> has finished parsing, reads from the
    /// <c>packet.info</c> field whose value is a boxed <see cref="ZeroAlloc.LazyString"/> —
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
    /// Protocol override for dispatch. When valid, PacketProtocol dispatches here
    /// instead of to <see cref="Stack.FrameProtocolId"/>.
    /// </summary>
    internal ProtocolId FirstProtocolOverride { get; set; } = ProtocolId.Invalid;

    #endregion

    #region Field Storage Access
    /// <summary>
    /// Returns the number of fields currently materialized in the tree.
    /// When <paramref name="materialize"/> is <see langword="true"/>, all pending lazy fields
    /// are materialized first so the returned value reflects the complete field count.
    /// </summary>
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref FieldBody _GetSkipBody(int index)
    {
        if (index == FieldBody.SkipStorageIndex && _SkipScratchDepth > 0)
        {
            FieldBody[] scratch = _SkipScratchStack!;
            return ref scratch[_SkipScratchDepth - 1];
        }

        if (index != 0)
        {
            ThrowHelpers.ThrowSkipFieldTreeValue();
        }

        return ref _SkipRootBody;
    }

    /// <summary>
    /// Spins until <paramref name="index"/> is covered by the reader-visible <see cref="_FieldCount"/>
    /// so a tree walker that observed a volatile child/sibling pointer cannot read a default
    /// <see cref="FieldBody"/> before the matching <see cref="_PublishFieldCount"/>.
    /// </summary>
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

    #region Field Access
    /// <summary>Gets the root of the field tree. Navigate from here with Field parent/child/sibling APIs or iterators.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Field RootField() => new(this, 0);

    /// <summary>
    /// Tries to wrap the field body at a storage index. Returns false if out of range.
    /// Storage indexes are packet-owned; this is not a public navigation API.
    /// External callers use <see cref="RootField"/>, Field tree navigation, or
    /// <see cref="IterFieldsDfs"/> / <see cref="IterFieldsFlat"/>.
    /// </summary>
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal MutField RootFieldMut() => new(this, 0, GetFieldRef(0).FieldId);

    /// <summary>Tries to get a mutable field reference by index. Returns false if out of range.</summary>
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

    #region Buffer Management
    /// <summary>Adds an additional data buffer (e.g., reassembled data). Returns 1-based buffer index.</summary>
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
        return _AdditionalBufferCount;
    }

    /// <summary>
    /// Stores <paramref name="buffer"/> as an additional packet buffer and returns that stored slice.
    /// Nested <see cref="IProtocol.Parse"/> calls must use this slice so
    /// <see cref="TryGetEffectLayerKey"/> can identify the layer.
    /// The additional-buffer cap is a constant, not a setting: 255, because the effect-layer key
    /// uses bits 31–24 (index 0 is the frame, 1..255 are additional). A full table returns
    /// <see cref="ReadOnlyMemory{T}.Empty"/> and does not store.
    /// Reparse of the same packet must bind the same recorded bytes once on the new packet
    /// (recycle clears additional buffers).
    /// After <see cref="Seal"/> additional buffers are frozen: this method returns
    /// <see cref="ReadOnlyMemory{T}.Empty"/> and does not store.
    /// Thread-safety follows the packet parse contract: one writer until Seal.
    /// </summary>
    /// <param name="buffer">Reassembled or otherwise owned bytes that outlive this parse.</param>
    /// <returns>
    /// The stored slice, suitable as the <c>data</c> argument of a nested parse;
    /// empty after Seal or when the 255 additional-buffer table is full.
    /// </returns>
    internal ReadOnlyMemory<byte> BindParseBuffer(ReadOnlyMemory<byte> buffer)
    {
        if (_Finalized != 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        // 255 additional buffers is the effect-key limit (index 1..255). AddBuffer
        // returns the current count and does not store past that. Empty tells the
        // caller the slice is not a packet buffer.
        int before = _AdditionalBufferCount;
        _ = AddBuffer(buffer);
        if (_AdditionalBufferCount == before)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return buffer;
    }

    /// <summary>
    /// Gets a buffer by index. 0 = frame data, 1+ = additional buffers.
    /// Negative indexes and indexes at or beyond <see cref="BufferCount"/> return <see langword="null"/>.
    /// </summary>
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
    /// passed to <see cref="BindParseBuffer"/> — the <c>data</c> argument of
    /// <see cref="IProtocol.Parse"/>, not a heap copy.
    /// Buffers are scanned from index 0 (the frame) upward; the first match wins.
    /// A slice of <see cref="Frame.Data"/> that was also passed to
    /// <see cref="BindParseBuffer"/> therefore packs as the frame, not as that additional buffer.
    /// Nested parsers of reassembled bytes must see a memory that is not a frame slice
    /// (pass an owned copy into <see cref="BindParseBuffer"/>).
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

    /// <summary>Test helper for packed-key overflow cases.</summary>
    internal static int PackEffectLayerKeyForTests(int bufferIndex, int offset) =>
        _PackEffectLayerKey(bufferIndex, offset);

    private static int _PackEffectLayerKey(int bufferIndex, int offset)
    {
        if ((uint)bufferIndex > 0xFFu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferIndex),
                "Effect layer key supports buffer index 0..255.");
        }

        if ((uint)offset > 0xFFFFFFu)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Effect layer key supports offset 0..16777215.");
        }

        return (bufferIndex << 24) | offset;
    }

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

    #region Lazy Field Support
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
    /// </summary>
    internal void RegisterLazyPopulator(
        FieldId fieldId, ushort fieldIndex, in FieldValue value, LazyString customText, LazyPopulator populator)
    {
        if (!_HasFieldTree)
        {
            bool cacheWants = _ActiveValueCache is not null && _ActiveValueCache.ShouldMaterialize(fieldId);
            bool watchWants = _WatchExpands(fieldId);
            if (!cacheWants && !watchWants)
            {
                return;
            }

            if (_SkipScratchDepth >= _MaxMaterializationDepth)
            {
                SetFieldError(
                    FieldBody.SkipStorageIndex,
                    "Skip-tree lazy populator exceeded materialization depth 128.");
                return;
            }

            _PushSkipScratch(new FieldBody(fieldId, value));
            if (!customText.IsNull)
            {
                _SkipScratchStack![_SkipScratchDepth - 1].SetCustomText(customText);
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
            LazyPopulatorTable lazyTable = _LazyTable!;
            lazyTable.Buffer[lazyTable.Offset + slot] = populator;
            if (ReferenceEquals(lazyTable, _LazyTable))
            {
                break;
            }
        }

        ref FieldBody target = ref GetFieldRef(fieldIndex);
        if ((target.ReadLazyIndexVolatile() & FieldBody.LazyIndexMaterializingBit) != 0)
        {
            SetFieldError(fieldIndex, "Lazy populator must not re-register the container being materialized.");
            return;
        }

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ReserveLazyPopulatorSlot()
    {
        const int maxSlots = ushort.MaxValue - 1;
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
    private void _EnsureLazyCapacity(int slot)
    {
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
                // Stores that landed in the old buffer after CopyTo are invisible in `grown`
                // until this backfill. Writers that already observed the new table retry.
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
    /// <b>Pre-Seal (single-threaded):</b> Uses per-field CAS on <see cref="FieldBody.LazyIndex"/>.
    /// </para>
    /// <para>
    /// <b>Post-Seal (concurrent):</b> Counts the materializer in
    /// <c>_ActiveLazyMaterializations</c> before re-reading the recycle gate, then per-field CAS
    /// so exactly one thread executes each populator; other threads spin until the field's lazy
    /// index clears.
    /// </para>
    /// </summary>
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

                // Another thread is materializing this field — spin until complete.
                if (body.IsLazyMaterializationInProgress())
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

            LazyPopulatorTable? publishedTable = _LazyTable;
            if (publishedTable is not null)
            {
                lazyTable = publishedTable;
            }

            try
            {
                // Extract and clear the populator to allow GC of captured state
                int arrayIndex = lazyTable.Offset + lazyPopulatorIndex - 1;
                LazyPopulator populator = lazyTable.Buffer[arrayIndex];
                lazyTable.Buffer[arrayIndex] = null!;

                MutField containerField = new(this, fieldIndex, GetFieldRef(fieldIndex).FieldId);
                try
                {
                    ParseResult result = populator(in containerField);
                    if (result.TryGetError(out ParseError populateError))
                    {
                        // Attach the error under the lazy container field (not at root)
                        SetFieldError(fieldIndex, $"Lazy field population failed: {populateError}");
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Catch protocol/parser failures (FieldAppendException, NullReferenceException, etc.)
                    // and attach the error under the lazy container field (not at root). OOM must
                    // propagate — catching it would hide a fatal process condition and allocate.
                    RecordCaughtError(fieldIndex, BuildCaughtExceptionText(ex, includeStackTrace: false));
                    return false;
                }
                finally
                {
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
    /// <b>Pre-Seal:</b> Called single-threaded during parse. Uses plain reads (no Volatile)
    /// and relies on <see cref="MaterializeLazyField"/> skipping the CAS guard.
    /// </para>
    /// <para>
    /// <b>Post-Seal:</b> Thread-safe. Multiple threads may call concurrently; each waits
    /// until all lazy fields are fully populated before returning.
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
    /// Pre-Seal materialization: single-threaded, no CAS guard, no Volatile reads in loop.
    /// Delegates to <see cref="MaterializeLazyField"/> which skips the CAS when pre-Seal.
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
                if (_ActiveLazyMaterializations <= 1)
                {
                    // Nested RegisterLazyPopulator leaked a pending count without a live
                    // field materializer (this session holds the remaining 1). Stop rather
                    // than freeze the caller.
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

    #endregion

    #region Packet Info
    /// <summary>
    /// The raw <see cref="LazyString"/> set by sub-protocols during parsing.
    /// Read by <see cref="Protocols.PacketProtocol"/> to create the <c>packet.info</c> field
    /// value (boxed <see cref="ZeroAlloc.LazyString"/>) without evaluating the factory.
    /// </summary>
    internal LazyString InfoLazy { get; set; }

    /// <summary>
    /// Records the storage index of the <c>packet.info</c> FieldBody.
    /// Called by <see cref="Protocols.PacketProtocol"/> after the field is appended so that
    /// subsequent reads of <see cref="Info"/> go through the field's
    /// <see cref="ZeroAlloc.LazyString"/> for in-heap cached lazy evaluation.
    /// </summary>
    internal void SetInfoFieldIndex(ushort index)
    {
        if (!_HasFieldTree)
        {
            return;
        }

        _InfoFieldIndex = index;
    }

    /// <summary>Sets the packet info/summary string.</summary>
    internal void SetInfo(LazyString info) => InfoLazy = info;

    /// <summary>Appends to the packet info/summary string.</summary>
    internal void AppendToInfo(LazyString suffix) => InfoLazy = InfoLazy.Append(suffix);

    /// <summary>Prepends to the packet info/summary string.</summary>
    internal void PrependToInfo(LazyString prefix) => InfoLazy = prefix.Append(InfoLazy);

    // Cold parse-only fields (after hot storage/count state to reduce false sharing in AppendChild).
    private ushort _InfoFieldIndex = FieldBody.NullIndex;
    private ValueCache? _ActiveValueCache;

    // Observer memory is the caller's array. Cleared before TryParse returns, including on failure.
    private ReadOnlyMemory<ParseObserver> _Observers;
    private bool _ObserverFailed;

    // True while a value cache or an observer may be active for this parse (set and cleared only by _ParseAndSeal,
    // the only writer of _ActiveValueCache and _Observers besides RecordCaughtError's temporary cache swap). False for
    // most parses, so inserts skip both hook checks with one test. Parse-thread only, like the hooks themselves.
    private bool _HasParseHooks;

    /// <summary>
    /// Whether custom display text should be built for <paramref name="fieldId"/>.
    /// Step 1: true only when a field tree will store it. Step 2 ORs an attached cache's display probe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WantsCustomText(FieldId fieldId)
        => _HasFieldTree
            || (_ActiveValueCache is { } cache && cache.WantsDisplayText(fieldId));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _ThrowIfInvalidFieldTree(FieldTreeMode fieldTree)
    {
        if (fieldTree is not FieldTreeMode.Build and not FieldTreeMode.Skip)
        {
            ThrowHelpers.ThrowInvalidFieldTreeMode(fieldTree);
        }
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PopSkipScratch()
    {
        _SkipScratchDepth--;
        Array.Clear(_SkipScratchStack!, _SkipScratchDepth, 1);
    }

    #endregion

    #region Field Lookup
    /// <summary>
    /// Maximum number of targeted materialization iterations before giving up.
    /// Protects against infinite loops from buggy populators that recursively
    /// create self-referencing lazy fields.
    /// The limit is set generously to accommodate future deeply-nested protocols.
    /// </summary>
    private const int _MaxMaterializationDepth = 128;

    /// <summary>
    /// Searches the flat field array for a field with the given ID and returns its value.
    /// Convenience wrapper over <see cref="TryGetNextFieldValue"/> that always starts
    /// from the beginning of the field array (first occurrence wins).
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
        return TryGetNextFieldValue(fieldId, ref cookie, out value, materialize);
    }

    /// <summary>
    /// Searches the flat field array for the next occurrence of a field with the given ID,
    /// starting from the position encoded in <paramref name="cookie"/>.
    /// Same scan as <see cref="TryGetNextFieldValue"/>; returns a <see cref="Field"/> so
    /// custom text is readable.
    /// </summary>
    /// <param name="fieldId">The field ID to search for.</param>
    /// <param name="cookie">
    /// Opaque continuation token. Use <see cref="FieldLookupCookie.Start"/> for the first call.
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

            for (int depth = 0; depth < _MaxMaterializationDepth; depth++)
            {
                if (!HasUnpopulatedLazyFields)
                {
                    break;
                }

                count = _FieldCount;
                bool materialized = false;

                for (int i = 0; i < count; i++)
                {
                    ref readonly FieldBody fb = ref GetFieldRef(i);
                    if (!fb.NeedsMaterialization)
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
        }

        cookie.Position = searchedUpTo;
        field = default;
        return false;
    }

    /// <summary>
    /// Searches the flat field array for the next occurrence of a field with the given ID,
    /// starting from the position encoded in <paramref name="cookie"/>.
    /// Returns each occurrence on successive calls, enabling iteration over multi-occurrence
    /// fields (e.g., tunneled packets with inner and outer IP headers, repeated options).
    ///
    /// <para><b>Cookie semantics:</b> The cookie is an opaque continuation token.
    /// Use <see cref="FieldLookupCookie.Start"/> for the first call. After each successful
    /// call, the cookie is updated so that the next call continues searching after the
    /// found field. After an unsuccessful call, the cookie is parked at the end of the
    /// searched range.</para>
    ///
    /// <para><b>Usage pattern:</b></para>
    /// <code>
    /// FieldLookupCookie cookie = FieldLookupCookie.Start;
    /// while (packet.TryGetNextFieldValue(ipSrcFieldId, ref cookie, out FieldValue value, materialize: false))
    /// {
    ///     // Process each occurrence of ip.src (e.g., outer and inner tunnel headers)
    /// }
    /// // Pass materialize: true when the lookup must wait for lazy containers.
    /// </code>
    ///
    /// <para><b>Lazy materialization (when <paramref name="materialize"/> is true):</b>
    /// If the field is not found among eagerly-populated fields and lazy containers
    /// exist, performs targeted lazy materialization: only the owning protocol's lazy
    /// containers are materialized rather than all pending lazy fields. This makes the
    /// common case (field found eagerly) zero-overhead while still supporting lazy fields
    /// transparently.</para>
    ///
    /// <para><b>Materialization optimization:</b> Materialization always appends new fields
    /// at the end of the flat array. After the initial scan covers positions 0..N, the
    /// method tracks the searched boundary and only scans newly-appended fields after each
    /// materialization step, avoiding redundant re-scans from the beginning.</para>
    ///
    /// <para><b>Recursive lazy support:</b> Lazy populators may register new lazy containers
    /// (e.g., FrameProtocol creates a lazy interface container inside its lazy frame container).
    /// This method handles the recursive case by iterating: after materializing a container,
    /// it re-scans the (now potentially larger) field array for newly-created lazy containers
    /// belonging to the same protocol, continuing until the field is found or no more
    /// matching containers exist.</para>
    ///
    /// <para><b>Algorithm (materialization path):</b></para>
    /// <list type="number">
    ///   <item>Linear scan from cookie position through existing fields.</item>
    ///   <item>If missed: check whether any lazy fields remain (O(1) via pending count).</item>
    ///   <item>Resolve the target FieldId's owning ProtocolId via the stack registry.</item>
    ///   <item>Iteratively scan the field array for a lazy container (LazyIndex &gt; 0)
    ///         belonging to the same protocol, materialize it, and scan only the
    ///         newly-appended fields (searchedUpTo..newCount).</item>
    ///   <item>Stop when the field is found, no more matching containers exist, or the
    ///         safety limit (<see cref="_MaxMaterializationDepth"/>) is reached.</item>
    /// </list>
    /// </summary>
    /// <param name="fieldId">The field ID to search for.</param>
    /// <param name="cookie">
    /// Opaque continuation token. Use <see cref="FieldLookupCookie.Start"/> for the first call.
    /// Updated on return to the position after the found field (on success) or the end of
    /// the searched range (on failure). Do not modify between successive calls.
    /// </param>
    /// <param name="value">Receives the field value if found.</param>
    /// <param name="materialize">
    /// When <see langword="true"/>, triggers targeted lazy materialization if the
    /// field is not found in eagerly-populated fields.
    /// When <see langword="false"/>, only searches already-materialized fields.
    /// </param>
    /// <returns><see langword="true"/> if a (next) occurrence was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetNextFieldValue(FieldId fieldId, ref FieldLookupCookie cookie, out FieldValue value, bool materialize)
    {
        if (!TryGetNextField(fieldId, ref cookie, out Field field, materialize))
        {
            value = FieldValue.None;
            return false;
        }

        value = field.Value;
        return true;
    }

    #endregion

    #region Tree Modification
    /// <summary>
    /// Reserves a unique storage slot index without publishing it to readers.
    /// Pre-Seal uses a plain increment of <see cref="_AllocatedFieldCount"/> (single-threaded parse).
    /// Post-Seal uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> so concurrent
    /// lazy materializers cannot share a slot. Callers write the <see cref="FieldBody"/>, fix
    /// parent/sibling pointers, then publish via <see cref="_PublishFieldCount"/>. Throws
    /// <c>FieldAppendException</c> once <paramref name="limit"/> slots are allocated.
    /// </summary>
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void _PublishReservedOrTombstone(int reservedIndex, FieldId fieldId)
    {
        bool postSeal = _Finalized != 0;
        FieldId tombstoneId = Stack.PacketErrorFieldId.IsValid ? Stack.PacketErrorFieldId : fieldId;
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
    internal ushort AppendChild(ushort parentIndex, FieldId fieldId, in FieldValue value, int limit = _RegularFieldLimit)
        => _InsertField(InsertKind.Append, parentIndex, fieldId, in value, default, limit);

    /// <summary>Appends a child field with custom display text (attached before the field is published).</summary>
    internal ushort AppendChildWithCustomText(ushort parentIndex, FieldId fieldId, in FieldValue value, LazyString customText)
        => _InsertField(InsertKind.Append, parentIndex, fieldId, in value, customText, _RegularFieldLimit);

    /// <summary>Prepends a child field with custom display text (attached before the field is published).</summary>
    internal ushort PrependChildWithCustomText(ushort parentIndex, FieldId fieldId, in FieldValue value, LazyString customText)
        => _InsertField(InsertKind.Prepend, parentIndex, fieldId, in value, customText, _RegularFieldLimit);

    /// <summary>Prepends a child field (inserts before all existing children).</summary>
    internal ushort PrependChild(ushort parentIndex, FieldId fieldId, in FieldValue value)
        => _InsertField(InsertKind.Prepend, parentIndex, fieldId, in value, default, _RegularFieldLimit);

    /// <summary>
    /// Inserts a field after the given sibling with custom display text. The root has no siblings: inserting after
    /// it throws in Build and in Skip mode.
    /// </summary>
    internal ushort InsertAfterWithCustomText(ushort siblingIndex, FieldId fieldId, in FieldValue value, LazyString customText)
    {
        _ThrowIfInsertAfterRoot(siblingIndex);
        return _InsertField(InsertKind.After, siblingIndex, fieldId, in value, customText, _RegularFieldLimit);
    }

    /// <summary>
    /// Inserts a field after the given sibling. The root has no siblings: inserting after it throws in Build and in
    /// Skip mode.
    /// </summary>
    internal ushort InsertAfter(ushort siblingIndex, FieldId fieldId, in FieldValue value)
    {
        _ThrowIfInsertAfterRoot(siblingIndex);
        return _InsertField(InsertKind.After, siblingIndex, fieldId, in value, default, _RegularFieldLimit);
    }

    /// <summary>
    /// Shared insert of every append, prepend, and insert-after variant. A default <paramref name="customText"/>
    /// means "no custom text": the slot starts with default text and the value cache ignores a null text, which is
    /// what the former plain variants did. All parent / sibling linked-list stores happen <b>before</b> the field
    /// count is published, so a concurrent reader after Seal never sees the new count with stale links. A throw
    /// between reserve and publish publishes a tombstone instead.
    /// </summary>
    private ushort _InsertField(InsertKind kind, ushort anchorIndex, FieldId fieldId, in FieldValue value, LazyString customText, int limit)
    {
        // Skip mode stores nothing; hooks still see the value (and the flag keeps the common no-hook parse cheap).
        if (!_HasFieldTree)
        {
            if (_HasParseHooks)
            {
                _RecordValueCache(fieldId, in value, customText);
                _ObserveProducedField(fieldId, in value, FieldBody.SkipStorageIndex, hasField: false);
            }

            return FieldBody.SkipStorageIndex;
        }

        // Parent: the anchor itself, or the sibling's parent for insert-after (validated before any reservation).
        ushort parentIndex = kind == InsertKind.After ? _ParentForInsertAfter(anchorIndex) : anchorIndex;

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

            // Cache before publish, observers after publish (they may read the published field).
            if (_HasParseHooks)
            {
                _RecordValueCache(fieldId, in value, customText);
            }

            _PublishFieldCount(reservedIndex + 1, postSeal);
            published = true;
            if (_HasParseHooks)
            {
                _ObserveProducedField(fieldId, in value, newIndex, hasField: true);
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

    /// <summary>The root has no siblings; checked before the skip-mode branch, as every insert-after variant did.</summary>
    private static void _ThrowIfInsertAfterRoot(ushort siblingIndex)
    {
        if (siblingIndex == 0)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }
    }

    /// <summary>Parent of <paramref name="siblingIndex"/>; a field without a parent cannot get a sibling.</summary>
    private ushort _ParentForInsertAfter(ushort siblingIndex)
    {
        ushort parentIndex = _Fields.Slot(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        return parentIndex;
    }

    /// <summary>
    /// A deferred container expands when a watch names it or a field in its index group.
    /// Empty observers and <see cref="FieldWatch.None"/> do not expand.
    /// </summary>
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
            _BeginOne(observers[0]);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            _BeginOne(observers[i]);
            if (_ObserverFailed)
            {
                return;
            }
        }
    }

    private void _BeginOne(in ParseObserver entry)
    {
        if (entry.Watch.IsNone || _ObserverFailed)
        {
            return;
        }

        try
        {
            entry.Observer!.BeginPacket(Id, Timestamp);
        }
        catch (Exception ex)
        {
            _ObserverFailed = true;
            RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
        }
    }

    /// <summary>
    /// Reports one produced field. The synthetic root is never appended through this path.
    /// A throw is stored on the packet and later fields of this packet are not reported.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _ObserveProducedField(FieldId fieldId, in FieldValue value, ushort storageIndex, bool hasField)
    {
        ReadOnlySpan<ParseObserver> observers = _Observers.Span;
        int count = observers.Length;
        if (count == 0 || _ObserverFailed)
        {
            return;
        }

        if (count == 1)
        {
            _ObserveOne(observers[0], fieldId, in value, storageIndex, hasField);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            _ObserveOne(observers[i], fieldId, in value, storageIndex, hasField);
            if (_ObserverFailed)
            {
                return;
            }
        }
    }

    private void _ObserveOne(
        in ParseObserver entry,
        FieldId fieldId,
        in FieldValue value,
        ushort storageIndex,
        bool hasField)
    {
        if (_ObserverFailed || !entry.Watch.Accepts(fieldId))
        {
            return;
        }

        Field cursor = hasField ? new Field(this, storageIndex, fieldId) : default;
        FieldVisit visit = new(Id, fieldId, in value, cursor, hasField);
        try
        {
            entry.Observer!.OnField(in visit);
        }
        catch (Exception ex)
        {
            _ObserverFailed = true;
            RecordCaughtError(0, BuildCaughtExceptionText(ex, Stack.IncludeExceptionStackTrace));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _RecordValueCache(FieldId fieldId, in FieldValue value, LazyString customText)
    {
        // Unrecorded parse: predicted not-taken. The slow stub is NoInlining so Record / slot
        // probe cannot inflate AppendChild native size (I-cache and inline budget).
        ValueCache? cache = _ActiveValueCache;
        if (cache is not null)
        {
            _RecordValueCacheSlow(this, cache, fieldId, in value, customText);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _RecordValueCacheSlow(
        Packet packet, ValueCache cache, FieldId fieldId, in FieldValue value, LazyString customText) =>
        cache.Record(packet.Id.Value, packet.Timestamp.AsNanos, fieldId, in value, customText);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _RecordValueCacheCustomText(FieldId fieldId, LazyString customText)
    {
        ValueCache? cache = _ActiveValueCache;
        if (cache is not null)
        {
            _RecordValueCacheCustomTextSlow(this, cache, fieldId, customText);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void _RecordValueCacheCustomTextSlow(
        Packet packet, ValueCache cache, FieldId fieldId, LazyString customText) =>
        cache.RecordCustomText(packet.Id.Value, packet.Timestamp.AsNanos, fieldId, customText);

    /// <summary>Sets custom display text and records it when a value cache is attached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetFieldCustomText(ushort index, FieldId fieldId, LazyString text)
    {
        GetFieldRef(index).SetCustomText(text);
        _RecordValueCacheCustomText(fieldId, text);
    }

    /// <summary>Clears custom display text and records the null text when a value cache is attached.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearFieldCustomText(ushort index, FieldId fieldId)
    {
        GetFieldRef(index).ClearCustomText();
        _RecordValueCacheCustomText(fieldId, default);
    }

    /// <summary>Appends to custom display text and records the combined text when a value cache is attached.</summary>
    internal void AppendFieldCustomText(ushort index, FieldId fieldId, LazyString suffix)
    {
        ref FieldBody body = ref GetFieldRef(index);
        body.AppendCustomText(suffix);
        _RecordValueCacheCustomText(fieldId, body.CustomText);
    }

    #endregion

    #region Error Handling
    /// <summary>Records a packet-level error as a field in the tree.</summary>
    internal void SetError(string message)
    {
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
    internal void RecordCaughtError(ushort parentFieldIndex, string message)
    {
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
    internal void SetFieldError(ushort parentFieldIndex, string message)
    {
        FieldId errorFieldId = Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }
        AppendChild(parentFieldIndex, errorFieldId, FieldValue.NewString(message));
    }

    #endregion

    #region Private Helpers
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
    /// </summary>
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

        buffer = null!;
        offset = 0;
        ThrowHelpers.ThrowFieldAppend(ParseError.Custom(
            "packet",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Slab allocation failed after {_MaxSlabGrowAttempts} attempts (requested {count} slots).")));
    }

    /// <summary>
    /// Derives the <see cref="FrameSourceId"/> from a frame's interface ID via the registry.
    /// O(1) array lookup — called once per packet in the constructor.
    /// </summary>
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

    #region Finalization
    /// <summary>
    /// Seals the packet, publishing all parsing results for concurrent readers.
    /// After this call, the packet is safe for concurrent reads from multiple threads.
    /// <para>
    /// There is no unused capacity to trim: the chunks are slab slices that a recycle reuses
    /// (<see cref="FieldStorage.ResetForReuse"/>).
    /// </para>
    /// </summary>
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
        _RecycleGate = 0;
    }

    #endregion

    #region Static Factory Methods

    // ── Internal parse pipeline ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches to the stack's packet protocol and writes all parsed fields into
    /// <paramref name="packet"/>. An optional <paramref name="context"/> activates
    /// index recording for indexed parses.
    /// </summary>
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
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string BuildCaughtExceptionText(Exception ex, bool includeStackTrace)
    {
        if (!includeStackTrace || ex.StackTrace is null)
        {
            return ex.Message;
        }

        // ZeroAlloc: concatenate message + newline + stack trace without intermediate allocations.
        using TempString temp = ZA.String(ex.Message, "\n", ex.StackTrace);
        return temp.ToString();
    }

    // ── Shared lifecycle helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs parse inside a protocol-exception guard, optionally writes <paramref name="index"/>
    /// and records into <paramref name="cache"/>, then seals the packet. Any protocol exception
    /// is recorded as a field-level error rather than propagating to the caller.
    /// Replays do not write the cache unless <paramref name="recordOnReplay"/> is true (Session PullFill).
    /// </summary>
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

            ParseContext context = indexing
                ? new ParseContext(index!, packet.Stack, skipFieldTree: !packet.HasFieldTree)
                : new ParseContext(packet.Stack, skipFieldTree: !packet.HasFieldTree);
            try
            {
                packet._BeginObservers();
                if (!packet._ObserverFailed)
                {
                    _ParseFrameInternal(packet, frame, context);
                }

                if (recording)
                {
                    cache!.EnsureMaterialized(packet);
                }
            }
            catch (Exception ex)
            {
                packet._ObserverFailed = true;
                packet.RecordCaughtError(0, BuildCaughtExceptionText(ex, packet.Stack.IncludeExceptionStackTrace));
                if (indexing)
                {
                    index!.RollbackCurrentPacket();
                }
            }
            finally
            {
                packet._ActiveValueCache = null;
                if (indexing)
                {
                    index!.EndPacket();
                }

                if (!replay)
                {
                    packet.Stack.CompleteFirstParse(packet.Id);
                }
            }
        }
        finally
        {
            packet._Observers = default;
            packet._ObserverFailed = false;
            packet._HasParseHooks = false;
            packet.Seal();
        }
    }

    /// <summary>
    /// Rejects a null observer before the packet is constructed or recycled.
    /// <see cref="FieldWatch.None"/> is a legal watch and is not this check.
    /// </summary>
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
    /// id jump return false and do not throw. A dissector error or a watch exception is stored on
    /// the packet and this method still returns true, because the parse ran.
    /// </summary>
    public static bool TryParse(
        PacketId id,
        Stack stack,
        Frame frame,
        in ParseOptions options,
        out Packet? packet,
        out ParseFailure failure)
    {
        ArgumentNullException.ThrowIfNull(stack);
        _ThrowIfNullObserver(in options);
        packet = null;
        if (!id.IsValid)
        {
            failure = ParseFailure.InvalidPacketId;
            packet = null;
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
    /// </summary>
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

        if (!ReferenceEquals(recycle.Stack, stack))
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

        RecycleError? prepared = recycle.PrepareForReuse(id, frame, options.FieldTree);
        if (prepared is RecycleError recycleError)
        {
            failure = recycleError switch
            {
                RecycleError.NotFinalized => ParseFailure.NotFinalized,
                RecycleError.MaterializerActive => ParseFailure.MaterializerActive,
                RecycleError.RegistryMismatch => ParseFailure.RegistryMismatch,
                RecycleError.StackMismatch => ParseFailure.StackMismatch,
                RecycleError.InvalidFieldTree => ParseFailure.InvalidFieldTree,
                RecycleError.CacheStackMismatch => ParseFailure.CacheStackMismatch,
                RecycleError.ParseIdGap => ParseFailure.ParseIdGap,
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

    #region Iterators
    /// <summary>
    /// Iterates all fields in depth-first pre-order (including root).
    /// When <paramref name="materialize"/> is true, lazy fields are materialized during traversal.
    /// </summary>
    public FieldDfsEnumerable IterFieldsDfs(bool materialize) => new(this, materialize);

    /// <summary>
    /// Iterates all fields in storage order (linear walk over the internal array).
    /// When <paramref name="materialize"/> is true, lazy fields are materialized as encountered.
    /// </summary>
    public FieldFlatEnumerable IterFieldsFlat(bool materialize) => new(this, materialize);

    #endregion

    #region Nested Types
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
        internal LazyPopulatorTable(LazyPopulator[] buffer, int offset, int capacity)
        {
            Buffer = buffer;
            Offset = offset;
            Capacity = capacity;
        }
    }

    #endregion
}
