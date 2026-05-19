// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.


namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Smoke tests for the trailer and delegate-interceptor extensions
/// (concept §4.4 / §6.3).  Builds an Ethernet/IPv4/UDP frame with an
/// <see cref="EthernetFcs"/> trailer and verifies the FCS round-trip,
/// and exercises a <see cref="DelegateInterceptor"/> end-to-end.
/// </summary>
internal sealed class TrailerAndInterceptorSmokeTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF];

    [Test]
    public async Task EthernetFcs_AppendsValidCrc32()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        FB.CreatedStack<
            FB.StatelessStack<FB.UdpLayer,
                FB.StatelessStack<FB.IPv4Layer,
                    FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
            FB.EthernetFcs,
            FB.NoInterceptor> stack = FB.FrameStack
                .Start(eth)
                .Then(ip)
                .Then(udp)
                .WithTrailer(FB.EthernetFcs.Crc32)
                .CreateWithFixedValues();

        // Total = 14 + 20 + 8 + payload + 4 (FCS).
        int expectedTotal = 14 + 20 + 8 + _Payload.Length + 4;
        byte[] frame = new byte[expectedTotal];
        int written = EmitOnce(in stack, _Payload, frame);

        await Assert.That(written).IsEqualTo(expectedTotal);

        // The CRC verifies to a fixed magic value (0xDEBB20E3) when computed
        // over (data || CRC), per the standard self-test property of CRC-32.
        // Equivalent: re-computing the CRC over the data and reading the FCS
        // back must match.
        ReadOnlySpan<byte> data = frame.AsSpan(0, written - 4);
        uint expectedCrc = ComputeReferenceCrc32(data);
        uint actualCrc = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(written - 4, 4));
        await Assert.That(actualCrc).IsEqualTo(expectedCrc);
    }

    [Test]
    public async Task DelegateInterceptor_ReceivesFrameComplete()
    {
        FB.EthernetLayer eth = new(_DstMac, _SrcMac);
        FB.IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        FB.UdpLayer udp = new(53, 53, FB.Auto<ushort>.Explicit(0));

        int written;
        int frameLen;
        int headerCount;

        unsafe
        {
            _LastFrameLength = -1;
            _HeaderCount = 0;

            FB.DelegateInterceptor interceptor = new(
                onHeader: &OnHeaderStatic,
                onFrame: &OnFrameStatic);

            FB.CreatedStack<
                FB.StatelessStack<FB.UdpLayer,
                    FB.StatelessStack<FB.IPv4Layer,
                        FB.StatelessStack<FB.EthernetLayer, FB.StackEnd>>>,
                FB.NoTrailer,
                FB.DelegateInterceptor> stack = FB.FrameStack
                    .Start(eth)
                    .Then(ip)
                    .Then(udp)
                    .CreateWithFixedValues(in interceptor);

            byte[] frame = new byte[14 + 20 + 8 + _Payload.Length];
            written = EmitOnce(in stack, _Payload, frame);
            frameLen = _LastFrameLength;
            headerCount = _HeaderCount;
        }

        await Assert.That(written).IsEqualTo(14 + 20 + 8 + _Payload.Length);
        await Assert.That(frameLen).IsEqualTo(written);
        // Three layers ⇒ three OnHeaderWritten invocations.
        await Assert.That(headerCount).IsEqualTo(3);
    }

    /// <summary>Last frame length captured by the static OnFrame callback.</summary>
    private static volatile int _LastFrameLength;

    /// <summary>Header callback invocation count.</summary>
    private static volatile int _HeaderCount;

    /// <summary>Static callback target; function pointers can only point to non-instance methods.</summary>
    private static void OnFrameStatic(Span<byte> frame) => _LastFrameLength = frame.Length;

    /// <summary>Static callback target for OnHeaderWritten.</summary>
    private static void OnHeaderStatic(Span<byte> header) => _HeaderCount++;

    /// <summary>Sync helper to keep the ref-struct iterator off the await stack.</summary>
    private static int EmitOnce<TStack, TTrailer, TInterceptor>(
        in FB.CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload,
        Span<byte> dst)
        where TStack : struct, FB.IStackNode, FB.IStatelessStack
        where TTrailer : struct, FB.ITrailerLayer
        where TInterceptor : struct, FB.IFrameInterceptor
    {
        FB.FrameSequence<TStack, TTrailer, TInterceptor> seq = created.Build(payload);
        seq.MoveNext(dst, out int written);
        return written;
    }

    /// <summary>
    /// Reference CRC-32 (IEEE 802.3) used by tests to independently verify the
    /// FCS the trailer wrote.  Same algorithm, different code path.
    /// </summary>
    private static uint ComputeReferenceCrc32(ReadOnlySpan<byte> data)
    {
        const uint Polynomial = 0xEDB88320u;
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? Polynomial ^ (crc >> 1) : crc >> 1;
            }
        }
        return ~crc;
    }
}
