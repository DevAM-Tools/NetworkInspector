// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Thread-safe, lock-free registry for frame interfaces (e.g., eth0, wlan0) and their
/// owning frame sources (e.g., pcap files, live capture devices).
/// <para>
/// Extracted from <see cref="Stack"/> so that a single registry can be shared
/// across multiple stacks — this is important when the same capture source
/// feeds data into different analysis configurations.
/// </para>
/// <para>
/// Uses a copy-on-write pattern with <see cref="Interlocked.CompareExchange{T}"/>
/// instead of traditional reader-writer locks. Reads are always wait-free;
/// writes (registrations) are rare and use CAS retry.
/// </para>
/// <para>
/// <b>Why snapshots:</b> every parsed packet reads the registry (the frame's interface is looked up and the registry
/// reference is compared on the parse path), while registrations happen when a source opens. A lock or reader-writer lock
/// would put a synchronization cost on every read; here a read is one volatile load of an immutable snapshot plus one bounds
/// test. A registration allocates one small snapshot object and, when the backing array is full, one doubled array (so n
/// registrations copy O(n) references in total, not O(n²)). The count of registered interfaces comes from capture files
/// (untrusted), which is why the copy work must not grow with every registration. Ids are the array indexes, so they are
/// dense, assigned in registration order, and never reused or removed.
/// </para>
/// <para>
/// <b>Thread-safety:</b> all members are safe to call from any thread. Entries below the count of a published snapshot are
/// never mutated, so a snapshot (including the spans returned by <see cref="All"/> and <see cref="AllSources"/>) stays valid
/// and consistent while other threads register. Entries are only ever appended: an id that resolved once resolves forever.
/// </para>
/// <para>
/// <b>Retention:</b> entries are never removed, so a registry keeps every registered source reachable for its own lifetime;
/// a host that opens many captures uses one registry per session.
/// </para>
/// <para>
/// <b>Limit:</b> the number of interfaces is bounded by <see cref="CoreLimits.MaxInterfaces"/> (setting <c>frames.maxInterfaces</c>,
/// default 1,048,576): a capture file that is nothing but interface descriptions is refused with an exception that names the limit.
/// A registry without a stack applies the default; a stack built on it binds its own live holder.
/// </para>
/// </summary>
public sealed class FrameInterfaceRegistry
{
    #region Configuration and state

    /// <summary>
    /// One published state of a table: the first <see cref="Count"/> slots of <see cref="Items"/> are registered entries and
    /// never change; slots at and above <see cref="Count"/> are spare capacity (null) that no reader of this snapshot looks at.
    /// Immutable; safe to share between threads.
    /// </summary>
    /// <typeparam name="T">The registered entry type.</typeparam>
    private sealed class Snapshot<T>(T[] items, int count)
        where T : class
    {
        /// <summary>The backing array; shared with later snapshots until it is full.</summary>
        public readonly T[] Items = items;

        /// <summary>Number of registered entries; the exclusive upper bound of every read.</summary>
        public readonly int Count = count;
    }

    /// <summary>
    /// The current snapshot of registered interfaces. Read only through <see cref="Volatile.Read{T}(ref readonly T)"/> (an
    /// acquire read), so a reader that loads it once and uses only that object sees one consistent count and array. Replaced by
    /// compare-exchange on every registration. Not declared <see langword="volatile"/> because the shared append helper takes
    /// the field by reference, which C# does not allow for a volatile field without a warning.
    /// </summary>
    private Snapshot<FrameInterfaceInfo> _Interfaces = new([], 0);

    /// <summary>
    /// The current snapshot of registered frame sources. Same volatile-snapshot read rule as <see cref="_Interfaces"/>.
    /// </summary>
    private Snapshot<FrameSourceInfo> _Sources = new([], 0);

    /// <summary>
    /// The live limit of <see cref="CoreLimits.MaxInterfaces"/> this registry applies. The fixed default until a stack binds its
    /// own holder (<see cref="BindInterfaceLimit"/>). Read with <see cref="Volatile.Read{T}(ref readonly T)"/> on every
    /// registration; replaced at most once per stack build.
    /// </summary>
    private Limit _InterfaceLimit = CoreLimitSet.Defaults.MaxInterfaces;

