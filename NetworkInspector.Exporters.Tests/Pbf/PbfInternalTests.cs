// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for internal exporter types: typed <see cref="PreviousFieldStore"/> (sparse mode),
/// <see cref="ColumnarBlockBuilder"/> (MaxBlockSize flush), and <see cref="Lz4Compressor"/>
/// (incompressible data). These complement the higher-level <see cref="PbfExporterTests"/>.
/// </summary>
internal sealed class PbfInternalTests
{
    // ========================================================================
    // PreviousFieldStore sparse mode (> 2048 distinct field IDs)
    // ========================================================================

    /// <summary>
    /// When constructed with a <c>fieldCount</c> above the dense threshold
    /// (2048), the store must use the sparse (Dictionary) representation and still
    /// correctly detect same-as-previous values.
    /// </summary>
    [Test]
    public async Task PreviousFieldStore_SparseMode_DetectsSameAsPrevious()
    {
        // fieldCount > 2048 forces sparse mode
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;
        FieldValue hello = FieldValue.NewString("hello");

        // First call — nothing stored yet, no same flags
        uint flags1 = store.CompareAndUpdate(fieldId, hello, default, default);

        // Second call with same value — should detect SameValue
        uint flags2 = store.CompareAndUpdate(fieldId, hello, default, default);

        await Assert.That((flags1 & SameFlags.FieldSameValue) != 0).IsFalse();
        await Assert.That((flags2 & SameFlags.FieldSameValue) != 0).IsTrue();
    }

    [Test]
    public async Task PreviousFieldStore_SparseMode_DetectsValueChange()
    {
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;

        store.CompareAndUpdate(fieldId, FieldValue.NewString("hello"), default, default);
        uint flags = store.CompareAndUpdate(fieldId, FieldValue.NewString("world"), default, default);

        await Assert.That((flags & SameFlags.FieldSameValue) != 0).IsFalse();
    }

    [Test]
    public async Task PreviousFieldStore_SparseMode_Reset_ClearsPreviousValues()
    {
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;
        FieldValue hello = FieldValue.NewString("hello");
        store.CompareAndUpdate(fieldId, hello, default, default);

        store.Reset();

        // After reset the value should appear new again
        uint flags = store.CompareAndUpdate(fieldId, hello, default, default);
        await Assert.That((flags & SameFlags.FieldSameValue) != 0).IsFalse();
    }

    // ========================================================================
    // ColumnarBlockBuilder MaxBlockSize flush
    // ========================================================================

    /// <summary>
    /// <see cref="ColumnarBlockBuilder.AddPacket"/> must return <c>true</c>
    /// (flush signal) when accumulated data exceeds the configured
    /// <c>maxBlockSize</c> threshold, not only when packet count is reached.
    /// </summary>
    [Test]
    public async Task ColumnarBlockBuilder_FlushTriggeredByMaxBlockSize()
    {
        // Set a tiny maxBlockSize (100 bytes) with a large maxPacketsPerBlock so that
        // only the size threshold can trigger a flush.
        int maxPacketsPerBlock = 10_000;
        int maxBlockSize = 100; // bytes — will be exceeded after a few packets
        int maxFieldId = 32768;

        ColumnarBlockBuilder builder = new(maxFieldId, maxPacketsPerBlock, maxBlockSize);

        bool flushedBySize = false;
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(50);

        foreach (Packet packet in packets)
        {
            if (builder.AddPacket(packet))
            {
                flushedBySize = true;
                break;
            }
        }

        builder.Dispose();

        await Assert.That(flushedBySize).IsTrue();
    }

    // ========================================================================
    // Lz4Compressor incompressible data
    // ========================================================================

