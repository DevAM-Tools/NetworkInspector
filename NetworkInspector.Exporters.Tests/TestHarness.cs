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
    /// Builds a stack and parses one frame before the first test runs, so the one-time cost of type initialization and JIT
    /// compilation of the stack, the parser, and the exporters' shared code is not billed to whichever test happens to run first.
    /// </summary>
    [Before(Assembly)]
    public static void WarmUp()
    {
        _ = PacketGenerators.CreateParsedPacket(0, FrameGenerators.BuildEthernetIpv4UdpFrame(4));
    }

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

            // IPv4 is not registered at ip.proto 4 by default. The tunnel frame of the content tests (IPv4 in IPv4) needs the
            // nested layer: it is the one packet in which a single field (ip.src) occurs twice, which the occurrence policy of
            // the export content (first / last / all) is defined for.
            builder.RegisterParserInU64TableByName(IPv4Protocol.IpProtoTableName, 4, builder.GetProtocolId("ip")!.Value);
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
    internal static Packet ParseFrame(Frame frame)
    {
        ParseOptions options = new();
        if (!Packet.TryParse(NextPacketId(), GetStack(), frame, in options, out Packet? packet, out ParseFailure failure)
            || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    /// <summary>
    /// Parses a <see cref="Frame"/> with the given options and the next dense packet id, recycling <paramref name="recycle"/>
    /// when the stack accepts it. Throws on a failed parse.
    /// </summary>
    /// <param name="frame">The frame to parse.</param>
    /// <param name="options">Parse options (for example a tree-free parse with an observer).</param>
    /// <param name="recycle">A sealed packet whose storage is reused, or <see langword="null"/>.</param>
    internal static Packet ParseFrame(Frame frame, in ParseOptions options, Packet? recycle = null)
    {
        PacketId id = NextPacketId();
        if (recycle is not null && Packet.TryParse(recycle, id, GetStack(), frame, in options, out _))
        {
            return recycle;
        }

        if (!Packet.TryParse(id, GetStack(), frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

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
