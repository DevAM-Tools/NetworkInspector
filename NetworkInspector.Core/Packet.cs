// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Represents a parsed packet with a flat field tree.
/// Fields are stored in chunk-based <see cref="FieldBody"/> storage with <see cref="ushort"/> linked-list indices.
/// Each chunk holds <see cref="_FieldBodyChunkSize"/> (16) slots. Chunk descriptors are slab-backed
/// via <see cref="SlabAllocator{T}"/> — initially <see cref="_InitialChunkDescriptors"/> (4)
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
/// for concurrent reads from multiple threads. Lazy field materialization uses per-field
/// CAS on <see cref="FieldBody.LazyIndex"/> (materializing marker bit) so unrelated lazy
/// branches can populate concurrently after <see cref="Seal"/>.
/// All cross-thread visibility is ensured via <see cref="Volatile"/> reads/writes — no locks.
/// </para>
/// </summary>
public sealed class Packet
{
    #region Constants & Fields

    private const int _MaxFieldCount = ushort.MaxValue - 1;

    /// <summary>Number of <see cref="FieldBody"/> slots per chunk (must be power of 2).</summary>
    private const int _FieldBodyChunkSize = 16;

    /// <summary>Log₂ of <see cref="_FieldBodyChunkSize"/> for bitwise division: index >> ChunkShift = chunkIdx.</summary>
    private const int _FieldBodyChunkShift = 4;

    /// <summary>Bitmask for modulo <see cref="_FieldBodyChunkSize"/>: index &amp; ChunkMask = slotIdx.</summary>
    private const int _FieldBodyChunkMask = _FieldBodyChunkSize - 1;

    /// <summary>Default slab capacity for <see cref="FieldBody"/> storage: 1024 slots × ~64 B ≈ 64 KB (below LOH).</summary>
    private const int _FieldBodySlabCapacity = 1024;

    /// <summary>Number of chunk descriptors allocated per packet initially (4 × 16 = 64 fields).</summary>
    private const int _InitialChunkDescriptors = 4;

    /// <summary>Default slab capacity for chunk descriptors: 256 × 12 B ≈ 3 KB.</summary>
    private const int _ChunkDescriptorSlabCapacity = 256;

    /// <summary>Number of lazy populator slots per initial allocation (covers typical protocol stacks).</summary>
    private const int _LazyPopulatorChunkSize = 8;

    /// <summary>Default slab capacity for lazy populators: 512 × 8 B = 4 KB.</summary>
    private const int _LazyPopulatorSlabCapacity = 512;

    // Id, Timestamp, Frame and FrameSourceId use private set to support PrepareForReuse().
    // Stack is get-only: recycling requires the same stack (validated in PrepareForReuse).
    // Per-thread bump allocators: multiple packets share a single backing array per type.
    // When a slab is full, a new one is created; the old stays alive via consumer references.
    [ThreadStatic]
    private static SlabAllocator<FieldBody>? _FieldBodySlab;
    [ThreadStatic]
    private static SlabAllocator<LazyPopulator>? _LazyPopulatorSlab;
    [ThreadStatic]
    private static SlabAllocator<FieldBodyChunk>? _ChunkDescriptorSlab;

    // Slab-backed chunk descriptor table. Each entry is a (FieldBody[], int offset)
    // pair pointing into a FieldBody slab. Initially 4 slots (64 fields); grown by
    // doubling from the chunk descriptor slab when capacity is exceeded.
    // Published as one object so concurrent readers cannot tear (array, offset) on growth.
    private volatile ChunkTable _ChunkTable = null!;

    // Number of allocated FieldBody chunks. Post-Seal concurrent materializers may grow this.
    // Volatile: waiters acquire on this count after the allocating thread's release store.
    private volatile int _ChunkCount;

    private ReadOnlyMemory<byte>[]? _AdditionalBuffers;
    private int _AdditionalBufferCount;

