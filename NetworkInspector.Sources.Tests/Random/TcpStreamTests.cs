// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Random;

/// <summary>
/// Tests for stateless TCP stream generation via <see cref="TcpStreamFrameBuilder"/>
/// and <see cref="TcpStreamLayout"/>. Verifies determinism (same seed → same frames),
/// correct TCP connection lifecycle (SYN → data → FIN), sequence number consistency,
/// bidirectional traffic, and random-access reproducibility.
/// </summary>
internal sealed class TcpStreamTests
{
    // ─── TCP flag constants ──────────────────────────────────────────────────
    private const byte FlagSyn = 0x02;
    private const byte FlagAck = 0x10;
    private const byte FlagPsh = 0x08;
    private const byte FlagFin = 0x01;
    private const byte FlagSynAck = FlagSyn | FlagAck;

    // ─── Protocol offsets (Ethernet + IPv4) ──────────────────────────────────
    private const int EthHeaderSize = 14;
    private const int IPv4HeaderSize = 20;
    private const int TcpOffset = EthHeaderSize + IPv4HeaderSize;



    /// <summary>
    /// Extracts TCP flags byte from a frame's raw data at the standard
    /// Ethernet + IPv4 + TCP offset (byte 47).
    /// </summary>
    private static byte GetTcpFlags(ReadOnlySpan<byte> data) =>
        data[TcpOffset + 13];

    /// <summary>
    /// Extracts TCP sequence number from a frame.
    /// </summary>
    private static uint GetTcpSeqNum(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[(TcpOffset + 4)..]);

    /// <summary>
    /// Extracts TCP acknowledgment number from a frame.
    /// </summary>
    private static uint GetTcpAckNum(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[(TcpOffset + 8)..]);

    /// <summary>
    /// Extracts TCP source port from a frame.
    /// </summary>
    private static ushort GetTcpSrcPort(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[TcpOffset..]);

    /// <summary>
    /// Extracts TCP destination port from a frame.
    /// </summary>
    private static ushort GetTcpDstPort(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[(TcpOffset + 2)..]);

    // ========================================================================
    // TcpStreamLayout
    // ========================================================================

