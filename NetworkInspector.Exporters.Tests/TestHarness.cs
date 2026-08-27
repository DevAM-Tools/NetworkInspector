// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Per-test-thread stack and dense packet-id allocator.
/// First parses on a stack must be <c>0, 1, 2, …</c>; a process-wide shared stack with
/// interleaved ids from parallel tests would jump and throw. Each calling thread therefore
/// owns its own <see cref="Stack"/> and id sequence.
/// </summary>
internal static class TestHarness
{
    /// <summary>Stack owned by the current test thread. Built on first use.</summary>
    [ThreadStatic]
    private static Stack? _ThreadStack;

    /// <summary>Next first-parse packet id on this thread's stack. Starts at 0.</summary>
    [ThreadStatic]
    private static int _ThreadNextPacketId;

    /// <summary>
    /// Per-thread cache of <see cref="FrameInterfaceId"/> values keyed by <see cref="LinkType"/>.
    /// Avoids registering a new interface for every <see cref="CreateFrame"/> call.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<LinkType, FrameInterfaceId>? _ThreadInterfaceIds;

    /// <summary>
    /// Returns this thread's <see cref="Stack"/> with all standard protocols registered.
    /// Safe for concurrent tests: each thread has its own instance.
    /// </summary>
    internal static Stack GetStack()
    {
        Stack? cached = _ThreadStack;
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
            _ThreadStack = stack;
            return stack;
        }
        finally
        {
            settingsManager?.Dispose();
        }
    }

    /// <summary>
    /// Returns the next dense first-parse <see cref="PacketId"/> for this thread's stack
    /// (<c>0, 1, 2, …</c>).
    /// </summary>
    internal static PacketId NextPacketId() =>
        new(_ThreadNextPacketId++);

    /// <summary>
    /// Creates a <see cref="Frame"/> from raw data, registering the interface in this
    /// thread's stack <see cref="FrameInterfaceRegistry"/> if needed.
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
        Dictionary<LinkType, FrameInterfaceId> interfaceIds = _ThreadInterfaceIds ??= [];
        if (!interfaceIds.TryGetValue(linkType, out FrameInterfaceId ifId))
        {
            ifId = registry.Register(sourceId, $"test_{linkType}", null, linkType);
            interfaceIds[linkType] = ifId;
        }

        return Frame.Create(id, Timestamp.FromNanos(timestampNanos), data, linkType, ifId, registry).Value;
    }

    /// <summary>
    /// Parses a <see cref="Frame"/> into a <see cref="Packet"/> using this thread's stack
    /// and the next dense packet id.
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