    /// <summary>
    /// Makes this registry follow the interface limit of a stack. The registry may be shared by several stacks; the stack that
    /// is built last decides.
    /// </summary>
    /// <param name="limit">The live holder of <see cref="CoreLimits.MaxInterfaces"/> of the stack.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limit"/> is <see langword="null"/>.</exception>
    internal void BindInterfaceLimit(Limit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        Volatile.Write(ref _InterfaceLimit, limit);
    }

    /// <summary>Creates an empty registry.</summary>
    public FrameInterfaceRegistry()
    {
    }

    #endregion

    #region Snapshot append

    /// <summary>
    /// Appends one entry to a snapshot table at index <c>current.Count</c>: into spare capacity when there is some, otherwise
    /// into a doubled array. Lock-free; a lost race costs at most one allocation.
    /// </summary>
    /// <typeparam name="T">The registered entry type.</typeparam>
    /// <param name="table">The published snapshot field to update.</param>
    /// <param name="current">The snapshot the caller read; its <see cref="Snapshot{T}.Count"/> is the entry's index.</param>
    /// <param name="entry">The entry to store at index <c>current.Count</c>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="entry"/> now sits at that index; <see langword="false"/> when another thread
    /// took the index, in which case the caller re-reads the table and tries again.
    /// </returns>
    private static bool _TryAppend<T>(ref Snapshot<T> table, Snapshot<T> current, T entry)
        where T : class
    {
        int count = current.Count;
        T[] items = current.Items;
        if (count == items.Length)
        {
            // Full: grow into a private array (doubling keeps the total copy work linear). Nobody sees it before the publish,
            // so a lost race costs only this allocation.
            T[] grown = new T[Math.Max(4, items.Length * 2)];
            items.AsSpan(0, count).CopyTo(grown);
            grown[count] = entry;
            return ReferenceEquals(
                Interlocked.CompareExchange(ref table, new Snapshot<T>(grown, count + 1), current),
                current);
        }

        // Spare capacity: claim slot [count] of the shared array. Only threads that hold this (array, count) pair compete for
        // it, and readers of any published snapshot stop below their count, so the write is invisible until the publish.
        // Null! is the comparand "empty slot": spare slots are default (null) by construction. Invariant: slots below a
        // published count are never null, so a non-null result means another thread owns this index.
        T? winner = Interlocked.CompareExchange(ref items[count], entry, null!); // Invariant: spare capacity holds null.

        // Publish count + 1 for whoever owns the slot now (this thread or the one that won it): the slot content is final, so
        // helping is safe, and it guarantees progress when the winner is descheduled between its two steps. When this
        // compare-exchange loses, a newer snapshot is already published and nothing is left to do.
        _ = Interlocked.CompareExchange(ref table, new Snapshot<T>(items, count + 1), current);
        return winner is null;
    }

    #endregion

    #region Interfaces

    /// <summary>Number of registered interfaces.</summary>
    public int Count => Volatile.Read(ref _Interfaces).Count;

