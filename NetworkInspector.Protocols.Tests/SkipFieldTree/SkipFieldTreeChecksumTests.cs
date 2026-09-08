// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Skip-tree UDP/TCP checksum and stream fields match Build when IPv4 caches are populated;
/// sibling-walk fallbacks do not run when <see cref="MutField.HasFieldTree"/> is false.
/// </summary>
internal sealed class SkipFieldTreeChecksumTests
{
    #region Helpers

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static Stack _BuildStandardStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Frame(Stack stack, byte[] data) =>
        Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1),
            data,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    private static byte[] _UdpFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0xAC100164), new IPv4Address(0xAC100101));
        UdpLayer udp = new(12345, 53);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        return FrameStack.Start(eth).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _TcpFrame()
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(new IPv4Address(0x0A000001), new IPv4Address(0x0A000002));
        TcpLayer tcp = new(49152, 80, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame([]);
    }

    private static byte[] _BareUdpDatagram()
    {
        byte[] udp = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(0, 2), 12345);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4, 2), 8);
        return udp;
    }

    #endregion

    [Test]
    public async Task ParseFrame_Skip_UdpChecksumAndStream_MatchBuild()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, _UdpFrame());
        FieldId checksumId = stack.GetFieldId("udp.checksum")!.Value;
        FieldId streamId = stack.GetFieldId("udp.stream")!.Value;
        ValueCacheFieldConfig[] configs = [new(checksumId), new(streamId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(skip.GetSeries<ulong>(checksumId).Count).IsEqualTo(build.GetSeries<ulong>(checksumId).Count);
        await Assert.That(skip.GetSeries<ulong>(checksumId)[0].Value).IsEqualTo(build.GetSeries<ulong>(checksumId)[0].Value);
        await Assert.That(skip.GetSeries<ulong>(streamId).Count).IsEqualTo(build.GetSeries<ulong>(streamId).Count);
        await Assert.That(skip.GetSeries<ulong>(streamId)[0].Value).IsEqualTo(build.GetSeries<ulong>(streamId)[0].Value);
    }

    [Test]
    public async Task ParseFrame_Skip_TcpChecksum_MatchBuild()
    {
        using Stack stack = _BuildStandardStack();
        Frame frame = _Frame(stack, _TcpFrame());
        FieldId checksumId = stack.GetFieldId("tcp.checksum")!.Value;
        ValueCacheFieldConfig[] configs = [new(checksumId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(skip.GetSeries<ulong>(checksumId).Count).IsEqualTo(build.GetSeries<ulong>(checksumId).Count);
        await Assert.That(skip.GetSeries<ulong>(checksumId)[0].Value).IsEqualTo(build.GetSeries<ulong>(checksumId)[0].Value);
    }

    [Test]
    public async Task ParseFrame_Skip_UdpWithoutIpCache_DoesNotThrow()
    {
        using Stack stack = _BuildStandardStack();
        ProtocolId udpId = stack.GetProtocolId("udp")!.Value;
        Frame frame = _Frame(stack, _BareUdpDatagram());

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, udpId, FieldTreeMode.Skip);

        await Assert.That(packet.HasFieldTree).IsFalse();
        await Assert.That(packet.FieldCount(materialize: false)).IsEqualTo(1);
    }

    [Test]
    public async Task ParseFrame_Skip_UdpChecksumStatus_MatchBuildWhenVerifyOn()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("udp.verify_checksum", SettingValue.Bool(true)));
        Frame frame = _Frame(stack, _UdpFrame());
        FieldId statusId = stack.GetFieldId("udp.checksum.status")!.Value;
        ValueCacheFieldConfig[] configs = [new(statusId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(_StringRow(skip, statusId)).IsEqualTo(_StringRow(build, statusId));
        await Assert.That(_StringRow(skip, statusId)).IsNotEqualTo("[Unverified]");
    }

    [Test]
    public async Task ParseFrame_Skip_TcpChecksumStatus_MatchBuildWhenVerifyOn()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.verify_checksum", SettingValue.Bool(true)));
        Frame frame = _Frame(stack, _TcpFrame());
        FieldId checksumId = stack.GetFieldId("tcp.checksum")!.Value;
        FieldId statusId = stack.GetFieldId("tcp.checksum.status")!.Value;
        ValueCacheFieldConfig[] configs = [new(checksumId), new(statusId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(_StringRow(skip, statusId)).IsEqualTo(_StringRow(build, statusId));
        await Assert.That(_StringRow(skip, statusId)).IsEqualTo("[Good]");
    }

    [Test]
    public async Task ParseFrame_Skip_Icmpv6ChecksumStatus_MatchBuildWhenVerifyOn()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("icmpv6.verify_checksum", SettingValue.Bool(true)));
        Frame frame = _Frame(stack, _Icmpv6EchoFrame());
        FieldId typeId = stack.GetFieldId("icmpv6.type")!.Value;
        FieldId statusId = stack.GetFieldId("icmpv6.checksum.status")!.Value;
        ValueCacheFieldConfig[] configs = [new(typeId), new(statusId)];

        ValueCache build = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(0), stack, frame, FieldTreeMode.Build, build);

        ValueCache skip = new(stack, configs);
        _ = Packet.ParseFrame(new PacketId(1), stack, frame, FieldTreeMode.Skip, skip);

        await Assert.That(_StringRow(skip, statusId)).IsEqualTo(_StringRow(build, statusId));
        await Assert.That(_StringRow(skip, statusId)).IsNotEqualTo("[Bad]");
        await Assert.That(_StringRow(skip, statusId)).IsEqualTo("[Good]");
    }

    private static byte[] _Icmpv6EchoFrame()
    {
        MacAddress dstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
        MacAddress srcMac = MacAddress.FromBytes([0x33, 0x33, 0x00, 0x00, 0x00, 0x01]);
        IPv6Address srcIp = IPv6Address.FromBytes([0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
        IPv6Address dstIp = IPv6Address.FromBytes([0xFF, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01]);
        byte[] body = new byte[4];
        EthernetLayer eth = new(dstMac, srcMac);
        IPv6Layer ip = new(srcIp, dstIp);
        return FrameStack.Start(eth).Then(ip).Then(new IcmpV6Layer(128, 0)).CreateWithFixedValues().EmitFrame(body);
    }

    private static string _StringRow(ValueCache cache, FieldId fieldId)
    {
        foreach (ValueCacheSeries series in cache.Series)
        {
            if (series.FieldId == fieldId && series is ValueCacheSeries<string> strings)
            {
                if (strings.Count > 0)
                {
                    return strings[0].Value;
                }
            }
        }

        return string.Empty;
    }
}
