// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Roundtrip;

/// <summary>
/// Per-test frame fabricator that owns its own <see cref="FrameInterfaceRegistry"/>.
/// Roundtrip tests must avoid the shared singleton in <see cref="TestHarness"/> because
/// they assert interface mappings end-to-end; sharing a registry across tests would
/// cause spurious cross-test interactions.
/// </summary>
internal sealed class RoundtripFrameFactory
{
    /// <summary>The per-test registry exposed for direct interface registration.</summary>
    internal FrameInterfaceRegistry Registry
    {
        get;
    }

    /// <summary>The synthetic source id used for all interfaces created here.</summary>
    private readonly FrameSourceId _SourceId;

    /// <summary>Monotonic frame id counter so tests don't have to track ids manually.</summary>
    private int _NextFrameId;

    /// <summary>
    /// Creates a fresh registry and registers a stub source so interfaces can be added.
    /// </summary>
    internal RoundtripFrameFactory()
    {
        Registry = new FrameInterfaceRegistry();
        _SourceId = Registry.RegisterSource(StubSource.Instance);
    }

    /// <summary>
    /// Registers a new interface and returns its id. Callers can pass extra properties
    /// (e.g. <see cref="FrameInterfacePropertyKeys.BlfChannel"/>) used by the BLF
    /// exporter for channel routing.
    /// </summary>
    internal FrameInterfaceId AddInterface(
        string uiName, LinkType linkType,
        IReadOnlyDictionary<string, object>? properties = null) =>
        Registry.Register(_SourceId, uiName, null, linkType, properties);

    /// <summary>
    /// Builds a frame with auto-incremented id on the given interface. Timestamps are
    /// nanoseconds since Unix epoch — exactly what tshark <c>frame.time_epoch</c> reports.
    /// </summary>
    internal Frame Create(FrameInterfaceId interfaceId, LinkType linkType, long timestampNs, byte[] data) =>
        Frame.Create(
            new FrameId(_NextFrameId++),
            Timestamp.FromNanos(timestampNs),
            data,
            linkType,
            interfaceId,
            Registry).Value;

    /// <summary>Minimal <see cref="IFrameSource"/> stub for interface registration only.</summary>
    private sealed class StubSource : IFrameSource
    {
        /// <summary>Singleton — the stub carries no per-instance state.</summary>
        internal static readonly StubSource Instance = new();

        /// <inheritdoc/>
        public string UiName => "roundtrip-test";

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