    /// <summary>
    /// Registers a new frame interface belonging to a registered frame source.
    /// Thread-safe (lock-free, CAS retry).
    /// </summary>
    /// <param name="sourceId">The frame source that owns this interface. Must be a valid, registered source.</param>
    /// <param name="uiName">Human-readable interface name (e.g., "eth0").</param>
    /// <param name="description">Optional description text.</param>
    /// <param name="linkType">Optional link-layer header type for this interface.</param>
    /// <param name="properties">Optional source-specific metadata (e.g., channel number). Frozen on storage.</param>
    /// <returns>The unique identifier assigned to the new interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uiName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceId"/> is invalid or not registered, or <paramref name="uiName"/> is empty,
    /// whitespace, or contains control characters.
    /// </exception>
    /// <exception cref="InvalidOperationException">The interface count has reached <see cref="ArrayIndexIdRange.MaxValue"/> + 1.</exception>
    public FrameInterfaceId Register(
        FrameSourceId sourceId, string uiName, string? description = null,
        LinkType? linkType = null, IReadOnlyDictionary<string, object>? properties = null)
    {
        // Validate the display name here, before the retry loop. Names come from capture files and sources (untrusted); the
        // same check also runs inside FrameInterfaceInfo, but doing it first fails fast, before any array is copied, and
        // names the caller's parameter.
        ArgumentException.ThrowIfNullOrWhiteSpace(uiName);
        if (!NameValidation.IsValidUiName(uiName))
        {
            throw new ArgumentException(
                "Interface UI name must be non-empty and must not contain control characters.",
                nameof(uiName));
        }

        // Interfaces must belong to a registered source — no orphan interfaces allowed
        if (!sourceId.IsValid)
        {
            throw new ArgumentException(
                "A valid FrameSourceId is required. Register a source first via RegisterSource().",
                nameof(sourceId));
        }

        // The source table only grows and entries are never removed, so one check against the current snapshot is enough:
        // a source that is registered now stays registered while the loop below runs. The unsigned compare also rejects
        // negative values.
        if ((uint)sourceId.Value >= (uint)Volatile.Read(ref _Sources).Count)
        {
            throw new ArgumentException(
                Invariant($"Frame source ID {sourceId.Value} is not registered."), nameof(sourceId));
        }

        // CAS retry loop for lock-free registration. The id and the info are rebuilt on each attempt from the freshest
        // snapshot, because the id is the count and a concurrent registration may have taken it.
        while (true)
        {
            Snapshot<FrameInterfaceInfo> current = Volatile.Read(ref _Interfaces);
            int count = current.Count;

            // Capacity guard: ids are array indexes, so the count is capped by what an id can hold. Exceeding it is a
            // broken-invariant condition at registration time (not an expected per-packet failure), hence an exception.
            if (count > ArrayIndexIdRange.MaxValue)
            {
                throw new InvalidOperationException(
                    Invariant($"Maximum frame interface count exceeded (valid range 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)})."));
            }

            // The configured limit (frames.maxInterfaces): a capture file that is nothing but interface descriptions registers
            // as many interfaces as it can hold, so the registration is refused with the name of the limit. An exception, as
            // for the id range above: the registration of an interface is a setup call of a source, not a per-packet result.
            Limit interfaceLimit = Volatile.Read(ref _InterfaceLimit);
            if (count >= interfaceLimit.Ceiling)
            {
                throw new InvalidOperationException(CoreLimits.MaxInterfaces.HitMessage(
                    Invariant($"Maximum frame interface count exceeded: the registry already holds {count} interfaces"),
                    interfaceLimit.Value));
            }

            FrameInterfaceId id = new(count);
            FrameInterfaceInfo newInfo = new(id, sourceId, uiName, description, linkType, properties);
            if (_TryAppend(ref _Interfaces, current, newInfo))
            {
                return id;
            }

            // Another thread registered concurrently, or owned the slot this one wanted: retry with the new snapshot.
        }
    }

    /// <summary>
    /// Gets frame interface info by identifier. Wait-free O(1) lookup.
    /// </summary>
    /// <param name="id">The interface identifier to look up.</param>
    /// <returns>The interface info, or <c>null</c> if the ID is out of range.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FrameInterfaceInfo? Get(FrameInterfaceId id)
    {
        // Read the snapshot once — consistent even if another thread is registering. The unsigned compare handles
        // the negative Invalid id and any id past the end in one branch. An id issued by this registry always resolves.
        Snapshot<FrameInterfaceInfo> snapshot = Volatile.Read(ref _Interfaces);
        if ((uint)id.Value < (uint)snapshot.Count)
        {
            return snapshot.Items[id.Value];
        }

        return null;
    }

    /// <summary>
    /// Tries to get frame interface info by identifier.
    /// </summary>
    /// <param name="id">The interface identifier to look up.</param>
    /// <param name="info">When this method returns <c>true</c>, contains the interface info.</param>
    /// <returns><c>true</c> if the interface was found; otherwise <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(FrameInterfaceId id, [NotNullWhen(true)] out FrameInterfaceInfo? info)
    {
        Snapshot<FrameInterfaceInfo> snapshot = Volatile.Read(ref _Interfaces);
        if ((uint)id.Value < (uint)snapshot.Count)
        {
            info = snapshot.Items[id.Value];
            return true;
        }

        info = null;
        return false;
    }

