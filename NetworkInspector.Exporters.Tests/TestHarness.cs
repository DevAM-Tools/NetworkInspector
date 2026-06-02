// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Shared test infrastructure. Provides a lazily-initialized <see cref="Stack"/>
/// with all standard protocols registered.
/// </summary>
internal static class TestHarness
{
    /// <summary>
    /// Cached stack instance. Written once under <see cref="_Lock"/>; read without lock
    /// via <see cref="Volatile.Read{T}"/> to avoid the lock on the fast path.
    /// </summary>
    private static Stack? _Stack;

    /// <summary>
    /// Guards lazy initialisation of <see cref="_Stack"/>.
    /// Held only for the duration of the construction call; never held across I/O.
    /// </summary>
    private static readonly Lock _Lock = new();

    /// <summary>
    /// Monotonically increasing counter used to assign unique packet IDs.
    /// Incremented atomically via <see cref="Interlocked.Increment(ref long)"/>.
    /// </summary>
    private static long _NextPacketId;

    /// <summary>
    /// Cache of <see cref="FrameInterfaceId"/> values keyed by <see cref="LinkType"/>.
    /// Avoids registering a new interface for every <see cref="CreateFrame"/> call, which
    /// would cause 10 000+ registrations in bulk tests and grow the registry unboundedly.
    /// </summary>
    private static readonly Dictionary<LinkType, FrameInterfaceId> _InterfaceIds = new();

    /// <summary>
    /// Returns a shared <see cref="Stack"/> with all standard protocols registered.
    /// Thread-safe; the instance is created once and reused.
    /// </summary>
    internal static Stack GetStack()
    {
        Stack? cached = Volatile.Read(ref _Stack);
        if (cached is not null)
        {
            return cached;
        }

        lock (_Lock)
        {
            cached = Volatile.Read(ref _Stack);
            if (cached is not null)
            {
                return cached;
            }

            SettingsManager? settingsManager = new();
            try
            {
                FrameInterfaceRegistry registry = new();
                StackBuilder builder = new(settingsManager, registry);
                builder.RegisterStandardProtocols();
                Stack stack = builder.Build();
                settingsManager = null; // ownership transferred to stack
                Volatile.Write(ref _Stack, stack);
                return stack;
            }
            finally
            {
                settingsManager?.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns a unique <see cref="PacketId"/> for each call. Thread-safe.
    /// </summary>
    internal static PacketId NextPacketId() =>
        new((int)Interlocked.Increment(ref _NextPacketId));

    /// <summary>
    /// Creates a <see cref="Frame"/> from raw data, registering the interface in the
    /// shared stack's <see cref="FrameInterfaceRegistry"/> if needed.
    /// </summary>
    /// <param name="id">Frame identifier.</param>
    /// <param name="timestampNanos">Timestamp in nanoseconds.</param>
    /// <param name="data">Raw frame bytes.</param>
    /// <param name="linkType">Link-layer type.</param>
    /// <returns>A valid <see cref="Frame"/>.</returns>
    internal static Frame CreateFrame(
        FrameId id, long timestampNanos, byte[] data, LinkType linkType = LinkType.Ethernet)
    {
        Stack stack = GetStack();
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;

        // Register a NullFrameSource if no source exists yet
        if (registry.SourceCount == 0)
        {
            registry.RegisterSource(NullFrameSource.Instance);
        }

        FrameSourceId sourceId = new(0);

        // Reuse an existing interface registration for the same LinkType so that bulk
        // tests that call CreateFrame thousands of times do not grow the registry unboundedly.
        lock (_Lock)
        {
            if (!_InterfaceIds.TryGetValue(linkType, out FrameInterfaceId ifId))
            {
                ifId = registry.Register(sourceId, $"test_{linkType}", null, linkType);
                _InterfaceIds[linkType] = ifId;
            }

            return Frame.Create(id, Timestamp.FromNanos(timestampNanos), data, linkType, ifId, registry).Value;
        }
    }

    /// <summary>
    /// Parses a <see cref="Frame"/> into a <see cref="Packet"/> using the shared stack.
    /// </summary>
    internal static Packet ParseFrame(Frame frame) =>
        Packet.ParseFrame(NextPacketId(), GetStack(), frame);

    /// <summary>
    /// Creates a <see cref="NullFrameSource"/> instance for test interface registration.
    /// </summary>
    internal static IFrameSource CreateNullFrameSource() => NullFrameSource.Instance;

    /// <summary>Minimal <see cref="IFrameSource"/> stub for interface registration.</summary>
    private sealed class NullFrameSource : IFrameSource
    {
        /// <summary>Shared singleton instance.</summary>
        internal static readonly NullFrameSource Instance = new();

        /// <inheritdoc/>
        public string UiName => "test";

        /// <inheritdoc/>
        public string? Description => null;

        /// <inheritdoc/>
        public int? EstimatedFrameCount => null;

        /// <inheritdoc/>
        public bool IsRunning => false;

        /// <inheritdoc/>
        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        /// <inheritdoc/>
        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }
}
