// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Represents a parsed packet with a flat field tree.
/// Fields are stored in chunk-based <see cref="FieldBody"/> storage with <see cref="ushort"/> linked-list indices.
/// Each chunk holds <see cref="FieldBodyChunkSize"/> (16) slots. Chunk descriptors are slab-backed
/// via <see cref="SlabAllocator{T}"/> — initially <see cref="InitialChunkDescriptors"/> (4)
/// descriptors (64 fields), doubled on demand.
/// Only 1 FieldBody chunk is allocated upfront; additional chunks are allocated on demand.
/// This means packets that are never materialized consume only 16 slab slots.
/// <para>
/// All per-packet arrays (FieldBody slots, chunk descriptors, lazy populator delegates) are
/// allocated from thread-local <see cref="SlabAllocator{T}"/> instances — a generic slab
/// allocator. Multiple packets share the same backing array; when a slab is full, a new
/// one is created and the old slab stays alive as long as any packet references its buffer.
/// </para>
/// <para>
/// <b>Recycling:</b> For high-throughput single-threaded loops (initial trace scans, profiling),
/// use the <c>ParseFrame(Packet recycle, …)</c> overloads to reuse an existing, sealed Packet
/// object instead of allocating a new one. The internal slab storage is cleared and reused
/// in place, eliminating the heap allocation and its associated GC pressure entirely.
/// The <see cref="PrepareForReuse"/> method performs this reset.
/// </para>
/// <para>
/// <b>Thread-safety contract:</b> After <see cref="Seal"/> completes, a finalized packet is safe
/// for concurrent reads from multiple threads. Lazy field materialization uses a lock-free
/// SpinWait guard (<see cref="_MaterializingFlag"/>) so that exactly one thread populates each
/// lazy field while other threads spin briefly and then see the completed result.
/// All cross-thread visibility is ensured via <see cref="Volatile"/> reads/writes — no locks.
/// </para>
/// </summary>
public sealed class Packet
{
    #region Constants & Fields

    private const int MaxFieldCount = ushort.MaxValue - 1;

    /// <summary>Number of <see cref="FieldBody"/> slots per chunk (must be power of 2).</summary>
    private const int FieldBodyChunkSize = 16;

    /// <summary>Log₂ of <see cref="FieldBodyChunkSize"/> for bitwise division: index >> ChunkShift = chunkIdx.</summary>
    private const int FieldBodyChunkShift = 4;

    /// <summary>Bitmask for modulo <see cref="FieldBodyChunkSize"/>: index &amp; ChunkMask = slotIdx.</summary>
    private const int FieldBodyChunkMask = FieldBodyChunkSize - 1;

    /// <summary>Default slab capacity for <see cref="FieldBody"/> storage: 1024 slots × ~64 B ≈ 64 KB (below LOH).</summary>
    private const int FieldBodySlabCapacity = 1024;

    /// <summary>Number of chunk descriptors allocated per packet initially (4 × 16 = 64 fields).</summary>
    private const int InitialChunkDescriptors = 4;

    /// <summary>Default slab capacity for chunk descriptors: 256 × 12 B ≈ 3 KB.</summary>
    private const int ChunkDescriptorSlabCapacity = 256;

    /// <summary>Number of lazy populator slots per initial allocation (covers typical protocol stacks).</summary>
    private const int LazyPopulatorChunkSize = 8;

    /// <summary>Default slab capacity for lazy populators: 512 × 8 B = 4 KB.</summary>
    private const int LazyPopulatorSlabCapacity = 512;

    // _Id, _Timestamp, _Frame and _FrameSourceId are not readonly to support PrepareForReuse().
    // _Stack is readonly: recycling requires the same stack (validated in PrepareForReuse).
    private PacketId _Id;
    private Timestamp _Timestamp;
    private readonly Stack _Stack;
    private Frame _Frame;
    private FrameSourceId _FrameSourceId;
    // Per-thread bump allocators: multiple packets share a single backing array per type.
    // When a slab is full, a new one is created; the old stays alive via consumer references.
    [ThreadStatic]
    private static SlabAllocator<FieldBody>? _FieldBodySlab;
    [ThreadStatic]
    private static SlabAllocator<LazyPopulator>? _LazyPopulatorSlab;
    [ThreadStatic]
    private static SlabAllocator<FieldBodyChunk>? _ChunkDescriptorSlab;

    // Slab-backed chunk descriptor array. Each entry is a (FieldBody[], int offset)
    // pair pointing into a FieldBody slab. Initially 4 slots (64 fields); grown by
    // doubling from the chunk descriptor slab when capacity is exceeded.
    private FieldBodyChunk[] _Chunks = null!;
    private int _ChunkBaseOffset;
    private int _ChunkCapacity;
    private int _ChunkCount;

    private ReadOnlyMemory<byte>[]? _AdditionalBuffers;
    private int _AdditionalBufferCount;

    // LazyPopulator storage: slab-backed like FieldBody. On first lazy field,
    // allocates LazyPopulatorChunkSize (8) slots from the thread-local SlabAllocator.
    // Growth beyond 8 slots uses Array.Resize (rare).
    private LazyPopulator[]? _LazyPopulators;
    private int _LazyPopulatorOffset;
    private int _LazyPopulatorCapacity;
    private int _LazyPopulatorCount;

    // Tracks how many lazy populators have not yet been invoked (int for Volatile compatibility).
    // Incremented by RegisterLazyPopulator, decremented by MaterializeLazyField.
    // Enables O(1) HasUnpopulatedLazyFields and fast-exit in all materialization paths.
    private int _PendingLazyCount;

    // Lock-free SpinWait guard: 0 = free, 1 = busy.
    // Prevents concurrent materialization from appending duplicate children.
    private int _MaterializingFlag;

    // Field count uses int for Volatile.Read/Write compatibility (ushort has no overload).
    // Actual range is [0, MaxFieldCount].
    private int _FieldCount;

    // 0 = not finalized, 1 = finalized.
    // int sentinel retained for Interlocked pattern consistency — mixing Volatile and Interlocked
    // access styles on the same field is a contract violation; all finalization writes use
    // Interlocked.CompareExchange/Exchange, so the int type keeps all accesses in one contract.
    private int _Finalized;

    // Side-channel info LazyString set by sub-protocols during Parse().
    // Used as the source for the lazy packet.info field value. After PacketProtocol.Parse
    // creates the packet.info field and calls SetInfoFieldIndex(), _InfoFieldIndex is valid
    // and Packet.Info reads directly from the boxed LazyString field for in-heap caching.
    private LazyString _Info;

    // Storage index of the packet.info FieldBody in the flat field array.
    // Set by PacketProtocol after the packet.info field is appended.
    // Sentinel value FieldBody.NullIndex (0xFFFF) means not yet set.
    private ushort _InfoFieldIndex = FieldBody.NullIndex;

    /// <summary>
    /// When set to a valid ID, PacketProtocol dispatches to this protocol
    /// instead of the stack's default frame protocol. Used by ParseFrame overloads
    /// that specify a custom first protocol (e.g. in tests).
    /// </summary>
    private ProtocolId _FirstProtocolOverride = ProtocolId.Invalid;

    #endregion