    [Test]
    public async Task Layout_CalculatesCorrectTotalFrames()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 4,
            SegmentsPerStream = 10,
            IncludeHandshake = true,
            IncludeTeardown = true,
        };
        TcpStreamLayout layout = new(options);

        // 3 handshake + 10 data + 4 teardown = 17 per connection × 4 = 68
        await Assert.That(layout.FramesPerConnection).IsEqualTo(17);
        await Assert.That(layout.TotalFrameCount).IsEqualTo(68);
        await Assert.That(layout.HandshakeFrames).IsEqualTo(3);
        await Assert.That(layout.DataFrames).IsEqualTo(10);
        await Assert.That(layout.TeardownFrames).IsEqualTo(4);
    }

    [Test]
    public async Task Layout_NoHandshakeNoTeardown()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 2,
            SegmentsPerStream = 5,
            IncludeHandshake = false,
            IncludeTeardown = false,
        };
        TcpStreamLayout layout = new(options);

        await Assert.That(layout.FramesPerConnection).IsEqualTo(5);
        await Assert.That(layout.TotalFrameCount).IsEqualTo(10);
    }

    [Test]
    public async Task Layout_Locate_Sequential()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 2,
            SegmentsPerStream = 3,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        // 3 + 3 + 4 = 10 frames per connection, 2 connections = 20 total
        // Frame 0-9: stream 0, Frame 10-19: stream 1
        TcpFrameLocation? loc0 = layout.Locate(0);
        await Assert.That(loc0).IsNotNull();
        await Assert.That(loc0!.Value.StreamIndex).IsEqualTo(0);
        await Assert.That(loc0.Value.LocalFrameIndex).IsEqualTo(0);

        TcpFrameLocation? loc9 = layout.Locate(9);
        await Assert.That(loc9).IsNotNull();
        await Assert.That(loc9!.Value.StreamIndex).IsEqualTo(0);
        await Assert.That(loc9.Value.LocalFrameIndex).IsEqualTo(9);

        TcpFrameLocation? loc10 = layout.Locate(10);
        await Assert.That(loc10).IsNotNull();
        await Assert.That(loc10!.Value.StreamIndex).IsEqualTo(1);
        await Assert.That(loc10.Value.LocalFrameIndex).IsEqualTo(0);

        // Out of range
        await Assert.That(layout.Locate(20)).IsNull();
        await Assert.That(layout.Locate(-1)).IsNull();
    }

    [Test]
    public async Task Layout_Locate_Interleaved()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 3,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = true,
        };
        TcpStreamLayout layout = new(options);

        // 3 + 2 + 4 = 9 per connection, 3 connections = 27 total
        // Interleaved: frame 0 → stream 0 local 0, frame 1 → stream 1 local 0, ...

        TcpFrameLocation? loc0 = layout.Locate(0);
        await Assert.That(loc0!.Value.StreamIndex).IsEqualTo(0);
        await Assert.That(loc0.Value.LocalFrameIndex).IsEqualTo(0);

        TcpFrameLocation? loc1 = layout.Locate(1);
        await Assert.That(loc1!.Value.StreamIndex).IsEqualTo(1);
        await Assert.That(loc1.Value.LocalFrameIndex).IsEqualTo(0);

        TcpFrameLocation? loc3 = layout.Locate(3);
        await Assert.That(loc3!.Value.StreamIndex).IsEqualTo(0);
        await Assert.That(loc3.Value.LocalFrameIndex).IsEqualTo(1);
    }

    [Test]
    public async Task Layout_ClassifyPhase_CorrectBoundaries()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 5,
            IncludeHandshake = true,
            IncludeTeardown = true,
        };
        TcpStreamLayout layout = new(options);

        // 0, 1, 2 = handshake
        await Assert.That(layout.ClassifyPhase(0)).IsEqualTo(TcpFramePhase.Handshake);
        await Assert.That(layout.ClassifyPhase(1)).IsEqualTo(TcpFramePhase.Handshake);
        await Assert.That(layout.ClassifyPhase(2)).IsEqualTo(TcpFramePhase.Handshake);

        // 3..7 = data
        await Assert.That(layout.ClassifyPhase(3)).IsEqualTo(TcpFramePhase.Data);
        await Assert.That(layout.ClassifyPhase(7)).IsEqualTo(TcpFramePhase.Data);

        // 8..11 = teardown
        await Assert.That(layout.ClassifyPhase(8)).IsEqualTo(TcpFramePhase.Teardown);
        await Assert.That(layout.ClassifyPhase(11)).IsEqualTo(TcpFramePhase.Teardown);
    }

    // ========================================================================
    // TCP Stream — Handshake verification
    // ========================================================================

    [Test]
    public async Task TcpStream_Handshake_SynSynAckAck()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = false,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;
        byte[]? syn = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, false);
        byte[]? synAck = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 1, false);
        byte[]? ack = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 2, false);

        await Assert.That(syn).IsNotNull();
        await Assert.That(synAck).IsNotNull();
        await Assert.That(ack).IsNotNull();

        // Verify flags
        await Assert.That(GetTcpFlags(syn!)).IsEqualTo(FlagSyn);
        await Assert.That(GetTcpFlags(synAck!)).IsEqualTo(FlagSynAck);
        await Assert.That(GetTcpFlags(ack!)).IsEqualTo(FlagAck);

        // SYN: ack = 0
        await Assert.That(GetTcpAckNum(syn!)).IsEqualTo(0u);

        // SYN-ACK: ack = client ISN + 1
        uint clientIsn = GetTcpSeqNum(syn!);
        await Assert.That(GetTcpAckNum(synAck!)).IsEqualTo(clientIsn + 1);

        // ACK: seq = client ISN + 1, ack = server ISN + 1
        uint serverIsn = GetTcpSeqNum(synAck!);
        await Assert.That(GetTcpSeqNum(ack!)).IsEqualTo(clientIsn + 1);
        await Assert.That(GetTcpAckNum(ack!)).IsEqualTo(serverIsn + 1);
    }

    // ========================================================================
    // TCP Stream — Data segments have correct direction alternation
    // ========================================================================

    [Test]
    public async Task TcpStream_DataSegments_AlternateDirection()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 4,
            IncludeHandshake = true,
            IncludeTeardown = false,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;

        // Frames 0-2: handshake, frames 3-6: data
        byte[]? syn = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, false);
        byte[]? data0 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 3, false);
        byte[]? data1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 4, false);
        byte[]? data2 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 5, false);
        byte[]? data3 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 6, false);

        // SYN establishes the client → server direction
        ushort clientPort = GetTcpSrcPort(syn!);
        ushort serverPort = GetTcpDstPort(syn!);

        // Even data segments: client → server
        await Assert.That(GetTcpSrcPort(data0!)).IsEqualTo(clientPort);
        await Assert.That(GetTcpDstPort(data0!)).IsEqualTo(serverPort);

        // Odd data segments: server → client
        await Assert.That(GetTcpSrcPort(data1!)).IsEqualTo(serverPort);
        await Assert.That(GetTcpDstPort(data1!)).IsEqualTo(clientPort);

        // Even again
        await Assert.That(GetTcpSrcPort(data2!)).IsEqualTo(clientPort);
        await Assert.That(GetTcpDstPort(data2!)).IsEqualTo(serverPort);

        // Odd again
        await Assert.That(GetTcpSrcPort(data3!)).IsEqualTo(serverPort);
        await Assert.That(GetTcpDstPort(data3!)).IsEqualTo(clientPort);
    }

    // ========================================================================
    // TCP Stream — Data segments have PSH+ACK flags
    // ========================================================================

    [Test]
    public async Task TcpStream_DataSegments_HavePshAckFlags()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = false,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        byte[]? data0 = TcpStreamFrameBuilder.BuildFrame(in layout, options, 42, 3, false);
        byte[]? data1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, 42, 4, false);

        byte expectedFlags = FlagAck | FlagPsh;
        await Assert.That(GetTcpFlags(data0!)).IsEqualTo(expectedFlags);
        await Assert.That(GetTcpFlags(data1!)).IsEqualTo(expectedFlags);
    }

    // ========================================================================
    // TCP Stream — Teardown has FIN-ACK/ACK/FIN-ACK/ACK
    // ========================================================================

    [Test]
    public async Task TcpStream_Teardown_CorrectFlagSequence()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;

        // 3 handshake + 2 data + 4 teardown = 9 total
        // Teardown frames: index 5, 6, 7, 8

        byte[]? syn = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, false);
        byte[]? finAck1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 5, false);
        byte[]? ackFin = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 6, false);
        byte[]? finAck2 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 7, false);
        byte[]? finalAck = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 8, false);

        ushort clientPort = GetTcpSrcPort(syn!);
        ushort serverPort = GetTcpDstPort(syn!);

        // Step 0: FIN-ACK client → server
        await Assert.That(GetTcpFlags(finAck1!)).IsEqualTo((byte)(FlagFin | FlagAck));
        await Assert.That(GetTcpSrcPort(finAck1!)).IsEqualTo(clientPort);

        // Step 1: ACK server → client
        await Assert.That(GetTcpFlags(ackFin!)).IsEqualTo(FlagAck);
        await Assert.That(GetTcpSrcPort(ackFin!)).IsEqualTo(serverPort);

        // Step 2: FIN-ACK server → client
        await Assert.That(GetTcpFlags(finAck2!)).IsEqualTo((byte)(FlagFin | FlagAck));
        await Assert.That(GetTcpSrcPort(finAck2!)).IsEqualTo(serverPort);

        // Step 3: Final ACK client → server
        await Assert.That(GetTcpFlags(finalAck!)).IsEqualTo(FlagAck);
        await Assert.That(GetTcpSrcPort(finalAck!)).IsEqualTo(clientPort);
    }

    // ========================================================================
    // TCP Stream — Sequence number consistency across full connection
    // ========================================================================

    [Test]
    public async Task TcpStream_SequenceNumbers_AreConsistent()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 4,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = false,
            MinPayloadSize = 100,
            MaxPayloadSize = 100, // Fixed payload for easier verification
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 77;
        // 3 handshake + 4 data + 4 teardown = 11 total

        byte[]? syn = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, false);
        byte[]? synAck = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 1, false);
        byte[]? ack = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 2, false);

        uint clientIsn = GetTcpSeqNum(syn!);
        uint serverIsn = GetTcpSeqNum(synAck!);

        // After handshake: client seq = ISN+1, server seq = ISN+1
        await Assert.That(GetTcpSeqNum(ack!)).IsEqualTo(clientIsn + 1);
        await Assert.That(GetTcpAckNum(ack!)).IsEqualTo(serverIsn + 1);

        // Data frame 0 (client→server): seq=client ISN+1, payload=100
        byte[]? d0 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 3, false);
        await Assert.That(GetTcpSeqNum(d0!)).IsEqualTo(clientIsn + 1);
        await Assert.That(GetTcpAckNum(d0!)).IsEqualTo(serverIsn + 1);

        // Data frame 1 (server→client): seq=server ISN+1, ack=client ISN+1+100
        byte[]? d1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 4, false);
        await Assert.That(GetTcpSeqNum(d1!)).IsEqualTo(serverIsn + 1);
        await Assert.That(GetTcpAckNum(d1!)).IsEqualTo(clientIsn + 1 + 100);

        // Data frame 2 (client→server): seq=client ISN+1+100
        byte[]? d2 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 5, false);
        await Assert.That(GetTcpSeqNum(d2!)).IsEqualTo(clientIsn + 1 + 100);
        await Assert.That(GetTcpAckNum(d2!)).IsEqualTo(serverIsn + 1 + 100);

        // Data frame 3 (server→client): seq=server ISN+1+100
        byte[]? d3 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 6, false);
        await Assert.That(GetTcpSeqNum(d3!)).IsEqualTo(serverIsn + 1 + 100);
        await Assert.That(GetTcpAckNum(d3!)).IsEqualTo(clientIsn + 1 + 200);

        // Teardown FIN-ACK (client): seq=client ISN+1+200
        byte[]? fin1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 7, false);
        await Assert.That(GetTcpSeqNum(fin1!)).IsEqualTo(clientIsn + 1 + 200);
    }

    // ========================================================================
    // TCP Stream — Random access: any frame independently reproducible
    // ========================================================================

    [Test]
    public async Task TcpStream_RandomAccess_ProducesSameResultAsSequential()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 2,
            SegmentsPerStream = 5,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = true,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;

        // Generate all frames sequentially
        List<byte[]> allFrames = [];
        for (int i = 0; i < layout.TotalFrameCount; i++)
        {
            byte[]? frame = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, i, false);
            await Assert.That(frame).IsNotNull();
            allFrames.Add(frame!);
        }

        // Verify random access produces identical results
        // Access in reverse order
        for (int i = layout.TotalFrameCount - 1; i >= 0; i--)
        {
            byte[]? randomAccessFrame = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, i, false);
            await Assert.That(randomAccessFrame).IsNotNull();
            await Assert.That(randomAccessFrame!.SequenceEqual(allFrames[i])).IsTrue()
                .Because($"Frame {i} should be identical when accessed out of order");
        }

        // Access at arbitrary positions
        int[] randomIndices = [5, 0, 15, 3, 20, 1, 10];
        foreach (int idx in randomIndices)
        {
            if (idx < layout.TotalFrameCount)
            {
                byte[]? frame = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, idx, false);
                await Assert.That(frame!.SequenceEqual(allFrames[idx])).IsTrue()
                    .Because($"Random access to frame {idx} should match sequential");
            }
        }
    }

    // ========================================================================
    // TCP Stream — Multiple connections have different endpoints
    // ========================================================================

    [Test]
    public async Task TcpStream_MultipleStreams_DifferentEndpoints()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 3,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = false,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;

        // First SYN of each stream
        byte[]? syn0 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, false);
        byte[]? syn1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 5, false);
        byte[]? syn2 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 10, false);

        // All should be SYN frames
        await Assert.That(GetTcpFlags(syn0!)).IsEqualTo(FlagSyn);
        await Assert.That(GetTcpFlags(syn1!)).IsEqualTo(FlagSyn);
        await Assert.That(GetTcpFlags(syn2!)).IsEqualTo(FlagSyn);

        // Different connections should use different ports
        ushort port0 = GetTcpSrcPort(syn0!);
        ushort port1 = GetTcpSrcPort(syn1!);
        ushort port2 = GetTcpSrcPort(syn2!);

        // With high probability, all 3 random ports differ
        await Assert.That(port0 != port1 || port0 != port2).IsTrue()
            .Because("Different streams should typically have different ports");
    }

    // ========================================================================
    // TCP Stream — IPv6 mode
    // ========================================================================

    [Test]
    public async Task TcpStream_IPv6_ProducesValidFrames()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = false,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;
        int ipv6TcpOffset = EthHeaderSize + 40; // IPv6 header = 40 bytes

        byte[]? syn = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 0, true);
        await Assert.That(syn).IsNotNull();

        // Verify EtherType is IPv6 (0x86DD)
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(syn.AsSpan(12));
        await Assert.That(etherType).IsEqualTo((ushort)0x86DD);

        // Verify IPv6 version nibble (high 4 bits of byte 14 = 0x6)
        await Assert.That((syn![14] >> 4)).IsEqualTo(6);

        // Verify TCP SYN flag at IPv6 TCP offset
        await Assert.That(syn[ipv6TcpOffset + 13]).IsEqualTo(FlagSyn);
    }

    // ========================================================================
    // Integration: RandomFrameSource with TcpStreamIPv4 mode
    // ========================================================================

    [Test]
    public async Task RandomFrameSource_TcpStreamIPv4_ProducesCorrectLifecycle()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 0, // Let the stream layout determine frame count
            Seed = 42,
            Mode = RandomFrameMode.TcpStreamIPv4,
            TcpStreamOptions = new TcpStreamOptions
            {
                StreamCount = 1,
                SegmentsPerStream = 3,
                IncludeHandshake = true,
                IncludeTeardown = true,
                InterleaveStreams = false,
            },
        });
        SourceTestFixture.InitializeAndStartSource(source);

        // 3 + 3 + 4 = 10 frames
        await Assert.That(source.EstimatedFrameCount).IsEqualTo(10);

        List<Frame> frames = [];
        Frame? f;
        while ((f = source.NextFrame()) is not null)
        {
            frames.Add(f.Value);
        }

        await Assert.That(frames.Count).IsEqualTo(10);

        // Verify handshake sequence
        await Assert.That(GetTcpFlags(frames[0].Data.Span)).IsEqualTo(FlagSyn);
        await Assert.That(GetTcpFlags(frames[1].Data.Span)).IsEqualTo(FlagSynAck);
        await Assert.That(GetTcpFlags(frames[2].Data.Span)).IsEqualTo(FlagAck);

        // Data frames have PSH+ACK
        byte pshAck = FlagPsh | FlagAck;
        await Assert.That(GetTcpFlags(frames[3].Data.Span)).IsEqualTo(pshAck);
        await Assert.That(GetTcpFlags(frames[4].Data.Span)).IsEqualTo(pshAck);
        await Assert.That(GetTcpFlags(frames[5].Data.Span)).IsEqualTo(pshAck);

        // Teardown
        await Assert.That(GetTcpFlags(frames[6].Data.Span)).IsEqualTo((byte)(FlagFin | FlagAck));
        await Assert.That(GetTcpFlags(frames[7].Data.Span)).IsEqualTo(FlagAck);
        await Assert.That(GetTcpFlags(frames[8].Data.Span)).IsEqualTo((byte)(FlagFin | FlagAck));
        await Assert.That(GetTcpFlags(frames[9].Data.Span)).IsEqualTo(FlagAck);
    }

    [Test]
    public async Task RandomFrameSource_TcpStreamIPv4_Deterministic()
    {
        TcpStreamOptions tcpOptions = new()
        {
            StreamCount = 2,
            SegmentsPerStream = 3,
            IncludeHandshake = true,
            IncludeTeardown = true,
            InterleaveStreams = true,
        };

        using RandomFrameSource source1 = new(new RandomSourceOptions
        {
            Seed = 42,
            Mode = RandomFrameMode.TcpStreamIPv4,
            TcpStreamOptions = tcpOptions,
        });
        SourceTestFixture.InitializeAndStartSource(source1);

        using RandomFrameSource source2 = new(new RandomSourceOptions
        {
            Seed = 42,
            Mode = RandomFrameMode.TcpStreamIPv4,
            TcpStreamOptions = tcpOptions,
        });
        SourceTestFixture.InitializeAndStartSource(source2);

        Frame? f1;
        while ((f1 = source1.NextFrame()) is not null)
        {
            Frame? f2 = source2.NextFrame();
            await Assert.That(f2).IsNotNull();
            await Assert.That(f1.Value.Data.Span.SequenceEqual(f2!.Value.Data.Span)).IsTrue();
        }
    }

    [Test]
    public async Task RandomFrameSource_TcpStreamIPv6_ProducesFrames()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            Seed = 42,
            Mode = RandomFrameMode.TcpStreamIPv6,
            TcpStreamOptions = new TcpStreamOptions
            {
                StreamCount = 1,
                SegmentsPerStream = 2,
                IncludeHandshake = true,
                IncludeTeardown = true,
            },
        });
        SourceTestFixture.InitializeAndStartSource(source);

        // 3 + 2 + 4 = 9 frames
        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(9);
    }

    // ========================================================================
    // TCP Stream — FrameCount cap limits output
    // ========================================================================

    [Test]
    public async Task RandomFrameSource_TcpStream_FrameCountCap()
    {
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = 5,  // Cap at 5, even though the stream has more
            Seed = 42,
            Mode = RandomFrameMode.TcpStreamIPv4,
            TcpStreamOptions = new TcpStreamOptions
            {
                StreamCount = 1,
                SegmentsPerStream = 10,
                IncludeHandshake = true,
                IncludeTeardown = true,
            },
        });
        SourceTestFixture.InitializeAndStartSource(source);

        int count = 0;
        while (source.NextFrame() is not null)
        {
            count++;
        }

        await Assert.That(count).IsEqualTo(5);
    }

    // ========================================================================
    // TCP Stream — Data payload is non-empty
    // ========================================================================

    [Test]
    public async Task TcpStream_DataSegments_HavePayload()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = false,
            MinPayloadSize = 50,
            MaxPayloadSize = 200,
        };
        TcpStreamLayout layout = new(options);

        const ulong seed = 42;

        // Data frame at index 3 and 4
        byte[]? d0 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 3, false);
        byte[]? d1 = TcpStreamFrameBuilder.BuildFrame(in layout, options, seed, 4, false);

        // Frame should be larger than headers alone (14 + 20 + 20 = 54 bytes headers)
        int minFrameSize = EthHeaderSize + IPv4HeaderSize + 20 + 50; // headers + min payload
        await Assert.That(d0!.Length).IsGreaterThanOrEqualTo(minFrameSize);
        await Assert.That(d1!.Length).IsGreaterThanOrEqualTo(minFrameSize);
    }

    // ========================================================================
    // Out of range returns null
    // ========================================================================

    [Test]
    public async Task TcpStream_OutOfRange_ReturnsNull()
    {
        TcpStreamOptions options = new()
        {
            StreamCount = 1,
            SegmentsPerStream = 2,
            IncludeHandshake = true,
            IncludeTeardown = true,
        };
        TcpStreamLayout layout = new(options);

        byte[]? result = TcpStreamFrameBuilder.BuildFrame(in layout, options, 42, 100, false);
        await Assert.That(result).IsNull();
    }
}