    /// <summary>
    /// When the input data is incompressible and the compressed output would
    /// exceed the output buffer, <see cref="Lz4Compressor.Compress"/> must return -1
    /// to signal that compression should be abandoned.
    /// </summary>
    [Test]
    public async Task Lz4Compressor_IncompressibleData_SmallOutputBuffer_ReturnsMinusOne()
    {
        // Random-looking bytes that resist compression (one of the simplest patterns:
        // sequential XOR with a prime to avoid any repeated sequences).
        byte[] input = new byte[1024];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i * 7 + 13);
        }

        // Destination that is far too small to hold the compressed output.
        byte[] output = new byte[16];

        int result = Lz4Compressor.Compress(input, output);

        await Assert.That(result).IsEqualTo(-1);
    }

    [Test]
    public async Task Lz4Compressor_CompressibleData_ReturnsPositiveLength()
    {
        // Highly compressible: all zeros
        byte[] input = new byte[4096];
        byte[] output = new byte[Lz4Compressor.MaxCompressedSize(input.Length)];

        int result = Lz4Compressor.Compress(input, output);

        await Assert.That(result > 0).IsTrue();
        await Assert.That(result < input.Length).IsTrue();
    }

    /// <summary>
    /// Data compressed via <see cref="Lz4Compressor"/> (which delegates to
    /// <see cref="Lz4Codec"/>) must decompress back to the original via
    /// <see cref="Lz4Codec.Decompress"/>.
    /// </summary>
    [Test]
    public async Task Lz4Compressor_Roundtrip_DecompressViaLz4Codec_RecoversOriginal()
    {
        byte[] input = new byte[8 * 1024];
        Array.Fill(input, (byte)0x42);

        byte[] compressed = new byte[Lz4Compressor.MaxCompressedSize(input.Length)];
        int compressedLen = Lz4Compressor.Compress(input, compressed);
        await Assert.That(compressedLen > 0).IsTrue();

        byte[] recovered = new byte[input.Length];
        int written = Lz4Codec.Decompress(compressed.AsSpan(0, compressedLen), recovered);

        await Assert.That(written).IsEqualTo(input.Length);
        await Assert.That(recovered.AsSpan().SequenceEqual(input)).IsTrue();
    }

    // ========================================================================
    // ColumnarBlockBuilder MinTimestamp / MaxTimestamp after Build()
    // ========================================================================

    /// <summary>
    /// <see cref="ColumnarBlockBuilder.MinTimestamp"/> and
    /// <see cref="ColumnarBlockBuilder.MaxTimestamp"/> must reflect the actual
    /// minimum and maximum timestamps of all packets added to the block once
    /// <see cref="ColumnarBlockBuilder.Build"/> has been called.
    /// This ensures that <see cref="NetworkInspector.Exporters.Pbf.PbfExporter"/>
    /// writes correct block-index entries in the PBF trailer for columnar blocks.
    /// </summary>
    [Test]
    public async Task ColumnarBlockBuilder_Build_MinMaxTimestamp_MatchPacketTimestamps()
    {
        // Three packets with timestamps 0, 1 000 000, and 2 000 000 nanoseconds.
        // PacketGenerators.CreateEthernetUdpPackets uses timestamp = i * 1_000_000.
        int maxFieldId = 32768;
        ColumnarBlockBuilder builder = new(maxFieldId);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(3);
        foreach (Packet packet in packets)
        {
            builder.AddPacket(packet);
        }

        // Build must compute and expose min/max before returning.
        builder.Build();

        long expectedMin = 0L;             // packet 0: 0 * 1_000_000
        long expectedMax = 2_000_000L;     // packet 2: 2 * 1_000_000

        await Assert.That(builder.MinTimestamp).IsEqualTo(expectedMin);
        await Assert.That(builder.MaxTimestamp).IsEqualTo(expectedMax);

        builder.Dispose();
    }

    /// <summary>
    /// When the block contains only one packet, MinTimestamp and MaxTimestamp must
    /// both equal that packet's timestamp.
    /// </summary>
    [Test]
    public async Task ColumnarBlockBuilder_Build_SinglePacket_MinMaxTimestampAreEqual()
    {
        int maxFieldId = 32768;
        ColumnarBlockBuilder builder = new(maxFieldId);

        long ts = 5_000_000L; // 5 ms
        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(1);
        // Override timestamp via CreateParsedPacket so both min and max are 'ts'.
        byte[] frameData = FrameGenerators.BuildEthernetIpv4UdpFrame(32);
        Packet singlePacket = PacketGenerators.CreateParsedPacket(99, frameData, ts);

        builder.AddPacket(singlePacket);
        builder.Build();

        await Assert.That(builder.MinTimestamp).IsEqualTo(ts);
        await Assert.That(builder.MaxTimestamp).IsEqualTo(ts);

        builder.Dispose();
    }

    // ========================================================================
    // StandardBlockBuilder TruncatedFieldCount for deep nesting
    // ========================================================================

    /// <summary>
    /// <see cref="StandardBlockBuilder.TruncatedFieldCount"/> must be zero for
    /// packets whose protocol trees do not exceed <c>MaxNestingDepth</c> (16 levels).
    /// Standard Ethernet+IPv4+UDP packets produce a tree of at most 4 levels, so no
    /// truncation should occur.
    /// </summary>
    [Test]
    public async Task StandardBlockBuilder_TruncatedFieldCount_ZeroForShallowPackets()
    {
        int maxFieldId = 32768;
        StandardBlockBuilder builder = new(maxFieldId);

        Packet[] packets = PacketGenerators.CreateEthernetUdpPackets(5);
        foreach (Packet packet in packets)
        {
            builder.AddPacket(packet);
        }

        await Assert.That(builder.TruncatedFieldCount).IsEqualTo(0);

        builder.Dispose();
    }

    /// <summary>
    /// <see cref="StandardBlockBuilder.TruncatedFieldCount"/> must be incremented when a
    /// field at depth <c>MaxNestingDepth - 1</c> has children, and when
    /// <see cref="StandardBlockBuilder.SerializeField"/> is called at depth ≥
    /// <c>MaxNestingDepth</c>. A deep packet is constructed by parsing a frame through a
    /// custom <see cref="IProtocol"/> that appends 18 levels of nested container fields
    /// via the public <see cref="MutField.Append"/> API, so that the deepest field is
    /// beyond the 16-level serializer limit.
    /// </summary>
    [Test]
    public async Task StandardBlockBuilder_TruncatedFieldCount_IncrementedForDeeplyNestedPacket()
    {
        Packet packet = _CreateDeepPacket(18);

        int maxFieldId = 32768;
        StandardBlockBuilder builder = new(maxFieldId);
        builder.AddPacket(packet);

        // At least one truncation must have been recorded because the chain exceeds
        // MaxNestingDepth (16).
        await Assert.That(builder.TruncatedFieldCount > 0).IsTrue();

        builder.Dispose();
    }

    /// <summary>
    /// <see cref="StandardBlockBuilder.Reset"/> must clear
    /// <see cref="StandardBlockBuilder.TruncatedFieldCount"/> back to zero so
    /// that the counter does not accumulate across blocks.
    /// </summary>
    [Test]
    public async Task StandardBlockBuilder_Reset_ClearsTruncatedFieldCount()
    {
        Packet packet = _CreateDeepPacket(18);

        int maxFieldId = 32768;
        StandardBlockBuilder builder = new(maxFieldId);
        builder.AddPacket(packet);

        // Verify truncation occurred (pre-condition for the reset assertion below).
        await Assert.That(builder.TruncatedFieldCount > 0).IsTrue();

        // Reset and verify the counter is cleared.
        builder.Reset();
        await Assert.That(builder.TruncatedFieldCount).IsEqualTo(0);

        builder.Dispose();
    }

    // ========================================================================
    // PbfExporter strict mode — truncated-field early abort
    // ========================================================================

    /// <summary>
    /// When <see cref="PbfExporter.ErrorTolerance"/> is set to
    /// <see cref="ErrorToleranceMode.Strict"/> and a packet exceeds the maximum
    /// nesting depth (triggering <see cref="StandardBlockBuilder.TruncatedFieldCount"/>
    /// &gt; 0), <see cref="PbfExporter"/> must not write the incomplete block to the
    /// stream and must set <see cref="PbfExporter.HasErrors"/> to <c>true</c>.
    /// </summary>
    [Test]
    public async Task PbfExporter_StrictMode_TruncatedFieldAborts_BlockNotWritten()
    {
        Packet packet = _CreateDeepPacket(18);

        using MemoryStream baselineMs = new();
        using MemoryStream strictMs = new();

        // Tolerant mode: write the incomplete block
        using (PbfExporter tolerant = PbfExporter.CreateBuilder()
            .ToStream(baselineMs)
            .WithCompressed(false)
            .Build())
        {
            tolerant.ErrorTolerance = ErrorToleranceMode.Tolerant;
            tolerant.OnPacket(packet);
            tolerant.OnFinish();
        }

        // Strict mode: block must be suppressed
        using PbfExporter strict = PbfExporter.CreateBuilder()
            .ToStream(strictMs)
            .WithCompressed(false)
            .Build();
        strict.ErrorTolerance = ErrorToleranceMode.Strict;
        strict.OnPacket(packet);
        strict.OnFinish();

        // In tolerant mode the block is written so the output is larger than just the header.
        await Assert.That(baselineMs.Length).IsGreaterThan(0);

        // In strict mode the truncated block must be suppressed: fewer bytes than tolerant.
        await Assert.That(strictMs.Length).IsLessThan(baselineMs.Length);
        await Assert.That(strict.HasErrors).IsTrue();
        await Assert.That(strict.SkippedCount).IsEqualTo(1);
    }

    // ========================================================================
    // Helpers — deep-nesting packet factory
    // ========================================================================

    /// <summary>
    /// Builds a <see cref="Packet"/> whose field tree is <paramref name="depth"/> levels
    /// deep by parsing a raw frame through a dedicated <see cref="DeepNestingProtocol"/>
    /// registered at <see cref="LinkType.Null"/>.
    /// <para>
    /// Uses only the public <see cref="MutField.Append"/> API — no Core internals are
    /// accessed from test code.
    /// </para>
    /// </summary>
    /// <param name="depth">Number of nested container levels to emit.</param>
    private static Packet _CreateDeepPacket(int depth)
    {
        DeepNestingProtocol deepProtocol = new(depth);

        SettingsManager? settingsManager = new();
        Stack stack;
        try
        {
            FrameInterfaceRegistry registry = new();
            StackBuilder builder = new(settingsManager, registry);
            // Register all standard protocols so that frame-level dispatch
            // infrastructure (FrameProtocol, frame.link_type table) is present.
            builder.RegisterStandardProtocols();
            // Register our custom deep-nesting protocol and map it to LinkType.Null
            // (BSD loopback encapsulation, value 0) — not used by any standard protocol.
            builder.RegisterProtocol<DeepNestingProtocol>(
                deepProtocol,
                (b, id, _) =>
                {
                    b.RegisterField(id, "test.deep.level", "Level", FieldType.None);
                    b.RegisterParserInU64TableByName(
                        FrameProtocol.LinkTypeTableName, (ulong)LinkType.Null, id);
                });
            stack = builder.Build();
            settingsManager = null; // ownership transferred to stack
        }
        finally
        {
            settingsManager?.Dispose();
        }

        using (stack)
        {
            FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
            FrameSourceId sourceId = registry.RegisterSource(TestHarness.CreateNullFrameSource());
            FrameInterfaceId ifId = registry.Register(sourceId, "test_null", null, LinkType.Null);
            Frame frame = Frame.Create(
                new FrameId(1),
                Timestamp.FromNanos(1_000_000L),
                // Minimal payload — the protocol ignores the raw bytes.
                new byte[] { 0, 0, 0, 0 },
                LinkType.Null,
                ifId,
                registry).Value;

            Packet packet = Packet.ParseFrame(new PacketId(1), stack, frame);
            packet.MaterializeAll();
            return packet;
        }
    }

    /// <summary>
    /// Test-only protocol that appends a chain of <c>_Depth</c> nested container fields
    /// using only the public <see cref="MutField.Append"/> API.
    /// Registered at <see cref="LinkType.Null"/> on a dedicated test stack so that frames
    /// with that link type produce arbitrarily deep field trees.
    /// <para><b>Thread safety:</b> instances are immutable after registration; <see cref="Parse"/>
    /// may be called concurrently.</para>
    /// </summary>
    private sealed class DeepNestingProtocol : IProtocol
    {
        private readonly int _Depth;
        private FieldId _ContainerFieldId;

        /// <summary>Initialises the protocol with the desired nesting depth.</summary>
        internal DeepNestingProtocol(int depth)
        {
            _Depth = depth;
        }

        /// <inheritdoc/>
        public string Name => "test.deep";

        /// <inheritdoc/>
        public string UiName => "Deep Nesting Test";

        /// <inheritdoc/>
        public void OnStart(Stack stack)
        {
            // The field was already registered in the RegisterProtocol callback;
            // resolve its ID here so Parse can use it without a dictionary lookup.
            _ContainerFieldId = stack.GetFieldId("test.deep.level")
                ?? throw new InvalidOperationException("test.deep.level field not registered.");
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            // Build a linear chain: root → level[0] → level[1] → … → level[depth-1].
            // Declaring current as scoped constrains its safe-to-escape lifetime to this
            // method's scope, which matches the lifetime of Append's return value when
            // context is an in (ref-struct) parameter — required by C# 11 ref safety rules.
            scoped MutField current = parentField;
            for (int i = 0; i < _Depth; i++)
            {
                current = current.Append(_ContainerFieldId, FieldValue.None);
            }

            return 0; // consumed bytes irrelevant for this test protocol
        }
    }
}