    #region Constructors
    /// <summary>
    /// Creates a packet from a captured frame.
    /// <see cref="FrameSourceId"/> is derived from the frame's <see cref="FrameInterfaceId"/>
    /// via the stack's <see cref="FrameInterfaceRegistry"/>.
    /// The frame's registry must match the stack's registry
    /// (validated via reference equality for maximum efficiency on the hot path).
    /// </summary>
    internal Packet(PacketId id, Stack stack, Frame frame)
    {
        // Validate registry consistency: reference equality — single pointer comparison
        if (!ReferenceEquals(frame.Registry, stack.FrameInterfaceRegistry))
        {
            ThrowRegistryMismatch();
        }

        _Id = id;
        _Timestamp = frame.Timestamp;
        _Stack = stack;
        _Frame = frame;
        _FrameSourceId = DeriveFrameSourceId(frame.InterfaceId, stack.FrameInterfaceRegistry);
        AllocateFirstChunk();
        ref FieldBodyChunk firstChunk = ref GetChunk(0);
        firstChunk.Buffer[firstChunk.Offset] = new FieldBody(stack.RootFieldId);
        _FieldCount = 1;
        _Info = LazyString.Empty;
        _InfoFieldIndex = FieldBody.NullIndex;
    }

    /// <summary>
    /// Validates preconditions and resets this packet for reuse with a new frame,
    /// eliminating the heap allocation of <c>new Packet(...)</c> on the hot path.
    /// Existing slab-backed storage (FieldBody chunks and lazy populator slots) is
    /// cleared and reused in place; only if the new parse requires more chunks than the
    /// previous parse will additional slab allocations occur.
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
    /// must be in progress (<c>_MaterializingFlag == 0</c>). Call from a single thread only.
    /// </para>
    /// <para>
    /// The <see cref="Stack"/> is not a parameter: a recycled packet always belongs to the
    /// same stack as its previous parse. Passing a frame from a different stack's registry
    /// is detected via <see cref="RecycleError.RegistryMismatch"/>.
    /// </para>
    /// </summary>
    /// <param name="id">New packet identifier.</param>
    /// <param name="frame">New frame to parse. Must share the same <see cref="FrameInterfaceRegistry"/> as this packet's stack.</param>
    /// <returns>
    /// <see langword="null"/> on success; a <see cref="RecycleError"/> value when a
    /// precondition is violated (not finalized, materializer active, or registry mismatch).
    /// </returns>
    internal RecycleError? PrepareForReuse(PacketId id, Frame frame)
    {
        // Precondition: packet must be sealed — recycling an unsealed packet would corrupt
        // an in-progress parse on the same thread.
        if (_Finalized == 0)
        {
            return RecycleError.NotFinalized;
        }

        // Precondition: no concurrent materializer — avoids data corruption.
        if (_MaterializingFlag != 0)
        {
            return RecycleError.MaterializerActive;
        }

        // Validate registry consistency (same check as the constructor).
        if (!ReferenceEquals(frame.Registry, _Stack.FrameInterfaceRegistry))
        {
            return RecycleError.RegistryMismatch;
        }

        // ── 1. Clear GC-visible references in every active FieldBody chunk ──────────
        // Array.Clear zeroes entire FieldBody structs (including FieldValue and LazyString
        // reference fields) so the GC does not retain stale references after reuse.
        // All chunks before the last are always completely full (FieldBodyChunkSize slots).
        // The last chunk is only partially used: its slot count equals (_FieldCount % FieldBodyChunkSize),
        // or FieldBodyChunkSize when the chunk was exactly filled. Clearing only used slots
        // avoids zeroing unused tail slots — typically saves ~25% of Array.Clear work when
        // the last chunk holds ~12 out of 16 used slots (common for IPv6/UDP packets).
        int usedInLastChunk = _FieldCount & FieldBodyChunkMask;
        if (usedInLastChunk == 0)
        {
            usedInLastChunk = FieldBodyChunkSize; // chunk was exactly filled
        }
        for (int i = 0; i < _ChunkCount; i++)
        {
            ref FieldBodyChunk chunk = ref GetChunk(i);
            // Full chunks: all FieldBodyChunkSize slots. Last (partial) chunk: only used slots.
            int clearCount = i < _ChunkCount - 1 ? FieldBodyChunkSize : usedInLastChunk;
            Array.Clear(chunk.Buffer, chunk.Offset, clearCount);
        }

        // ── 2. Clear lazy populator references ───────────────────────────────────────
        // Null out delegate slots to release any closures captured during the previous
        // parse, allowing the GC to collect protocol-specific captured state.
        if (_LazyPopulators is not null && _LazyPopulatorCount > 0)
        {
            Array.Clear(_LazyPopulators, _LazyPopulatorOffset, _LazyPopulatorCount);
        }

        // ── 3. Reset mutable state ────────────────────────────────────────────────────
        // Keep _Chunks, _ChunkBaseOffset, _ChunkCapacity, _LazyPopulators, _LazyPopulatorOffset,
        // _LazyPopulatorCapacity — they are reused as-is for the next parse.
        // Reset only one active chunk; extra chunks from a previous larger parse are abandoned
        // (their slab slots stay alive in the slab but are not reused by this packet).
        _ChunkCount = 1;
        _LazyPopulatorCount = 0;
        _PendingLazyCount = 0;
        _MaterializingFlag = 0;
        _FieldCount = 1;
        _Finalized = 0;  // Re-open the packet for a new parse (no fence needed: single-threaded)
        _Info = LazyString.Empty;
        _InfoFieldIndex = FieldBody.NullIndex;
        _FirstProtocolOverride = ProtocolId.Invalid;

        // Clear additional buffers (reassembly data from the previous parse).
        if (_AdditionalBufferCount > 0 && _AdditionalBuffers is not null)
        {
            Array.Clear(_AdditionalBuffers, 0, _AdditionalBufferCount);
            _AdditionalBufferCount = 0;
        }

        // ── 4. Set new identity fields ────────────────────────────────────────────────
        _Id = id;
        _Timestamp = frame.Timestamp;
        _Frame = frame;
        _FrameSourceId = DeriveFrameSourceId(frame.InterfaceId, _Stack.FrameInterfaceRegistry);

        // ── 5. Re-initialise the root FieldBody in the first slot of the first chunk ─
        ref FieldBodyChunk firstChunk = ref GetChunk(0);
        firstChunk.Buffer[firstChunk.Offset] = new FieldBody(_Stack.RootFieldId);

        return null; // success
    }

    #endregion

