// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Stacks;

/// <summary>
/// Tests for <see cref="IFrameInterceptor"/> — verifying that interceptors
/// can inspect and modify frame bytes after fixups.
/// </summary>
internal sealed class InterceptorTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly byte[] _Payload = [0xDE, 0xAD, 0xBE, 0xEF];

    /// <summary>Interceptor that captures the complete frame bytes into a list via <see cref="IFrameInterceptor.OnFrameComplete"/>.</summary>
    private struct CaptureInterceptor : IFrameInterceptor
    {
        private readonly List<byte[]> _Frames;
        internal CaptureInterceptor(List<byte[]> frames)
        {
            _Frames = frames;
        }

        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, IProtocolLayer
        {
        }

        public void OnFrameComplete(scoped Span<byte> frame)
            => _Frames.Add(frame.ToArray());
    }

    /// <summary>
    /// Interceptor that corrupts the TCP checksum field in <see cref="IFrameInterceptor.OnFrameComplete"/>,
    /// after all post-fix passes have written the valid checksum.
    /// Assumes an Ethernet + IPv4 + TCP stack layout.
    /// </summary>
    private struct CorruptTcpChecksumInterceptor : IFrameInterceptor
    {
        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, IProtocolLayer
        {
        }

        public void OnFrameComplete(scoped Span<byte> frame)
        {
            // TCP checksum occupies bytes [16..17] within the TCP header.
            // For an Ethernet+IPv4+TCP stack, the TCP header begins at EthernetHeader.Size + IPv4Header.Size.
            int tcpChecksumOffset = EthernetHeader.Size + IPv4Header.Size + 16;
            if (frame.Length > tcpChecksumOffset + 1)
            {
                BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(tcpChecksumOffset, 2), 0xDEAD);
            }
        }
    }

    /// <summary>Captures per-layer header sizes and the total frame length reported by <see cref="IFrameInterceptor.OnFrameComplete"/>.</summary>
    private struct LayerSizeCapture : IFrameInterceptor
    {
        /// <summary>Header sizes, in outer-to-inner order, as reported by <see cref="IFrameInterceptor.OnHeaderWritten{TLayer}"/>.</summary>
        internal readonly List<int> HeaderSizes = [];

        // int[] used instead of plain int so mutations remain visible after the struct is copied when passed by value.
        private readonly int[] _FrameCompleteSize = [0];

        public LayerSizeCapture()
        {
        }

        /// <summary>Total frame length received in the last <see cref="IFrameInterceptor.OnFrameComplete"/> call.</summary>
        internal int FrameCompleteSize => _FrameCompleteSize[0];

        public void OnHeaderWritten<TLayer>(in TLayer layer, scoped Span<byte> headerSlice)
            where TLayer : struct, IProtocolLayer
            => HeaderSizes.Add(headerSlice.Length);

        public void OnFrameComplete(scoped Span<byte> frame)
            => _FrameCompleteSize[0] = frame.Length;
    }

    #region NoInterceptor produces same bytes

    [Test]
    public async Task NoInterceptor_ProducesIdenticalBytesToDefaultBuild()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);

        // Build without interceptor
        byte[] expected = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(_Payload).MoveNext(expected, out _);

        // Build with explicit NoInterceptor
        byte[] actual = new byte[expected.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues(new NoInterceptor()).Build(_Payload).MoveNext(actual, out _);

        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    #endregion

    #region CaptureInterceptor

    [Test]
    public async Task CaptureInterceptor_CapturedBytesMatchFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);

        List<byte[]> captured = [];
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues(new CaptureInterceptor(captured)).Build(_Payload).MoveNext(buf, out int len);

        await Assert.That(captured.Count).IsEqualTo(1);
        await Assert.That(captured[0].AsSpan().SequenceEqual(buf.AsSpan(0, len))).IsTrue();
    }

    #endregion

    #region Corrupt TCP checksum

    [Test]
    public async Task CorruptTcpChecksum_ProducesBadChecksum()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);

        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues(new CorruptTcpChecksumInterceptor()).Build(_Payload).MoveNext(buf, out int total);

        // Verify the TCP checksum is now invalid
        int ipOffset = EthernetHeader.Size;
        int tcpOffset = ipOffset + IPv4Header.Size;
        ReadOnlySpan<byte> srcIp = buf.AsSpan(ipOffset + 12, 4);
        ReadOnlySpan<byte> dstIp = buf.AsSpan(ipOffset + 16, 4);

        ushort verification = ChecksumUtils.PseudoHeaderIPv4(
            srcIp, dstIp, IpProtocols.Tcp, buf.AsSpan(tcpOffset, total - tcpOffset));
        await Assert.That(verification).IsNotEqualTo((ushort)0);
    }

    #endregion

    #region Per-layer header callbacks

    [Test]
    public async Task Interceptor_OnHeaderWritten_FiresForEachLayer_WithCorrectHeaderSize()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002));
        TcpLayer tcp = new(1234, 80, flags: TcpFlags.PshAck);

        LayerSizeCapture capture = new();
        byte[] buf = new byte[eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + _Payload.Length];
        FB.FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues(capture).Build(_Payload).MoveNext(buf, out _);

        // OnHeaderWritten fires outer-to-inner: link → network → transport.
        await Assert.That(capture.HeaderSizes.Count).IsEqualTo(3);
        await Assert.That(capture.HeaderSizes[0]).IsEqualTo(EthernetHeader.Size);
        await Assert.That(capture.HeaderSizes[1]).IsEqualTo(IPv4Header.Size);
        await Assert.That(capture.HeaderSizes[2]).IsEqualTo(TcpHeader.Size);
        // OnFrameComplete fires once with the complete frame.
        await Assert.That(capture.FrameCompleteSize).IsGreaterThan(0);
    }

    #endregion
}
