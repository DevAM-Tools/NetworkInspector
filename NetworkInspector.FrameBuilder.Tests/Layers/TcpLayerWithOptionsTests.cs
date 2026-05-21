// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="TcpLayerWithOptions"/> — DataOffset, checksum,
/// options presence, and urgent pointer support.
/// </summary>
internal sealed class TcpLayerWithOptionsTests
{
    private static readonly MacAddress _Dst = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _Src = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly EthernetLayer _Eth = new(_Dst, _Src);

    private static readonly IPv4Address _SrcIp = new(0xC0A80001); // 192.168.0.1
    private static readonly IPv4Address _DstIp = new(0xC0A80002); // 192.168.0.2

    #region Header size

    [Test]
    public async Task EmptyOptions_HeaderSize_Is20()
    {
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: TcpOptions.Empty);
        await Assert.That(tcp.HeaderSize).IsEqualTo(TcpHeader.Size); // 20
    }

    [Test]
    public async Task MssOption_HeaderSize_Is24()
    {
        TcpOptionsBuilder builder = new();
        builder.Mss(1460);
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: opts);

        // MSS option = 4 bytes, padded to 4 = 4. 20 + 4 = 24
        await Assert.That(tcp.HeaderSize).IsEqualTo(24);
    }

    [Test]
    public async Task SynOptions_HeaderSize_Is44()
    {
        // MSS(4) + SACKPermitted(2) + NOP+NOP+Timestamps(12) + NOP(1) + WindowScale(3) = 22 → padded to 24
        TcpOptionsBuilder builder = new();
        builder.SynOptions();
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: opts);

        await Assert.That(tcp.HeaderSize).IsEqualTo(44);
    }

    #endregion

    #region DataOffset field

    [Test]
    public async Task EmptyOptions_DataOffset_Is5()
    {
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: TcpOptions.Empty);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // DataOffset is upper 4 bits of bytes[12..13] in TCP header
        int tcpOffset = 14 + IPv4Header.Size;
        byte dataOffsetNibble = (byte)(buf[tcpOffset + 12] >> 4);
        await Assert.That(dataOffsetNibble).IsEqualTo((byte)5); // 5 × 4 = 20 bytes
    }

    [Test]
    public async Task MssOption_DataOffset_Is6()
    {
        // 20 + 4 = 24 bytes → DataOffset = 24/4 = 6
        TcpOptionsBuilder builder = new();
        builder.Mss(1460);
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: opts);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        int tcpOffset = 14 + IPv4Header.Size;
        byte dataOffsetNibble = (byte)(buf[tcpOffset + 12] >> 4);
        await Assert.That(dataOffsetNibble).IsEqualTo((byte)6);
    }

    [Test]
    public async Task SynOptions_DataOffset_Is11()
    {
        // 20 + 24 = 44 bytes → DataOffset = 44/4 = 11
        TcpOptionsBuilder builder = new();
        builder.SynOptions();
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: opts);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        int tcpOffset = 14 + IPv4Header.Size;
        byte dataOffsetNibble = (byte)(buf[tcpOffset + 12] >> 4);
        await Assert.That(dataOffsetNibble).IsEqualTo((byte)11);
    }

    #endregion

    #region Checksum

    [Test]
    public async Task TcpWithOptions_IPv4Checksum_IsValid()
    {
        TcpOptionsBuilder builder = new();
        builder.SynOptions();
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 12345, dstPort: 443, opts: opts);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] payload = [1, 2, 3, 4];

        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + payload.Length];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(payload).MoveNext(buf, out _);

        // Verify IPv4 header checksum
        int ipOffset = 14;
        ushort ipChecksum = ChecksumUtils.IPv4Header(buf.AsSpan(ipOffset, IPv4Header.Size));
        await Assert.That(ipChecksum).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task TcpWithOptions_TcpChecksum_IsValid()
    {
        TcpOptionsBuilder builder = new();
        builder.SynOptions();
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 12345, dstPort: 443, opts: opts);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize + payload.Length];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build(payload).MoveNext(buf, out _);

        // Verify TCP checksum using pseudo-header
        int tcpOffset = 14 + IPv4Header.Size;
        byte[] srcIpBytes = [0xC0, 0xA8, 0x00, 0x01];
        byte[] dstIpBytes = [0xC0, 0xA8, 0x00, 0x02];
        ushort checksum = ChecksumUtils.PseudoHeaderIPv4(
            srcIpBytes, dstIpBytes, IpProtocols.Tcp,
            buf.AsSpan(tcpOffset, buf.Length - tcpOffset));
        await Assert.That(checksum).IsEqualTo((ushort)0);
    }

    #endregion

    #region Urgent pointer

    [Test]
    public async Task UrgentPointer_WrittenToCorrectOffset()
    {
        TcpLayerWithOptions tcp = new(
            srcPort: 1234, dstPort: 80,
            opts: TcpOptions.Empty,
            flags: (byte)(TcpFlags.Ack | TcpFlags.Urg),
            urgentPointer: 0x1234);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // TCP UrgentPointer at bytes 18-19 of the TCP header
        int tcpOffset = 14 + IPv4Header.Size;
        ushort urg = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(tcpOffset + 18, 2));
        await Assert.That(urg).IsEqualTo((ushort)0x1234);
    }

    #endregion

    #region MSS option encoding

    [Test]
    public async Task MssOption_Value_EncodedCorrectly()
    {
        TcpOptionsBuilder builder = new();
        builder.Mss(1460);
        TcpOptions opts = builder.Build();
        TcpLayerWithOptions tcp = new(srcPort: 1234, dstPort: 80, opts: opts);
        IPv4Layer ip = new(_SrcIp, _DstIp);
        byte[] buf = new byte[_Eth.HeaderSize + ip.HeaderSize + tcp.HeaderSize];
        FB.FrameStack.Start(_Eth).Then(ip).Then(tcp).CreateWithFixedValues().Build([]).MoveNext(buf, out _);

        // MSS option: kind(1) = 2, length(1) = 4, value(2) = MSS
        int tcpOffset = 14 + IPv4Header.Size;
        int optionsStart = tcpOffset + TcpHeader.Size;
        byte kind = buf[optionsStart];
        byte length = buf[optionsStart + 1];
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(optionsStart + 2, 2));

        await Assert.That(kind).IsEqualTo((byte)2); // MSS kind
        await Assert.That(length).IsEqualTo((byte)4);
        await Assert.That(value).IsEqualTo((ushort)1460);
    }

    #endregion
}
