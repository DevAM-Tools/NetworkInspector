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
/// </summary>
public sealed class FrameInterfaceRegistry
{
    /// <summary>
    /// The current snapshot of registered interfaces.
    /// Replaced atomically on every registration via copy-on-write
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>).
    /// All reads MUST go through <see cref="System.Threading.Volatile"/> Read
    /// to observe a consistent, immutable snapshot under the .NET memory model.
    /// </summary>
    private volatile FrameInterfaceInfo[] _Interfaces = [];

    /// <summary>
    /// The current snapshot of registered frame sources.
    /// Replaced atomically on every registration via copy-on-write
    /// (<see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>).
    /// All reads MUST go through <see cref="System.Threading.Volatile"/> Read.
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
    public FrameInterfaceId Register(
        FrameSourceId sourceId, string uiName, string? description = null,
        LinkType? linkType = null, IReadOnlyDictionary<string, object>? properties = null)
    {
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

        if ((uint)sourceId.Value >= (uint)_Sources.Length)
        {
            throw new ArgumentException(
                $"Frame source ID {sourceId.Value} is not registered.", nameof(sourceId));
        }

        // CAS retry loop: copy-on-write for lock-free registration
        while (true)
        {
            FrameInterfaceInfo[] current = _Interfaces;
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
        // Read the snapshot once — consistent even if another thread is registering
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
    /// and will not change as new interfaces are registered.
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
    public FrameSourceId RegisterSource(IFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // CAS retry loop: copy-on-write for lock-free registration
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
    /// Returns a snapshot of all registered frame sources.
    /// </summary>
    public ReadOnlySpan<FrameSourceInfo> AllSources => _Sources;
    #endregion
}