    /// <summary>
    /// Returns a snapshot of all registered interfaces. The returned array is immutable
    /// and will not change as new interfaces are registered; interfaces registered later are not part of an already
    /// returned span. Allocation-free (a span over the published array).
    /// </summary>
    public ReadOnlySpan<FrameInterfaceInfo> All
    {
        get
        {
            // One snapshot load: the span covers exactly the entries that were registered at that moment. Slots below the
            // count are never mutated, so the span stays valid while other threads register.
            Snapshot<FrameInterfaceInfo> snapshot = Volatile.Read(ref _Interfaces);
            return new ReadOnlySpan<FrameInterfaceInfo>(snapshot.Items, 0, snapshot.Count);
        }
    }

    #endregion

    #region Frame sources

    /// <summary>Number of registered frame sources.</summary>
    public int SourceCount => Volatile.Read(ref _Sources).Count;

    /// <summary>
    /// Registers a new frame source. Thread-safe (lock-free, CAS retry).
    /// <para>
    /// After registration the source receives its <see cref="FrameSourceId"/> and can use it
    /// to register interfaces via <see cref="Register(FrameSourceId, string, string?, LinkType?, IReadOnlyDictionary{string, object}?)"/>.
    /// </para>
    /// </summary>
    /// <param name="source">The frame source to register. Must not be <see langword="null"/>.</param>
    /// <returns>The unique identifier assigned to the new source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The source count has reached <see cref="ArrayIndexIdRange.MaxValue"/> + 1.</exception>
    public FrameSourceId RegisterSource(IFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // CAS retry loop for lock-free registration. Same scheme as Register: the id is the count of the snapshot, so it is
        // recomputed after a lost race.
        while (true)
        {
            Snapshot<FrameSourceInfo> current = Volatile.Read(ref _Sources);
            int count = current.Count;
            if (count > ArrayIndexIdRange.MaxValue)
            {
                throw new InvalidOperationException(
                    Invariant($"Maximum frame source count exceeded (valid range 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)})."));
            }

            FrameSourceId id = new(count);
            FrameSourceInfo newInfo = new(id, source);
            if (_TryAppend(ref _Sources, current, newInfo))
            {
                return id;
            }
        }
    }

    /// <summary>
    /// Gets frame source info by identifier. Wait-free O(1) lookup.
    /// </summary>
    /// <param name="id">The source identifier to look up.</param>
    /// <returns>The source info, or <c>null</c> if the ID is out of range.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FrameSourceInfo? GetSource(FrameSourceId id)
    {
        Snapshot<FrameSourceInfo> snapshot = Volatile.Read(ref _Sources);
        if ((uint)id.Value < (uint)snapshot.Count)
        {
            return snapshot.Items[id.Value];
        }

        return null;
    }

    /// <summary>
    /// Tries to get frame source info by identifier.
    /// </summary>
    /// <param name="id">The source identifier to look up.</param>
    /// <param name="info">When this method returns <c>true</c>, contains the source info.</param>
    /// <returns><c>true</c> if the source was found; otherwise <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSource(FrameSourceId id, [NotNullWhen(true)] out FrameSourceInfo? info)
    {
        Snapshot<FrameSourceInfo> snapshot = Volatile.Read(ref _Sources);
        if ((uint)id.Value < (uint)snapshot.Count)
        {
            info = snapshot.Items[id.Value];
            return true;
        }

        info = null;
        return false;
    }

    /// <summary>
    /// Returns a snapshot of all registered frame sources. Same snapshot rules as <see cref="All"/>.
    /// </summary>
    public ReadOnlySpan<FrameSourceInfo> AllSources
    {
        get
        {
            Snapshot<FrameSourceInfo> snapshot = Volatile.Read(ref _Sources);
            return new ReadOnlySpan<FrameSourceInfo>(snapshot.Items, 0, snapshot.Count);
        }
    }

    #endregion
}