    #region Public Properties
    /// <summary>Unique packet identifier.</summary>
    public PacketId Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Id;
    }

    /// <summary>Packet timestamp.</summary>
    public Timestamp Timestamp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Timestamp;
    }

    /// <summary>The protocol stack that owns this packet.</summary>
    public Stack Stack
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Stack;
    }

    /// <summary>The captured frame.</summary>
    public Frame Frame
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _Frame;
    }

    /// <summary>Frame source identifier.</summary>
    public FrameSourceId FrameSourceId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _FrameSourceId;
    }

    /// <summary>Whether the packet has been finalized.</summary>
    public bool IsFinalized
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _Finalized) != 0;
    }

    /// <summary>
    /// The packet info/summary string.
    /// <para>
    /// When <see cref="PacketProtocol"/> has finished parsing, reads from the
    /// <c>packet.info</c> field whose value is a boxed <see cref="ZeroAlloc.LazyString"/> —
    /// the summary string is evaluated and cached in-heap on first access.
    /// Before the field exists (during Parse) falls back to the side-channel
    /// <c>_Info</c> LazyString directly.
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
            return _Info.AsString;
        }
    }

    /// <summary>
    /// Protocol override for dispatch. When valid, PacketProtocol dispatches here
    /// instead of to <see cref="Stack.FrameProtocolId"/>.
    /// </summary>
    internal ProtocolId FirstProtocolOverride => _FirstProtocolOverride;

    #endregion

    #region Field Storage Access
    /// <summary>
    /// Returns the number of fields currently materialized in the tree.
    /// When <paramref name="materialize"/> is <see langword="true"/>, all pending lazy fields
    /// are materialized first so the returned value reflects the complete field count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FieldCount(bool materialize = false)
    {
        if (materialize)
        {
            MaterializeAll();
        }
        // After finalization, volatile read ensures cross-thread visibility.
        // Before finalization, plain read suffices (single-threaded parsing).
        return Volatile.Read(ref _Finalized) != 0 ? Volatile.Read(ref _FieldCount) : _FieldCount;
    }

    /// <summary>
    /// Returns a reference to the chunk descriptor at the given chunk index.
    /// All chunk descriptors are stored in the slab-backed <see cref="_Chunks"/> array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldBodyChunk GetChunk(int chunkIndex) =>
        ref _Chunks[_ChunkBaseOffset + chunkIndex];

    /// <summary>
    /// Returns a mutable reference to the field body at the given logical index.
    /// The logical index is decomposed into chunk index and slot index via bit shifts:
    /// <c>chunkIdx = logicalIndex >> ChunkShift</c>, <c>slotIdx = logicalIndex &amp; ChunkMask</c>.
    /// All stored <see cref="ushort"/> indices in <see cref="FieldBody"/> are logical indices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref FieldBody GetFieldRef(int index)
    {
        int chunkIdx = index >> FieldBodyChunkShift;
        int slotIdx = index & FieldBodyChunkMask;
        ref FieldBodyChunk chunk = ref GetChunk(chunkIdx);
        return ref chunk.Buffer[chunk.Offset + slotIdx];
    }

    #endregion

    #region Field Access
    /// <summary>Gets the root field (always at index 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Field RootField() => new(this, 0);

    /// <summary>Tries to get a field by storage index. Returns false if out of range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetFieldAt(ushort index, out Field field)
    {
        if (index >= Volatile.Read(ref _FieldCount))
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
        if (index >= Volatile.Read(ref _FieldCount))
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

    /// <summary>Gets a buffer by index. 0 = frame data, 1+ = additional buffers.</summary>
    public ReadOnlyMemory<byte>? Buffer(int index)
    {
        if (index == 0)
        {
            return _Frame.Data;
        }
        int additional = index - 1;
        if (_AdditionalBuffers is not null && additional < _AdditionalBufferCount)
        {
            return _AdditionalBuffers[additional];
        }
        return null;
    }

    /// <summary>Total buffer count (1 for frame + additional).</summary>
    public int BufferCount => 1 + _AdditionalBufferCount;

    #endregion

    #region Lazy Field Support
    /// <summary>
    /// Whether any lazy fields exist that have not been populated yet.
    /// O(1) — uses <see cref="System.Threading.Volatile"/> Read for cross-thread visibility.
    /// </summary>
    public bool HasUnpopulatedLazyFields
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _PendingLazyCount) > 0;
    }

    /// <summary>
    /// Registers a lazy populator for the given field index.
    /// <para>
    /// Called during single-threaded parsing (pre-<see cref="Seal"/>) AND from within lazy
    /// populators during materialization (post-Seal). In the post-Seal case, the caller
    /// is always executing under the <see cref="_MaterializingFlag"/> CAS guard, which
    /// serializes all materialization and provides the release fence that publishes these
    /// plain writes to concurrent reader threads. No additional synchronization is needed.
    /// </para>
    /// </summary>
    internal ushort RegisterLazyPopulator(ushort fieldIndex, LazyPopulator populator)
    {
        if (_LazyPopulators is null)
        {
            // First lazy field — allocate from thread-local slab (no per-packet alloc)
            AllocateFromSlab(
                ref _LazyPopulatorSlab, LazyPopulatorSlabCapacity,
                LazyPopulatorChunkSize, out _LazyPopulators, out _LazyPopulatorOffset);
            _LazyPopulatorCapacity = LazyPopulatorChunkSize;
        }
        else if (_LazyPopulatorCount >= _LazyPopulatorCapacity)
        {
            // Growth: rare. Copy from slab slice into a standalone array.
            int newCapacity = _LazyPopulatorCapacity * 2;
            LazyPopulator[] grown = new LazyPopulator[newCapacity];
            _LazyPopulators.AsSpan(_LazyPopulatorOffset, _LazyPopulatorCount).CopyTo(grown);
            _LazyPopulators = grown;
            _LazyPopulatorOffset = 0;
            _LazyPopulatorCapacity = newCapacity;
        }

        _LazyPopulators[_LazyPopulatorOffset + _LazyPopulatorCount] = populator;
        _LazyPopulatorCount++;

        ushort lazyIndex = (ushort)_LazyPopulatorCount; // 1-based
        GetFieldRef(fieldIndex).LazyIndex = lazyIndex;

        // Plain write — during parsing, Seal()'s release fence publishes this.
        // During post-Seal materialization, the _MaterializingFlag guard's release
        // fence (Volatile.Write in MaterializeLazyField's finally block) publishes it.
        _PendingLazyCount++;

        return lazyIndex;
    }

    /// <summary>
    /// Materializes a lazy field's children if not already populated.
    /// <para>
    /// <b>Pre-Seal (single-threaded):</b> Skips the CAS SpinWait guard — no concurrent
    /// access is possible during parse. Uses a plain write to decrement the pending count.
    /// </para>
    /// <para>
    /// <b>Post-Seal (concurrent):</b> Uses a lock-free CAS SpinWait guard to serialize
    /// materialization. Exactly one thread executes the populator; others spin and see
    /// the completed result via the double-check after acquiring the guard.
    /// </para>
    /// </summary>
    internal bool MaterializeLazyField(ushort fieldIndex)
    {
        // Fast path: volatile check — if no pending lazy fields, nothing to do
        if (Volatile.Read(ref _PendingLazyCount) == 0)
        {
            return false;
        }

        // Invariant: _PendingLazyCount > 0 guarantees _LazyPopulators is initialized
        if (_LazyPopulators is null)
        {
            return false;
        }

        // Per-field pre-check before acquiring the guard (cheap filter)
        if (GetFieldRef(fieldIndex).LazyIndex == 0)
        {
            return false;
        }

        // Post-Seal: serialise concurrent access via CAS SpinWait guard (~15-25 cycles
        // uncontended on x86-64). Pre-Seal: skip the guard — single-threaded parse context.
        bool needsGuard = _Finalized != 0;
        if (needsGuard)
        {
            SpinWait spin = default;
            while (Interlocked.CompareExchange(ref _MaterializingFlag, 1, 0) != 0)
            {
                spin.SpinOnce();
            }
        }

        try
        {
            // Double-check after guard — another thread may have materialized this field.
            // In pre-Seal mode this is a plain re-read (re-entry guard for nested populators).
            ushort lazyIndex = GetFieldRef(fieldIndex).LazyIndex;
            if (lazyIndex == 0)
            {
                return false;
            }

            // Mark as materialized first (prevents re-entry by other threads
            // that see this field in their loop — LazyIndex == 0 tells them to skip).
            GetFieldRef(fieldIndex).LazyIndex = 0;

            // Extract and clear the populator to allow GC of captured state
            int arrayIndex = _LazyPopulatorOffset + lazyIndex - 1;
            LazyPopulator populator = _LazyPopulators![arrayIndex];
            _LazyPopulators[arrayIndex] = null!;

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
            catch (Exception ex)
            {
                // Catch all exceptions (FieldAppendException, NullReferenceException, etc.)
                // and attach the error under the lazy container field (not at root)
                SetFieldError(fieldIndex, $"Lazy field materialization failed: {ex.Message}");
                return false;
            }
            finally
            {
                // Decrement pending count AFTER the populator has finished appending all
                // child fields. Post-Seal: Interlocked.Decrement provides both atomicity and
                // the cross-thread visibility needed by concurrent MaterializeAll callers.
                // Pre-Seal: plain write is sufficient — single-threaded, no visibility requirement.
                if (needsGuard)
                {
                    Interlocked.Decrement(ref _PendingLazyCount);
                }
                else
                {
                    _PendingLazyCount--;
                }
            }
            return true;
        }
        finally
        {
            // Release guard — Volatile.Write publishes all mutations to other threads.
            // Pre-Seal: no guard was acquired, so no release is needed.
            if (needsGuard)
            {
                Volatile.Write(ref _MaterializingFlag, 0);
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
        if (Volatile.Read(ref _PendingLazyCount) == 0)
        {
            return;
        }

        // Branch on Seal state: pre-Seal is single-threaded (no Volatile reads needed
        // in the scan loop); post-Seal requires volatile reads and spin-wait for
        // concurrent materialization in progress by other threads.
        if (_Finalized == 0)
        {
            MaterializeAllPreSeal();
        }
        else
        {
            MaterializeAllPostSeal();
        }
    }

    /// <summary>
    /// Pre-Seal materialization: single-threaded, no CAS guard, no Volatile reads in loop.
    /// Delegates to <see cref="MaterializeLazyField"/> which skips the CAS when pre-Seal.
    /// </summary>
    private void MaterializeAllPreSeal()
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

                if (GetFieldRef(i).LazyIndex > 0)
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
    private void MaterializeAllPostSeal()
    {
        // Outer loop handles nested lazy fields: materializing one container may
        // register new lazy containers. Each pass re-reads _FieldCount so newly
        // appended lazy fields are included in the scan.
        while (Volatile.Read(ref _PendingLazyCount) > 0)
        {
            int count = Volatile.Read(ref _FieldCount);
            bool progress = false;

            for (int i = 0; i < count; i++)
            {
                if (Volatile.Read(ref _PendingLazyCount) == 0)
                {
                    break;
                }

                if (GetFieldRef(i).LazyIndex > 0)
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
            if (!progress && Volatile.Read(ref _PendingLazyCount) > 0)
            {
                SpinWait spin = default;
                while (Volatile.Read(ref _PendingLazyCount) > 0
                    && Volatile.Read(ref _FieldCount) == count)
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
    internal LazyString InfoLazy => _Info;

    /// <summary>
    /// Records the storage index of the <c>packet.info</c> FieldBody.
    /// Called by <see cref="Protocols.PacketProtocol"/> after the field is appended so that
    /// subsequent reads of <see cref="Info"/> go through the field's
    /// <see cref="ZeroAlloc.LazyString"/> for in-heap cached lazy evaluation.
    /// </summary>
    internal void SetInfoFieldIndex(ushort index) => _InfoFieldIndex = index;

    /// <summary>Sets the packet info/summary string.</summary>
    internal void SetInfo(LazyString info) => _Info = info;

    /// <summary>Appends to the packet info/summary string.</summary>
    internal void AppendToInfo(LazyString suffix) => _Info = _Info.Append(suffix);

    /// <summary>Prepends to the packet info/summary string.</summary>
    internal void PrependToInfo(LazyString prefix) => _Info = prefix.Append(_Info);

    #endregion

    #region Field Lookup
    /// <summary>
    /// Maximum number of targeted materialization iterations before giving up.
    /// Protects against infinite loops from buggy populators that recursively
    /// create self-referencing lazy fields.
    /// The limit is set generously to accommodate future deeply-nested protocols.
    /// </summary>
    private const int MaxMaterializationDepth = 128;

    /// <summary>
    /// Searches the flat field array for a field with the given ID and returns its value.
    /// Convenience wrapper over <see cref="TryGetNextFieldValue"/> that always starts
    /// from the beginning of the field array (first occurrence wins).
    /// </summary>
    /// <param name="fieldId">The field ID to search for.</param>
    /// <param name="value">Receives the field value if found.</param>
    /// <param name="materialize">
    /// When <see langword="true"/> (default), triggers targeted lazy materialization if the
    /// field is not found in eagerly-populated fields.
    /// When <see langword="false"/>, only searches already-materialized fields.
    /// </param>
    /// <returns><see langword="true"/> if the field was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetFieldValue(FieldId fieldId, out FieldValue value, bool materialize = true)
    {
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        return TryGetNextFieldValue(fieldId, ref cookie, out value, materialize);
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
    /// while (packet.TryGetNextFieldValue(ipSrcFieldId, ref cookie, out FieldValue value))
    /// {
    ///     // Process each occurrence of ip.src (e.g., outer and inner tunnel headers)
    /// }
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
    ///         safety limit (<see cref="MaxMaterializationDepth"/>) is reached.</item>
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
    /// When <see langword="true"/> (default), triggers targeted lazy materialization if the
    /// field is not found in eagerly-populated fields.
    /// When <see langword="false"/>, only searches already-materialized fields.
    /// </param>
    /// <returns><see langword="true"/> if a (next) occurrence was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetNextFieldValue(FieldId fieldId, ref FieldLookupCookie cookie, out FieldValue value, bool materialize = true)
    {
        // Linear scan from cookie position through currently-materialized fields
        int count = Volatile.Read(ref _FieldCount);

        for (int i = cookie.Position; i < count; i++)
        {
            ref readonly FieldBody body = ref GetFieldRef(i);
            if (body.FieldId == fieldId)
            {
                value = body.Value;
                cookie.Position = i + 1; // Next call continues after this match
                return true;
            }
        }

        // All fields up to 'count' have been searched — remember this boundary.
        // Materialization appends beyond this point, so only new fields need scanning.
        int searchedUpTo = count;

        // Miss path: if materialization requested, check for lazy fields that might
        // contain this field. Only a single Volatile.Read on the miss path — the
        // common "field found eagerly" case above has zero overhead.
        if (materialize && Volatile.Read(ref _PendingLazyCount) > 0)
        {
            // Look up the target field's owning protocol via the stack registry
            FieldInfo? targetInfo = _Stack.GetField(fieldId);
            if (targetInfo is null)
            {
                cookie.Position = searchedUpTo;
                value = FieldValue.None;
                return false;
            }

            ProtocolId targetProtocolId = targetInfo.ProtocolId;

            // Iterative materialization loop: handles recursive lazy fields where
            // materializing one container creates new lazy containers for the same protocol.
            // Each iteration materializes at most one container and re-scans only the
            // newly-appended range (searchedUpTo..newCount).
            for (int depth = 0; depth < MaxMaterializationDepth; depth++)
            {
                if (!HasUnpopulatedLazyFields)
                {
                    break;
                }

                // Re-read field array each iteration — materialization may have grown it
                // and appended new lazy containers.
                count = Volatile.Read(ref _FieldCount);
                bool materialized = false;

                for (int i = 0; i < count; i++)
                {
                    ref readonly FieldBody fb = ref GetFieldRef(i);
                    if (fb.LazyIndex == 0)
                    {
                        continue;
                    }

                    // Check if this lazy container belongs to the target protocol
                    FieldInfo? containerInfo = _Stack.GetField(fb.FieldId);
                    if (containerInfo is not null && containerInfo.ProtocolId == targetProtocolId)
                    {
                        // Materialize this specific container only
                        MaterializeLazyField((ushort)i);
                        materialized = true;

                        // Scan only newly-appended fields (searchedUpTo..newCount).
                        // Everything before searchedUpTo was already checked — either
                        // in the initial scan or in a previous materialization iteration.
                        int newCount = Volatile.Read(ref _FieldCount);
                        for (int k = searchedUpTo; k < newCount; k++)
                        {
                            ref readonly FieldBody newBody = ref GetFieldRef(k);
                            if (newBody.FieldId == fieldId)
                            {
                                value = newBody.Value;
                                cookie.Position = k + 1;
                                return true;
                            }
                        }

                        // Update searched boundary for the next iteration
                        searchedUpTo = newCount;

                        // Target field not found yet — the populator may have created new
                        // lazy containers (recursive lazy). Break inner loop and re-scan
                        // the field array which may now contain new entries.
                        break;
                    }
                }

                // No matching lazy container found in this pass — field does not exist
                if (!materialized)
                {
                    break;
                }
            }
        }

        // Park cookie at the end of the searched range so future calls with
        // the same cookie can pick up any fields appended later.
        cookie.Position = searchedUpTo;
        value = FieldValue.None;
        return false;
    }

    #endregion

    #region Tree Modification
    /// <summary>
    /// Writes a new <see cref="FieldBody"/> into the next available slot of the chunk-based
    /// storage and advances <see cref="_FieldCount"/> with a plain store. When the current
    /// chunk is full, allocates a new chunk from the thread-local slab (no copy — previous
    /// chunks remain untouched).
    /// <para>
    /// <b>Thread-safety:</b> This helper performs <i>only</i> plain stores. It deliberately
    /// does <b>not</b> publish <see cref="_FieldCount"/> with a release fence even after
    /// finalization, because a post-<see cref="Seal"/> caller (e.g. <see cref="AppendChild"/>,
    /// <see cref="PrependChild"/>, <see cref="InsertAfter"/>) still has to fix up parent /
    /// sibling linked-list pointers <b>after</b> this call. Publishing the new count too
    /// early would let a concurrent reader observe the incremented count (and therefore the
    /// new slot) while the parent's <c>FirstChildIndex</c> / <c>LastChildIndex</c> /
    /// <c>NextIndex</c> / <c>PrevIndex</c> still point to stale neighbours, breaking
    /// child traversal. Callers post-Seal MUST invoke <see cref="PublishFieldCount"/>
    /// after every parent / sibling write that belongs to the same logical insertion.
    /// During parsing (pre-<see cref="Seal"/>), <see cref="Seal"/>'s release fence publishes
    /// every write, so no per-call publication is needed at all.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddFieldBody(in FieldBody field)
    {
        int count = _FieldCount;
        int chunkIdx = count >> FieldBodyChunkShift;
        int slotIdx = count & FieldBodyChunkMask;

        // Allocate a new chunk if we've exhausted all existing chunks
        if (chunkIdx >= _ChunkCount)
        {
            AllocateNewChunk();
        }

        ref FieldBodyChunk chunk = ref GetChunk(chunkIdx);
        chunk.Buffer[chunk.Offset + slotIdx] = field;

        // Plain advance only — see the remarks above. Post-Seal publication is the
        // caller's responsibility via PublishFieldCount() after parent fix-ups.
        _FieldCount = count + 1;
    }

    /// <summary>
    /// Re-publishes <see cref="_FieldCount"/> with a release fence so that all writes
    /// that preceded this call (including the field body itself and any parent / sibling
    /// linked-list fix-ups) become visible to a concurrent reader before the reader can
    /// observe the new count. No-op pre-Seal because <see cref="Seal"/>'s release fence
    /// will publish everything in bulk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishFieldCount()
    {
        if (_Finalized != 0)
        {
            Volatile.Write(ref _FieldCount, _FieldCount);
        }
    }

    /// <summary>
    /// Appends a child field to the given parent. Performs all parent / sibling linked-list
    /// updates <b>before</b> publishing the new field count, so that a concurrent reader
    /// post-Seal cannot observe the incremented count while parent pointers are still stale.
    /// </summary>
    internal ushort AppendChild(ushort parentIndex, FieldId fieldId, FieldValue value)
    {
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ref FieldBody parent = ref GetFieldRef(parentIndex);
        if (parent.LastChildIndex == FieldBody.NullIndex)
        {
            parent.FirstChildIndex = newIndex;
            parent.LastChildIndex = newIndex;
        }
        else
        {
            ushort lastChild = parent.LastChildIndex;
            GetFieldRef(lastChild).NextIndex = newIndex;
            GetFieldRef(newIndex).PrevIndex = lastChild;
            parent.LastChildIndex = newIndex;
        }
        parent.IncrementChildCount();

        // Publish AFTER parent / sibling fix-ups so a concurrent reader that observes
        // the incremented _FieldCount also sees the consistent linked-list state.
        PublishFieldCount();
        return newIndex;
    }

    /// <summary>
    /// Appends a child field with custom display text. The display text is attached
    /// before the field count is published so concurrent readers that see the new
    /// count also see the populated <c>CustomText</c> slot.
    /// </summary>
    internal ushort AppendChildWithCustomText(
        ushort parentIndex, FieldId fieldId, FieldValue value, LazyString customText)
    {
        // Inline AppendChild so we can attach the custom text BEFORE PublishFieldCount.
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ref FieldBody parent = ref GetFieldRef(parentIndex);
        if (parent.LastChildIndex == FieldBody.NullIndex)
        {
            parent.FirstChildIndex = newIndex;
            parent.LastChildIndex = newIndex;
        }
        else
        {
            ushort lastChild = parent.LastChildIndex;
            GetFieldRef(lastChild).NextIndex = newIndex;
            GetFieldRef(newIndex).PrevIndex = lastChild;
            parent.LastChildIndex = newIndex;
        }
        parent.IncrementChildCount();
        GetFieldRef(newIndex).SetCustomText(customText);

        PublishFieldCount();
        return newIndex;
    }

    /// <summary>
    /// Prepends a child field with custom display text. The display text is attached
    /// before the field count is published so concurrent readers that see the new
    /// count also see the populated <c>CustomText</c> slot.
    /// </summary>
    internal ushort PrependChildWithCustomText(
        ushort parentIndex, FieldId fieldId, FieldValue value, LazyString customText)
    {
        // Inline PrependChild so we can attach the custom text BEFORE PublishFieldCount.
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ref FieldBody parent = ref GetFieldRef(parentIndex);
        if (parent.FirstChildIndex == FieldBody.NullIndex)
        {
            parent.FirstChildIndex = newIndex;
            parent.LastChildIndex = newIndex;
        }
        else
        {
            ushort firstChild = parent.FirstChildIndex;
            GetFieldRef(firstChild).PrevIndex = newIndex;
            GetFieldRef(newIndex).NextIndex = firstChild;
            parent.FirstChildIndex = newIndex;
        }
        parent.IncrementChildCount();
        GetFieldRef(newIndex).SetCustomText(customText);

        PublishFieldCount();
        return newIndex;
    }

    /// <summary>
    /// Prepends a child field (inserts before all existing children). Performs all
    /// parent / sibling linked-list updates <b>before</b> publishing the new field
    /// count, so that a concurrent reader post-Seal cannot observe the incremented
    /// count while parent pointers are still stale.
    /// </summary>
    internal ushort PrependChild(ushort parentIndex, FieldId fieldId, FieldValue value)
    {
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ref FieldBody parent = ref GetFieldRef(parentIndex);
        if (parent.FirstChildIndex == FieldBody.NullIndex)
        {
            parent.FirstChildIndex = newIndex;
            parent.LastChildIndex = newIndex;
        }
        else
        {
            ushort firstChild = parent.FirstChildIndex;
            GetFieldRef(firstChild).PrevIndex = newIndex;
            GetFieldRef(newIndex).NextIndex = firstChild;
            parent.FirstChildIndex = newIndex;
        }
        parent.IncrementChildCount();

        PublishFieldCount();
        return newIndex;
    }

    /// <summary>
    /// Inserts a field after the given sibling with custom display text. The display text
    /// is attached before the field count is published so concurrent readers that see the
    /// new count also see the populated <c>CustomText</c> slot.
    /// </summary>
    internal ushort InsertAfterWithCustomText(
        ushort siblingIndex, FieldId fieldId, FieldValue value, LazyString customText)
    {
        // Inline InsertAfter so we can attach the custom text BEFORE PublishFieldCount.
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort parentIndex = GetFieldRef(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ushort nextSibling = GetFieldRef(siblingIndex).NextIndex;
        GetFieldRef(siblingIndex).NextIndex = newIndex;
        GetFieldRef(newIndex).PrevIndex = siblingIndex;
        GetFieldRef(newIndex).NextIndex = nextSibling;

        if (nextSibling != FieldBody.NullIndex)
        {
            GetFieldRef(nextSibling).PrevIndex = newIndex;
        }
        else
        {
            GetFieldRef(parentIndex).LastChildIndex = newIndex;
        }
        GetFieldRef(parentIndex).IncrementChildCount();
        GetFieldRef(newIndex).SetCustomText(customText);

        PublishFieldCount();
        return newIndex;
    }

    /// <summary>
    /// Inserts a field after the given sibling. Performs all parent / sibling linked-list
    /// updates <b>before</b> publishing the new field count, so that a concurrent reader
    /// post-Seal cannot observe the incremented count while sibling pointers are still stale.
    /// </summary>
    internal ushort InsertAfter(ushort siblingIndex, FieldId fieldId, FieldValue value)
    {
        if (_FieldCount >= MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }

        ushort parentIndex = GetFieldRef(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        ushort newIndex = (ushort)_FieldCount;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        AddFieldBody(in newField);

        ushort nextSibling = GetFieldRef(siblingIndex).NextIndex;
        GetFieldRef(siblingIndex).NextIndex = newIndex;
        GetFieldRef(newIndex).PrevIndex = siblingIndex;
        GetFieldRef(newIndex).NextIndex = nextSibling;

        if (nextSibling != FieldBody.NullIndex)
        {
            GetFieldRef(nextSibling).PrevIndex = newIndex;
        }
        else
        {
            GetFieldRef(parentIndex).LastChildIndex = newIndex;
        }
        GetFieldRef(parentIndex).IncrementChildCount();

        PublishFieldCount();
        return newIndex;
    }

    #endregion

    #region Error Handling
    /// <summary>Records a packet-level error as a field in the tree.</summary>
    internal void SetError(string message)
    {
        FieldId errorFieldId = _Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }
        AppendChild(0, errorFieldId, FieldValue.NewString(message));
    }

    /// <summary>
    /// Records a parse error as a child field under the specified parent field.
    /// Used by lazy materialization to attach errors directly under the lazy container
    /// rather than at the root packet level.
    /// </summary>
    internal void SetFieldError(ushort parentFieldIndex, string message)
    {
        FieldId errorFieldId = _Stack.PacketErrorFieldId;
        if (!errorFieldId.IsValid)
        {
            return;
        }
        AppendChild(parentFieldIndex, errorFieldId, FieldValue.NewString(message));
    }

    #endregion

    #region Private Helpers
    /// <summary>
    /// Allocates the initial chunk descriptor region and the first FieldBody chunk.
    /// <see cref="InitialChunkDescriptors"/> (4) descriptor slots are reserved
    /// from the thread-local chunk descriptor slab, covering up to 64 fields. Only the first
    /// FieldBody chunk (16 slots) is allocated upfront — subsequent chunks are allocated
    /// on demand by <see cref="AddFieldBody"/> when slots fill up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AllocateFirstChunk()
    {
        // Reserve chunk descriptor slots from the chunk descriptor slab
        AllocateFromSlab(
            ref _ChunkDescriptorSlab, ChunkDescriptorSlabCapacity,
            InitialChunkDescriptors, out _Chunks, out _ChunkBaseOffset);
        _ChunkCapacity = InitialChunkDescriptors;

        // Allocate the first FieldBody chunk (16 slots) from the FieldBody slab
        ref FieldBodyChunk first = ref _Chunks[_ChunkBaseOffset];
        AllocateFromSlab(
            ref _FieldBodySlab, FieldBodySlabCapacity,
            FieldBodyChunkSize, out first.Buffer, out first.Offset);
        _ChunkCount = 1;
    }

    /// <summary>
    /// Allocates a single chunk of <see cref="FieldBodyChunkSize"/> slots from the
    /// thread-local FieldBody slab. Creates a new slab if the current one is full.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AllocateFieldBodyChunk(out FieldBody[] buffer, out int offset) =>
        AllocateFromSlab(
            ref _FieldBodySlab, FieldBodySlabCapacity,
            FieldBodyChunkSize, out buffer, out offset);

    /// <summary>
    /// Allocates an additional FieldBody chunk on demand.
    /// If the chunk descriptor array is full, doubles it by allocating a new region
    /// from the chunk descriptor slab and copying existing descriptors (small: count × 12 bytes).
    /// </summary>
    private void AllocateNewChunk()
    {
        // Grow chunk descriptor array if capacity is exhausted
        if (_ChunkCount >= _ChunkCapacity)
        {
            int newCapacity = _ChunkCapacity * 2;
            AllocateFromSlab(
                ref _ChunkDescriptorSlab, ChunkDescriptorSlabCapacity,
                newCapacity, out FieldBodyChunk[] newChunks, out int newOffset);
            // Copy existing descriptors (ChunkCount × 12 bytes — negligible)
            _Chunks.AsSpan(_ChunkBaseOffset, _ChunkCount)
                   .CopyTo(newChunks.AsSpan(newOffset));
            _Chunks = newChunks;
            _ChunkBaseOffset = newOffset;
            _ChunkCapacity = newCapacity;
        }

        // Allocate FieldBody slots for the new chunk from the FieldBody slab
        ref FieldBodyChunk newChunk = ref _Chunks[_ChunkBaseOffset + _ChunkCount];
        AllocateFieldBodyChunk(out newChunk.Buffer, out newChunk.Offset);
        _ChunkCount++;
    }

    /// <summary>
    /// Generic slab allocation helper. Tries to allocate <paramref name="count"/> slots from
    /// the thread-local <see cref="SlabAllocator{T}"/>. If the current slab is full or null,
    /// creates a new slab with <paramref name="slabCapacity"/> and retries.
    /// <para>
    /// This single method replaces all type-specific <c>AllocateFrom*Slab</c> helpers.
    /// The JIT specializes it per <typeparamref name="T"/>, so there is no boxing or
    /// virtual dispatch overhead.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AllocateFromSlab<T>(
        ref SlabAllocator<T>? slab, int slabCapacity,
        int count, out T[] buffer, out int offset)
    {
        if (slab is not null && slab.TryAllocate(count, out buffer, out offset))
        {
            return;
        }

        // Current slab is full or doesn't exist — create a new one.
        // The old slab's backing array stays alive via consumer references.
        slab = new SlabAllocator<T>(slabCapacity);
        slab.TryAllocate(count, out buffer, out offset);
    }

    /// <summary>
    /// Derives the <see cref="FrameSourceId"/> from a frame's interface ID via the registry.
    /// O(1) array lookup — called once per packet in the constructor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FrameSourceId DeriveFrameSourceId(
        FrameInterfaceId interfaceId, FrameInterfaceRegistry registry)
    {
        if (!interfaceId.IsValid)
        {
            return FrameSourceId.Invalid;
        }

        FrameInterfaceInfo? info = registry.Get(interfaceId);
        return info?.SourceId ?? FrameSourceId.Invalid;
    }

    /// <summary>Throws when a frame's registry does not match the stack's registry.</summary>
    /// <exception cref="ArgumentException">Always thrown.</exception>
    [DoesNotReturn]
    private static void ThrowRegistryMismatch()
    {
        throw new ArgumentException(
            "The frame's FrameInterfaceRegistry does not match the stack's registry. " +
            "Frame and stack must share the same FrameInterfaceRegistry instance.");
    }

    /// <summary>
    /// Translates a <see cref="RecycleError"/> code into the appropriate exception and throws it.
    /// Called by the throwing <c>ParseFrame(Packet recycle, …)</c> overloads to convert the
    /// return code from <see cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">For <see cref="RecycleError.NotFinalized"/> and <see cref="RecycleError.MaterializerActive"/>.</exception>
    /// <exception cref="ArgumentException">For <see cref="RecycleError.RegistryMismatch"/> and <see cref="RecycleError.StackMismatch"/>.</exception>
    [DoesNotReturn]
    private static void ThrowRecycleError(RecycleError error)
    {
        throw error switch
        {
            RecycleError.NotFinalized => new InvalidOperationException(
                "Cannot recycle a packet that has not been finalized (sealed). " +
                "Call Seal() before reuse."),
            RecycleError.MaterializerActive => new InvalidOperationException(
                "Cannot recycle a packet while concurrent materialization is in progress. " +
                "Ensure all readers have finished before recycling."),
            RecycleError.RegistryMismatch => new ArgumentException(
                "The frame's FrameInterfaceRegistry does not match the stack's registry. " +
                "Frame and stack must share the same FrameInterfaceRegistry instance."),
            RecycleError.StackMismatch => new ArgumentException(
                "The recycle packet belongs to a different Stack. " +
                "The stack argument must be reference-equal to the recycle packet's stack."),
            _ => new ArgumentException($"Unknown recycle error: {error}"),
        };
    }

    #endregion

    #region Finalization
    /// <summary>
    /// Seals the packet, publishing all parsing results for concurrent readers.
    /// After this call, the packet is safe for concurrent reads from multiple threads.
    /// <para>
    /// With chunk-based storage, there is no large unused capacity to trim.
    /// Each chunk holds exactly <see cref="FieldBodyChunkSize"/> (16) slots, and
    /// at most 15 slots in the last chunk may be unused — negligible overhead.
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
        Volatile.Write(ref _Finalized, 1);
    }

    #endregion

    #region Static Factory Methods

    // ── Internal parse pipeline ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches to the stack's packet protocol and writes all parsed fields into
    /// <paramref name="packet"/>. An optional <paramref name="context"/> activates
    /// index and value-cache recording for indexed parses.
    /// </summary>
    private static void ParseFrameInternal(Packet packet, Frame frame, ParseContext context = default)
    {
        ProtocolId packetProtocolId = packet._Stack.PacketProtocolId;
        if (!packetProtocolId.IsValid)
        {
            return;
        }

        // Ensure the context carries the stack — needed by dispatch methods on MutField.
        // When called without an indexed context (e.g., from ParseAndSeal), we create a
        // non-indexed context that only carries the stack reference.
        if (!context.HasStack)
        {
            context = new ParseContext(packet._Stack);
        }

        MutField rootField = packet.RootFieldMut();
        ParseResult result = packet._Stack.CallProtocol(packetProtocolId, in rootField, frame.Data, in context);
        if (result.TryGetError(out ParseError error))
        {
            packet.SetError(error.ToString());
        }
    }

    /// <summary>
    /// Builds the error message for a caught parser exception.
    /// When <paramref name="includeStackTrace"/> is <see langword="true"/>, appends a newline
    /// and the full stack trace to help diagnose protocol parser bugs.
    /// Uses ZeroAlloc for zero-allocation string building in the stack-trace case.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string BuildExceptionMessage(Exception ex, bool includeStackTrace)
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
    /// Runs <see cref="ParseFrameInternal"/> inside a protocol-exception guard and seals
    /// the packet. Any protocol exception is recorded as a field-level error rather than
    /// propagating to the caller.
    /// </summary>
    private static void ParseAndSeal(Packet packet, Frame frame)
    {
        try
        {
            ParseFrameInternal(packet, frame);
        }
        catch (Exception ex)
        {
            packet.SetError(BuildExceptionMessage(ex, packet._Stack.IncludeExceptionStackTrace));
        }
        packet.Seal();
    }

    /// <summary>
    /// Runs the full indexed-parse lifecycle: begins the packet in the index, builds the
    /// parse context, runs the protocol-exception-guarded parse, ends the packet in the
    /// index, and seals the packet.
    /// </summary>
    private static void ParseIndexedAndSeal(Packet packet, Frame frame, PacketIndex index)
    {
        index.BeginPacket(packet._Id.Value);

        ParseContext context = new(index, packet._Stack);

        try
        {
            ParseFrameInternal(packet, frame, context);
        }
        catch (Exception ex)
        {
            packet.SetError(BuildExceptionMessage(ex, packet._Stack.IncludeExceptionStackTrace));
        }

        index.EndPacket();
        packet.Seal();
    }

    // ── Non-recycling overloads ───────────────────────────────────────────────────────────────────
    //
    // Allocate a fresh packet, parse, seal, return it. Programmer-error preconditions
    // (frame/stack registry mismatch) are validated in the Packet constructor and surface
    // as exceptions — these are not hot-path concerns since allocation already dominates.

    /// <summary>Parses a frame into a new packet, catching exceptions from protocol parsers.</summary>
    public static Packet ParseFrame(PacketId id, Stack stack, Frame frame)
    {
        Packet packet = new(id, stack, frame);
        ParseAndSeal(packet, frame);
        return packet;
    }

    /// <summary>
    /// Parses a frame into a new packet, dispatching to <paramref name="firstProtocolId"/>
    /// instead of the stack's default frame protocol.
    /// </summary>
    public static Packet ParseFrame(PacketId id, Stack stack, Frame frame, ProtocolId firstProtocolId)
    {
        Packet packet = new(id, stack, frame)
        {
            _FirstProtocolOverride = firstProtocolId
        };
        ParseAndSeal(packet, frame);
        return packet;
    }

    /// <summary>Parses a frame into a new packet while recording field presence in the given index.</summary>
    public static Packet ParseFrameIndexed(
        PacketId id, Stack stack, Frame frame, PacketIndex index)
    {
        Packet packet = new(id, stack, frame);
        ParseIndexedAndSeal(packet, frame, index);
        return packet;
    }

    /// <summary>
    /// Parses a frame into a new packet while recording field presence in the given index,
    /// dispatching to <paramref name="firstProtocolId"/> instead of the stack's default frame protocol.
    /// </summary>
    public static Packet ParseFrameIndexed(
        PacketId id, Stack stack, Frame frame, PacketIndex index, ProtocolId firstProtocolId)
    {
        Packet packet = new(id, stack, frame)
        {
            _FirstProtocolOverride = firstProtocolId
        };
        ParseIndexedAndSeal(packet, frame, index);
        return packet;
    }

    // ── Recycling preconditions (shared) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the recycling preconditions in the order they are checked at runtime:
    /// the recycle packet must belong to <paramref name="stack"/>, must be finalized,
    /// must have no active materializer, and the frame's registry must match the stack's.
    /// Returns <see langword="null"/> on success or the specific <see cref="RecycleError"/>
    /// for the first failed check. Marked <see cref="MethodImplOptions.AggressiveInlining"/>
    /// so the stack reference compare folds into the caller on the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RecycleError? TryPrepareForRecycle(Packet recycle, PacketId id, Stack stack, Frame frame)
    {
        // Stack check first: cheapest, catches the most common cross-stack mistake.
        if (!ReferenceEquals(recycle._Stack, stack))
        {
            return RecycleError.StackMismatch;
        }
        return recycle.PrepareForReuse(id, frame);
    }

    // ── Hot-path recycling overloads (return code — no exceptions) ────────────────────────────────
    //
    // Use these in tight recycling loops. A null return means success — the recycle
    // object is ready to use. A non-null return means one of the preconditions failed;
    // the recycle object is unchanged and the caller can decide whether to throw or skip.
    //
    // Callers that prefer exceptions can use the ParseFrame(Packet recycle, …) overloads
    // below, which delegate to these methods and translate the return code.

    /// <summary>
    /// Hot-path variant: parses a new frame into <paramref name="recycle"/> without heap
    /// allocation. Returns <see langword="null"/> on success; returns a
    /// <see cref="RecycleError"/> code if a precondition failed — no exception is thrown.
    /// </summary>
    /// <param name="recycle">Packet to reuse. Must be finalized, belong to <paramref name="stack"/>, and have no active materializer.</param>
    /// <param name="id">New packet identifier.</param>
    /// <param name="stack">The owning stack. Must be reference-equal to the recycle packet's stack.</param>
    /// <param name="frame">New frame to parse. Must share the same <see cref="FrameInterfaceRegistry"/> as the stack.</param>
    /// <returns><see langword="null"/> on success; a <see cref="RecycleError"/> value on precondition failure.</returns>
    public static RecycleError? TryParseFrame(Packet recycle, PacketId id, Stack stack, Frame frame)
    {
        RecycleError? error = TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        ParseAndSeal(recycle, frame);
        return null;
    }

    /// <summary>
    /// Hot-path variant: parses a new frame into <paramref name="recycle"/> dispatching to
    /// <paramref name="firstProtocolId"/>, without heap allocation.
    /// </summary>
    /// <inheritdoc cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static RecycleError? TryParseFrame(
        Packet recycle, PacketId id, Stack stack, Frame frame, ProtocolId firstProtocolId)
    {
        RecycleError? error = TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        recycle._FirstProtocolOverride = firstProtocolId;
        ParseAndSeal(recycle, frame);
        return null;
    }

    /// <summary>
    /// Hot-path variant: parses a new frame into <paramref name="recycle"/> while recording
    /// field presence in the given index, without heap allocation.
    /// </summary>
    /// <inheritdoc cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static RecycleError? TryParseFrameIndexed(
        Packet recycle, PacketId id, Stack stack, Frame frame, PacketIndex index)
    {
        RecycleError? error = TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        ParseIndexedAndSeal(recycle, frame, index);
        return null;
    }

    /// <summary>
    /// Hot-path variant: parses a new frame into <paramref name="recycle"/> while recording
    /// field presence in the given index and dispatching to <paramref name="firstProtocolId"/>,
    /// without heap allocation.
    /// </summary>
    /// <inheritdoc cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static RecycleError? TryParseFrameIndexed(
        Packet recycle, PacketId id, Stack stack, Frame frame,
        PacketIndex index, ProtocolId firstProtocolId)
    {
        RecycleError? error = TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        recycle._FirstProtocolOverride = firstProtocolId;
        ParseIndexedAndSeal(recycle, frame, index);
        return null;
    }

    // ── Recycling overloads (throwing — convenience / programmer-error detection) ─────────────────
    //
    // Thin wrappers over the TryParseFrame variants above. They translate a non-null
    // RecycleError into the appropriate exception. Prefer TryParseFrame in hot paths
    // to avoid exception construction overhead.

    /// <summary>
    /// Parses a new frame into an existing recycled packet, eliminating heap allocation.
    /// The <paramref name="recycle"/> packet must be finalized (<see cref="Packet.IsFinalized"/>
    /// == <see langword="true"/>), must belong to <paramref name="stack"/>, and must not be
    /// accessed concurrently. The returned reference is the same object as
    /// <paramref name="recycle"/>.
    /// <para>
    /// For hot recycling loops, prefer
    /// <see cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/> which returns a
    /// <see cref="RecycleError"/> code instead of throwing.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="stack"/> does not match the recycle packet's stack,
    /// or when the frame's registry does not match.</exception>
    /// <exception cref="InvalidOperationException">When the recycle packet is not yet finalized or a materializer is active.</exception>
    public static Packet ParseFrame(Packet recycle, PacketId id, Stack stack, Frame frame)
    {
        RecycleError? error = TryParseFrame(recycle, id, stack, frame);
        if (error is not null)
        {
            ThrowRecycleError(error.Value);
        }
        return recycle;
    }

    /// <summary>
    /// Parses a new frame into an existing recycled packet dispatching to
    /// <paramref name="firstProtocolId"/>, eliminating heap allocation.
    /// </summary>
    /// <inheritdoc cref="ParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static Packet ParseFrame(
        Packet recycle, PacketId id, Stack stack, Frame frame, ProtocolId firstProtocolId)
    {
        RecycleError? error = TryParseFrame(recycle, id, stack, frame, firstProtocolId);
        if (error is not null)
        {
            ThrowRecycleError(error.Value);
        }
        return recycle;
    }

    /// <summary>
    /// Parses a new frame into an existing recycled packet while recording field presence
    /// in the given index, eliminating heap allocation.
    /// </summary>
    /// <inheritdoc cref="ParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static Packet ParseFrameIndexed(
        Packet recycle, PacketId id, Stack stack, Frame frame, PacketIndex index)
    {
        RecycleError? error = TryParseFrameIndexed(recycle, id, stack, frame, index);
        if (error is not null)
        {
            ThrowRecycleError(error.Value);
        }
        return recycle;
    }

    /// <summary>
    /// Parses a new frame into an existing recycled packet while recording field presence
    /// in the given index and dispatching to <paramref name="firstProtocolId"/>,
    /// eliminating heap allocation.
    /// </summary>
    /// <inheritdoc cref="ParseFrame(Packet, PacketId, Stack, Frame)"/>
    public static Packet ParseFrameIndexed(
        Packet recycle, PacketId id, Stack stack, Frame frame,
        PacketIndex index, ProtocolId firstProtocolId)
    {
        RecycleError? error = TryParseFrameIndexed(recycle, id, stack, frame, index, firstProtocolId);
        if (error is not null)
        {
            ThrowRecycleError(error.Value);
        }
        return recycle;
    }

    #endregion

    #region Iterators
    /// <summary>
    /// Iterates all fields in depth-first pre-order (including root).
    /// When <paramref name="materialize"/> is true (default), lazy fields are materialized during traversal.
    /// </summary>
    public FieldDfsEnumerable IterFieldsDfs(bool materialize = true) => new(this, materialize);

    /// <summary>
    /// Iterates all fields in storage order (linear walk over the internal array).
    /// When <paramref name="materialize"/> is true (default), lazy fields are materialized as encountered.
    /// </summary>
    public FieldFlatEnumerable IterFieldsFlat(bool materialize = true) => new(this, materialize);

    #endregion
}
