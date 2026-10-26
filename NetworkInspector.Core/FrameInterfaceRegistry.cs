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
/// <b>Why copy-on-write:</b> every parsed packet reads the registry (the frame's interface is looked up and the registry
/// reference is compared on the parse path), while registrations happen a handful of times when a source opens. A lock or
/// reader-writer lock would put a synchronization cost on every read; here a read is one volatile load of an immutable
/// array. A registration pays instead: it copies the array (O(n) in the registered count, which stays tiny) and publishes
/// the new array with a compare-exchange, retrying if another registration won the race. Ids are the array indexes, so
/// they are dense, assigned in registration order, and never reused or removed.
/// </para>
/// <para>
/// <b>Thread-safety:</b> all members are safe to call from any thread. Published arrays are never mutated, so a snapshot
/// (including the spans returned by <see cref="All"/> and <see cref="AllSources"/>) stays valid and consistent while other
/// threads register. Entries are only ever appended: an id that resolved once resolves forever.
/// </para>
/// </summary>
public sealed class FrameInterfaceRegistry
{
    /// <summary>
    /// The current snapshot of registered interfaces.
    /// Replaced atomically on every registration via copy-on-write
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>).
    /// The field is <see langword="volatile"/>, so a plain read is an acquire read: it observes a fully constructed array.
    /// Readers load it once into a local and use only that local, so they see one consistent immutable snapshot.
    /// </summary>
    private volatile FrameInterfaceInfo[] _Interfaces = [];

    /// <summary>
    /// The current snapshot of registered frame sources.
    /// Replaced atomically on every registration via copy-on-write
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>).
    /// Same volatile-snapshot read rule as <see cref="_Interfaces"/>.
    /// </summary>
    private volatile FrameSourceInfo[] _Sources = [];

    /// <summary>Creates an empty registry.</summary>
    public FrameInterfaceRegistry()
    {
    }

    #region Interface Registration

    /// <summary>Number of registered interfaces.</summary>
    public int Count => _Interfaces.Length;

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
        if ((uint)sourceId.Value >= (uint)_Sources.Length)
        {
            throw new ArgumentException(
                $"Frame source ID {sourceId.Value} is not registered.", nameof(sourceId));
        }

        // CAS retry loop: copy-on-write for lock-free registration. The id and the info are rebuilt on each attempt from the
        // freshest snapshot, because the id is the array length and a concurrent registration may have taken it.
        while (true)
        {
            FrameInterfaceInfo[] current = _Interfaces;

            // Capacity guard: ids are array indexes, so the count is capped by what an id can hold. Exceeding it is a
            // broken-invariant condition at registration time (not an expected per-packet failure), hence an exception.
            if (current.Length > ArrayIndexIdRange.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Maximum frame interface count exceeded (valid range 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)}).");
            }

            FrameInterfaceId id = new(current.Length);
            FrameInterfaceInfo newInfo = new(id, sourceId, uiName, description, linkType, properties);

            // Create a new array with the additional entry appended
            FrameInterfaceInfo[] updated = new FrameInterfaceInfo[current.Length + 1];
            current.CopyTo(updated, 0);
            updated[current.Length] = newInfo;

            // Atomically swap if no other thread modified the array in the meantime
            if (Interlocked.CompareExchange(ref _Interfaces, updated, current) == current)
            {
                return id;
            }
            // Another thread registered concurrently — retry with the new snapshot
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
        FrameInterfaceInfo[] snapshot = _Interfaces;
        if ((uint)id.Value < (uint)snapshot.Length)
        {
            return snapshot[id.Value];
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
        FrameInterfaceInfo[] snapshot = _Interfaces;
        if ((uint)id.Value < (uint)snapshot.Length)
        {
            info = snapshot[id.Value];
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
    public ReadOnlySpan<FrameInterfaceInfo> All => _Interfaces;

    #endregion

    #region Frame Source Registration

    /// <summary>Number of registered frame sources.</summary>
    public int SourceCount => _Sources.Length;

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

        // CAS retry loop: copy-on-write for lock-free registration. Same scheme as Register: the id is the array length of
        // the snapshot, so it is recomputed after a lost race.
        while (true)
        {
            FrameSourceInfo[] current = _Sources;
            if (current.Length > ArrayIndexIdRange.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Maximum frame source count exceeded (valid range 0..{ArrayIndexIdRange.MaxValue.ToString(CultureInfo.InvariantCulture)}).");
            }

            FrameSourceId id = new(current.Length);
            FrameSourceInfo newInfo = new(id, source);

            FrameSourceInfo[] updated = new FrameSourceInfo[current.Length + 1];
            current.CopyTo(updated, 0);
            updated[current.Length] = newInfo;

            if (Interlocked.CompareExchange(ref _Sources, updated, current) == current)
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
        FrameSourceInfo[] snapshot = _Sources;
        if ((uint)id.Value < (uint)snapshot.Length)
        {
            return snapshot[id.Value];
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
        FrameSourceInfo[] snapshot = _Sources;
        if ((uint)id.Value < (uint)snapshot.Length)
        {
            info = snapshot[id.Value];
            return true;
        }
        info = null;
        return false;
    }

    /// <summary>
    /// Returns a snapshot of all registered frame sources. Same snapshot rules as <see cref="All"/>.
    /// </summary>
    public ReadOnlySpan<FrameSourceInfo> AllSources => _Sources;
    #endregion
}