    // LazyPopulator storage: slab-backed like FieldBody. On first lazy field,
    // allocates _LazyPopulatorChunkSize (8) slots from the thread-local SlabAllocator.
    // Growth beyond 8 slots uses Array.Resize (rare).
    private LazyPopulator[]? _LazyPopulators;
    private int _LazyPopulatorOffset;
    private int _LazyPopulatorCapacity;
    private int _LazyPopulatorCount;

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
    // int sentinel retained for Interlocked pattern consistency — mixing Volatile and Interlocked
    // access styles on the same field is a contract violation; all finalization writes use
    // Interlocked.CompareExchange/Exchange, so the int type keeps all accesses in one contract.
    private volatile int _Finalized;

    // Side-channel info LazyString set by sub-protocols during Parse().
    // Used as the source for the lazy packet.info field value. After PacketProtocol.Parse
    // creates the packet.info field and calls SetInfoFieldIndex(), InfoFieldIndex is valid
    // and Packet.Info reads directly from the boxed LazyString field for in-heap caching.

    // Storage index of the packet.info FieldBody in the flat field array.
    // Set by PacketProtocol after the packet.info field is appended.
    // Sentinel value FieldBody.NullIndex (0xFFFF) means not yet set.
    private ushort _InfoFieldIndex = FieldBody.NullIndex;

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
            _ThrowRegistryMismatch();
        }

        Id = id;
        Timestamp = frame.Timestamp;
        Stack = stack;
        Frame = frame;
        FrameSourceId = _DeriveFrameSourceId(frame.InterfaceId, stack.FrameInterfaceRegistry);
        _AllocateFirstChunk();
        ref FieldBodyChunk firstChunk = ref _GetChunk(0);
        firstChunk.Buffer[firstChunk.Offset] = new FieldBody(stack.RootFieldId);
        _AllocatedFieldCount = 1;
        _FieldCount = 1;
        InfoLazy = LazyString.Empty;
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
    /// must be in progress (<c>_ActiveLazyMaterializations &gt; 0</c>). Call from a single thread only.
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
        if (_ActiveLazyMaterializations != 0)
        {
            return RecycleError.MaterializerActive;
        }

        // Validate registry consistency (same check as the constructor).
        if (!ReferenceEquals(frame.Registry, Stack.FrameInterfaceRegistry))
        {
            return RecycleError.RegistryMismatch;
        }

        // ── 1. Clear GC-visible references in every active FieldBody chunk ──────────
        // Array.Clear zeroes entire FieldBody structs (including FieldValue and LazyString
        // reference fields) so the GC does not retain stale references after reuse.
        // All chunks before the last are always completely full (_FieldBodyChunkSize slots).
        // The last chunk is only partially used: its slot count equals (_AllocatedFieldCount % _FieldBodyChunkSize),
        // or _FieldBodyChunkSize when the chunk was exactly filled. Clearing only used slots
        // avoids zeroing unused tail slots — typically saves ~25% of Array.Clear work when
        // the last chunk holds ~12 out of 16 used slots (common for IPv6/UDP packets).
        int usedInLastChunk = _AllocatedFieldCount & _FieldBodyChunkMask;
        if (usedInLastChunk == 0)
        {
            usedInLastChunk = _FieldBodyChunkSize; // chunk was exactly filled
        }
        for (int i = 0; i < _ChunkCount; i++)
        {
            ref FieldBodyChunk chunk = ref _GetChunk(i);
            // Full chunks: all _FieldBodyChunkSize slots. Last (partial) chunk: only used slots.
            int clearCount = i < _ChunkCount - 1 ? _FieldBodyChunkSize : usedInLastChunk;
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
        // Keep the chunk table buffer/offset and lazy populator slots — they are reused
        // as-is for the next parse. Reset only one active chunk; extra chunks from a previous
        // larger parse are abandoned (their slab slots stay alive in the slab but are not
        // reused by this packet). Capacity is restored to the initial descriptor count so
        // reuse matches a fresh packet's growth curve.
        ChunkTable table = _ChunkTable;
        _ChunkTable = new ChunkTable(table.Buffer, table.BaseOffset, _InitialChunkDescriptors);
        _ChunkCount = 1;
        _LazyPopulatorCount = 0;
        _PendingLazyCount = 0;
        _ActiveLazyMaterializations = 0;
        _AllocatedFieldCount = 1;
        _FieldCount = 1;
        _Finalized = 0;  // Re-open the packet for a new parse (no fence needed: single-threaded)
        InfoLazy = LazyString.Empty;
        _InfoFieldIndex = FieldBody.NullIndex;
        FirstProtocolOverride = ProtocolId.Invalid;

        // Clear additional buffers (reassembly data from the previous parse).
        if (_AdditionalBufferCount > 0 && _AdditionalBuffers is not null)
        {
            Array.Clear(_AdditionalBuffers, 0, _AdditionalBufferCount);
            _AdditionalBufferCount = 0;
        }

        // ── 4. Set new identity fields ────────────────────────────────────────────────
        Id = id;
        Timestamp = frame.Timestamp;
        Frame = frame;
        FrameSourceId = _DeriveFrameSourceId(frame.InterfaceId, Stack.FrameInterfaceRegistry);

        // ── 5. Re-initialise the root FieldBody in the first slot of the first chunk ─
        ref FieldBodyChunk firstChunk = ref _GetChunk(0);
        firstChunk.Buffer[firstChunk.Offset] = new FieldBody(Stack.RootFieldId);

        return null; // success
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
    /// Returns a reference to the chunk descriptor at the given chunk index.
    /// All chunk descriptors are stored in the slab-backed <see cref="_ChunkTable"/>.
    /// Loads the table reference once so a concurrent growth cannot tear array vs offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref FieldBodyChunk _GetChunk(int chunkIndex)
    {
        ChunkTable table = _ChunkTable;
        return ref table.Buffer[table.BaseOffset + chunkIndex];
    }

    /// <summary>
    /// Returns a mutable reference to the field body at the given logical index.
    /// The logical index is decomposed into chunk index and slot index via bit shifts:
    /// <c>chunkIdx = logicalIndex >> ChunkShift</c>, <c>slotIdx = logicalIndex &amp; ChunkMask</c>.
    /// All stored <see cref="ushort"/> indices in <see cref="FieldBody"/> are logical indices.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref FieldBody GetFieldRef(int index)
    {
        int chunkIdx = index >> _FieldBodyChunkShift;
        int slotIdx = index & _FieldBodyChunkMask;
        ref FieldBodyChunk chunk = ref _GetChunk(chunkIdx);
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

    #endregion

    #region Lazy Field Support
    /// <summary>
    /// Whether any lazy fields exist that have not been populated yet.
    /// O(1) — uses <see cref="System.Threading.Volatile"/> Read for cross-thread visibility.
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
    internal ushort RegisterLazyPopulator(ushort fieldIndex, LazyPopulator populator)
    {
        if (_LazyPopulators is null)
        {
            // First lazy field — allocate from thread-local slab (no per-packet alloc)
            _AllocateFromSlab(
                ref _LazyPopulatorSlab, _LazyPopulatorSlabCapacity,
                _LazyPopulatorChunkSize, out _LazyPopulators, out _LazyPopulatorOffset);
            _LazyPopulatorCapacity = _LazyPopulatorChunkSize;
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

        // Interlocked for a uniform contract with post-Seal paths and volatile definition.
        // Pre-Seal is single-threaded; Interlocked remains correct and satisfies §4.3.
        Interlocked.Increment(ref _PendingLazyCount);

        return lazyIndex;
    }

    /// <summary>
    /// Materializes a lazy field's children if not already populated.
    /// <para>
    /// <b>Pre-Seal (single-threaded):</b> Uses per-field CAS on <see cref="FieldBody.LazyIndex"/>.
    /// </para>
    /// <para>
    /// <b>Post-Seal (concurrent):</b> Per-field CAS ensures exactly one thread executes each
    /// populator; other threads spin until the field's lazy index clears.
    /// </para>
    /// </summary>
    internal bool MaterializeLazyField(ushort fieldIndex)
    {
        // Fast path: volatile check — if no pending lazy fields, nothing to do
        if (_PendingLazyCount == 0)
        {
            return false;
        }

        // Invariant: _PendingLazyCount > 0 guarantees _LazyPopulators is initialized
        if (_LazyPopulators is null)
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

        bool postSeal = _Finalized != 0;
        ushort lazyPopulatorIndex;
        if (postSeal)
        {
            SpinWait spin = default;
            while (!body.TryClaimLazyMaterialization(out lazyPopulatorIndex))
            {
                if (body.ReadLazyIndexVolatile() == 0)
                {
                    return false;
                }
                spin.SpinOnce();
            }
            Interlocked.Increment(ref _ActiveLazyMaterializations);
        }
        else if (!body.TryClaimLazyMaterialization(out lazyPopulatorIndex))
        {
            return false;
        }

        try
        {
            // Extract and clear the populator to allow GC of captured state
            int arrayIndex = _LazyPopulatorOffset + lazyPopulatorIndex - 1;
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
                // child fields. Always Interlocked so the volatile field never uses compound RMW.
                Interlocked.Decrement(ref _PendingLazyCount);
            }
            return true;
        }
        finally
        {
            body.FinishLazyMaterialization();
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
            _MaterializeAllPostSeal();
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
    internal void SetInfoFieldIndex(ushort index) => _InfoFieldIndex = index;

    /// <summary>Sets the packet info/summary string.</summary>
    internal void SetInfo(LazyString info) => InfoLazy = info;

    /// <summary>Appends to the packet info/summary string.</summary>
    internal void AppendToInfo(LazyString suffix) => InfoLazy = InfoLazy.Append(suffix);

    /// <summary>Prepends to the packet info/summary string.</summary>
    internal void PrependToInfo(LazyString prefix) => InfoLazy = prefix.Append(InfoLazy);

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
        // Linear scan from cookie position through currently-materialized fields
        int count = _FieldCount;

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
        if (materialize && _PendingLazyCount > 0)
        {
            // Look up the target field's owning protocol via the stack registry
            FieldInfo? targetInfo = Stack.GetField(fieldId);
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
            for (int depth = 0; depth < _MaxMaterializationDepth; depth++)
            {
                if (!HasUnpopulatedLazyFields)
                {
                    break;
                }

                // Re-read field array each iteration — materialization may have grown it
                // and appended new lazy containers.
                count = _FieldCount;
                bool materialized = false;

                for (int i = 0; i < count; i++)
                {
                    ref readonly FieldBody fb = ref GetFieldRef(i);
                    if (!fb.NeedsMaterialization)
                    {
                        continue;
                    }

                    // Check if this lazy container belongs to the target protocol
                    FieldInfo? containerInfo = Stack.GetField(fb.FieldId);
                    if (containerInfo is not null && containerInfo.ProtocolId == targetProtocolId)
                    {
                        // Materialize this specific container only
                        MaterializeLazyField((ushort)i);
                        materialized = true;

                        // Scan only newly-appended fields (searchedUpTo..newCount).
                        // Everything before searchedUpTo was already checked — either
                        // in the initial scan or in a previous materialization iteration.
                        int newCount = _FieldCount;
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
    /// Reserves a unique storage slot index without publishing it to readers.
    /// Pre-Seal uses a plain increment of <see cref="_AllocatedFieldCount"/> (single-threaded parse).
    /// Post-Seal uses <see cref="Interlocked.CompareExchange(ref int, int, int)"/> so concurrent
    /// lazy materializers cannot share a slot. Callers write the <see cref="FieldBody"/>, fix
    /// parent/sibling pointers, then publish via <see cref="_PublishFieldCount"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int _ReserveFieldSlot()
    {
        if (_Finalized != 0)
        {
            int current = _AllocatedFieldCount;
            while (true)
            {
                if (current >= _MaxFieldCount)
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
        if (count >= _MaxFieldCount)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Maximum field count exceeded"));
        }
        _AllocatedFieldCount = count + 1;
        return count;
    }

    /// <summary>
    /// Ensures the FieldBody chunk that holds <paramref name="reservedIndex"/> exists.
    /// Pre-Seal allocates immediately (single-threaded). Post-Seal: the thread that reserved
    /// the first slot of a new chunk allocates it; other threads spin until
    /// <see cref="_ChunkCount"/> includes that chunk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _EnsureChunkForReservedIndex(int reservedIndex)
    {
        int chunkIdx = reservedIndex >> _FieldBodyChunkShift;
        if (chunkIdx < _ChunkCount)
        {
            return;
        }

        if (_Finalized == 0)
        {
            _AllocateNewChunk();
            return;
        }

        int firstIndexOfChunk = chunkIdx << _FieldBodyChunkShift;
        if (reservedIndex == firstIndexOfChunk)
        {
            SpinWait spin = default;
            while (_ChunkCount < chunkIdx)
            {
                spin.SpinOnce();
            }
            _AllocateNewChunk();
            return;
        }

        SpinWait waiter = default;
        while (chunkIdx >= _ChunkCount)
        {
            waiter.SpinOnce();
        }
    }

    /// <summary>
    /// Writes a new <see cref="FieldBody"/> into the reserved slot of the chunk-based
    /// storage. When the current chunk is full, allocates a new chunk from the thread-local
    /// slab (no copy — previous chunks remain untouched).
    /// <para>
    /// <b>Thread-safety:</b> Does <b>not</b> publish <see cref="_FieldCount"/>. A post-
    /// <see cref="Seal"/> caller still has to fix up parent / sibling linked-list pointers
    /// <b>after</b> this call. Publishing the new count too early would let a concurrent
    /// reader observe the incremented count (and therefore the new slot) while the parent's
    /// <c>FirstChildIndex</c> / <c>LastChildIndex</c> / <c>NextIndex</c> / <c>PrevIndex</c>
    /// still point to stale neighbours. Callers MUST invoke <see cref="_PublishFieldCount"/>
    /// after every parent / sibling write that belongs to the same logical insertion.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AddFieldBody(in FieldBody field, int reservedIndex)
    {
        int chunkIdx = reservedIndex >> _FieldBodyChunkShift;
        int slotIdx = reservedIndex & _FieldBodyChunkMask;
        _EnsureChunkForReservedIndex(reservedIndex);

        ref FieldBodyChunk chunk = ref _GetChunk(chunkIdx);
        chunk.Buffer[chunk.Offset + slotIdx] = field;
    }

    /// <summary>
    /// Publishes <paramref name="newCount"/> as the reader-visible <see cref="_FieldCount"/>
    /// after the field body and all parent / sibling linked-list stores. Post-Seal publication
    /// is in-order: this thread waits until the previous reserved index has been published so
    /// readers never observe a hole. Pre-Seal stores directly; <see cref="Seal"/> is the
    /// bulk publication fence for the parse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _PublishFieldCount(int newCount)
    {
        if (_Finalized == 0)
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
    /// Appends a child field to the given parent. Performs all parent / sibling linked-list
    /// updates <b>before</b> publishing the new field count, so that a concurrent reader
    /// post-Seal cannot observe the incremented count while parent pointers are still stale.
    /// </summary>
    internal ushort AppendChild(ushort parentIndex, FieldId fieldId, FieldValue value)
    {
        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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
        _PublishFieldCount(reservedIndex + 1);
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
        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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

        _PublishFieldCount(reservedIndex + 1);
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
        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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

        _PublishFieldCount(reservedIndex + 1);
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
        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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

        _PublishFieldCount(reservedIndex + 1);
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
        ushort parentIndex = GetFieldRef(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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

        _PublishFieldCount(reservedIndex + 1);
        return newIndex;
    }

    /// <summary>
    /// Inserts a field after the given sibling. Performs all parent / sibling linked-list
    /// updates <b>before</b> publishing the new field count, so that a concurrent reader
    /// post-Seal cannot observe the incremented count while sibling pointers are still stale.
    /// </summary>
    internal ushort InsertAfter(ushort siblingIndex, FieldId fieldId, FieldValue value)
    {
        ushort parentIndex = GetFieldRef(siblingIndex).ParentIndex;
        if (parentIndex == FieldBody.NullIndex)
        {
            ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet", "Cannot insert after root"));
        }

        int reservedIndex = _ReserveFieldSlot();
        ushort newIndex = (ushort)reservedIndex;
        FieldBody newField = new(fieldId, value)
        {
            ParentIndex = parentIndex
        };
        _AddFieldBody(in newField, reservedIndex);

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

        _PublishFieldCount(reservedIndex + 1);
        return newIndex;
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
    /// Allocates the initial chunk descriptor region and the first FieldBody chunk.
    /// <see cref="_InitialChunkDescriptors"/> (4) descriptor slots are reserved
    /// from the thread-local chunk descriptor slab, covering up to 64 fields. Only the first
    /// FieldBody chunk (16 slots) is allocated upfront — subsequent chunks are allocated
    /// on demand by <see cref="_AddFieldBody"/> when slots fill up.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void _AllocateFirstChunk()
    {
        // Reserve chunk descriptor slots from the chunk descriptor slab
        _AllocateFromSlab(
            ref _ChunkDescriptorSlab, _ChunkDescriptorSlabCapacity,
            _InitialChunkDescriptors, out FieldBodyChunk[] chunks, out int baseOffset);
        _ChunkTable = new ChunkTable(chunks, baseOffset, _InitialChunkDescriptors);

        // Allocate the first FieldBody chunk (16 slots) from the FieldBody slab
        ref FieldBodyChunk first = ref chunks[baseOffset];
        _AllocateFromSlab(
            ref _FieldBodySlab, _FieldBodySlabCapacity,
            _FieldBodyChunkSize, out first.Buffer, out first.Offset);
        _ChunkCount = 1;
    }

    /// <summary>
    /// Allocates a single chunk of <see cref="_FieldBodyChunkSize"/> slots from the
    /// thread-local FieldBody slab. Creates a new slab if the current one is full.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void _AllocateFieldBodyChunk(out FieldBody[] buffer, out int offset) =>
        _AllocateFromSlab(
            ref _FieldBodySlab, _FieldBodySlabCapacity,
            _FieldBodyChunkSize, out buffer, out offset);

    /// <summary>
    /// Allocates an additional FieldBody chunk on demand.
    /// If the chunk descriptor array is full, doubles it by allocating a new region
    /// from the chunk descriptor slab and copying existing descriptors (small: count × 12 bytes).
    /// </summary>
    private void _AllocateNewChunk()
    {
        ChunkTable table = _ChunkTable;
        int chunkCount = _ChunkCount;

        // Grow chunk descriptor array if capacity is exhausted. Publish a new table
        // object so concurrent _GetChunk cannot tear (array, offset).
        if (chunkCount >= table.Capacity)
        {
            int newCapacity = table.Capacity * 2;
            _AllocateFromSlab(
                ref _ChunkDescriptorSlab, _ChunkDescriptorSlabCapacity,
                newCapacity, out FieldBodyChunk[] newChunks, out int newOffset);
            table.Buffer.AsSpan(table.BaseOffset, chunkCount)
                   .CopyTo(newChunks.AsSpan(newOffset));
            table = new ChunkTable(newChunks, newOffset, newCapacity);
            _ChunkTable = table;
        }

        // Allocate FieldBody slots for the new chunk from the FieldBody slab
        ref FieldBodyChunk newChunk = ref table.Buffer[table.BaseOffset + chunkCount];
        _AllocateFieldBodyChunk(out newChunk.Buffer, out newChunk.Offset);
        _ChunkCount = chunkCount + 1;
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
    private static void _AllocateFromSlab<T>(
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
        ThrowHelpers.ThrowFieldAppend(ParseError.Custom("packet",
            $"Slab allocation failed after {_MaxSlabGrowAttempts} attempts (requested {count} slots)."));
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

    /// <summary>
    /// Translates a <see cref="RecycleError"/> code into the appropriate exception and throws it.
    /// Called by the throwing <c>ParseFrame(Packet recycle, …)</c> overloads to convert the
    /// return code from <see cref="TryParseFrame(Packet, PacketId, Stack, Frame)"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">For <see cref="RecycleError.NotFinalized"/> and <see cref="RecycleError.MaterializerActive"/>.</exception>
    /// <exception cref="ArgumentException">For <see cref="RecycleError.RegistryMismatch"/> and <see cref="RecycleError.StackMismatch"/>.</exception>
    [DoesNotReturn]
    private static void _ThrowRecycleError(RecycleError error)
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
    /// Each chunk holds exactly <see cref="_FieldBodyChunkSize"/> (16) slots, and
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
        _Finalized = 1;
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
            context = new ParseContext(packet.Stack);
        }

        MutField rootField = packet.RootFieldMut();
        ParseResult result = packet.Stack.CallProtocol(packetProtocolId, in rootField, frame.Data, in context);
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
    private static string _BuildExceptionMessage(Exception ex, bool includeStackTrace)
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
    /// Runs <see cref="_ParseFrameInternal"/> inside a protocol-exception guard and seals
    /// the packet. Any protocol exception is recorded as a field-level error rather than
    /// propagating to the caller.
    /// </summary>
    private static void _ParseAndSeal(Packet packet, Frame frame)
    {
        try
        {
            _ParseFrameInternal(packet, frame);
        }
        catch (Exception ex)
        {
            packet.SetError(_BuildExceptionMessage(ex, packet.Stack.IncludeExceptionStackTrace));
        }
        packet.Seal();
    }

    /// <summary>
    /// Runs the full indexed-parse lifecycle: begins the packet in the index, builds the
    /// parse context, runs the protocol-exception-guarded parse, ends the packet in the
    /// index, and seals the packet.
    /// </summary>
    private static void _ParseIndexedAndSeal(Packet packet, Frame frame, PacketIndex index)
    {
        index.BeginPacket(packet.Id.Value);

        ParseContext context = new(index, packet.Stack);

        try
        {
            _ParseFrameInternal(packet, frame, context);
        }
        catch (Exception ex)
        {
            packet.SetError(_BuildExceptionMessage(ex, packet.Stack.IncludeExceptionStackTrace));
            index.RollbackCurrentPacket();
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
        _ParseAndSeal(packet, frame);
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
            FirstProtocolOverride = firstProtocolId
        };
        _ParseAndSeal(packet, frame);
        return packet;
    }

    /// <summary>Parses a frame into a new packet while recording field presence in the given index.</summary>
    public static Packet ParseFrameIndexed(
        PacketId id, Stack stack, Frame frame, PacketIndex index)
    {
        Packet packet = new(id, stack, frame);
        _ParseIndexedAndSeal(packet, frame, index);
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
            FirstProtocolOverride = firstProtocolId
        };
        _ParseIndexedAndSeal(packet, frame, index);
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
    private static RecycleError? _TryPrepareForRecycle(Packet recycle, PacketId id, Stack stack, Frame frame)
    {
        // Stack check first: cheapest, catches the most common cross-stack mistake.
        if (!ReferenceEquals(recycle.Stack, stack))
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
        RecycleError? error = _TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        _ParseAndSeal(recycle, frame);
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
        RecycleError? error = _TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        recycle.FirstProtocolOverride = firstProtocolId;
        _ParseAndSeal(recycle, frame);
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
        RecycleError? error = _TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        _ParseIndexedAndSeal(recycle, frame, index);
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
        RecycleError? error = _TryPrepareForRecycle(recycle, id, stack, frame);
        if (error is not null)
        {
            return error;
        }
        recycle.FirstProtocolOverride = firstProtocolId;
        _ParseIndexedAndSeal(recycle, frame, index);
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
            _ThrowRecycleError(error.Value);
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
            _ThrowRecycleError(error.Value);
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
            _ThrowRecycleError(error.Value);
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
            _ThrowRecycleError(error.Value);
        }
        return recycle;
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
    /// Immutable snapshot of the chunk-descriptor array plus its slab base offset and capacity.
    /// Published as a single reference so <see cref="_GetChunk"/> cannot observe a torn
    /// (array, offset) pair while descriptors grow.
    /// </summary>
    private sealed class ChunkTable
    {
        /// <summary>Slab-backed chunk descriptor array.</summary>
        internal readonly FieldBodyChunk[] Buffer;

        /// <summary>Index of this packet's first descriptor in <see cref="Buffer"/>.</summary>
        internal readonly int BaseOffset;

        /// <summary>Number of descriptor slots reserved for this packet in <see cref="Buffer"/>.</summary>
        internal readonly int Capacity;

        /// <summary>Creates a published chunk-descriptor table snapshot.</summary>
        internal ChunkTable(FieldBodyChunk[] buffer, int baseOffset, int capacity)
        {
            Buffer = buffer;
            BaseOffset = baseOffset;
            Capacity = capacity;
        }
    }

    #endregion
}
