// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Index;

/// <summary>
/// The presence index must be the same whether the packet is parsed with a field tree or without one. Groups that are
/// decided from the enclosing layer are the risk: a decision that reads the field tree sees nothing in the mode that
/// builds none.
/// </summary>
internal sealed class SkipTreeIndexTests
{
    /// <summary>Parses <paramref name="frame"/> with an index and returns the names of the index groups that are present.</summary>
    private static HashSet<string> _PresentGroups(byte[] frame, FieldTreeMode mode)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        NetworkInspector.Core.Index.PacketIndex index = new(stack);
        Frame parsed = Frame.Create(
            0, Timestamp.FromSecs(0), frame, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(mode, index: index);
        if (!Packet.TryParse(0, stack, parsed, in options, out _, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        HashSet<string> present = [];
        for (int group = 0; group < stack.IndexGroupCount; group++)
        {
            IndexGroupId id = new(group);
            if (index.GetGroupBitmap(id).Contains(0))
            {
                _ = present.Add(stack.GetIndexGroup(id)?.Name ?? group.ToString(CultureInfo.InvariantCulture));
            }
        }

        return present;
    }

    [Test]
    [Arguments((byte)133, (byte)64)]
    [Arguments((byte)135, (byte)64)]
    [Arguments((byte)136, (byte)1)]
    [Arguments((byte)135, (byte)255)]
    public async Task NeighborDiscoveryIndexIsTheSameWithAndWithoutAFieldTree(byte type, byte hopLimit)
    {
        byte[] body = type switch
        {
            133 => Icmpv6PacketBuilder.BuildRouterSolicitationBody([]),
            135 => Icmpv6PacketBuilder.BuildNeighborSolicitationBody(new byte[16], []),
            _ => Icmpv6PacketBuilder.BuildNeighborAdvertisementBody(false, true, true, new byte[16], []),
        };
        byte[] frame = Icmpv6PacketBuilder.Build(type, 0, body);
        frame[14 + 7] = hopLimit;

        HashSet<string> build = _PresentGroups(frame, FieldTreeMode.Build);
        HashSet<string> skip = _PresentGroups(frame, FieldTreeMode.Skip);

        await Assert.That(skip.SetEquals(build)).IsTrue().Because(Invariant($"build: {string.Join(',', build)}; skip: {string.Join(',', skip)}"));
        await Assert.That(build.Contains("icmpv6.nd.error")).IsEqualTo(hopLimit != 255);
    }

    private static byte[] _MtuAndSourceAddress() =>
        [.. Icmpv6PacketBuilder.BuildMtuOption(1500), .. Icmpv6PacketBuilder.BuildLinkLayerAddressOption(1, new byte[6])];

    private static byte[] _RouterAdvertisement(byte[] options) =>
        Icmpv6PacketBuilder.Build(134, 0, Icmpv6PacketBuilder.BuildRouterAdvertisementBody(64, true, false, 1800, 0, 0, options));

    /// <summary>One frame for every message family this work touched; each must index the same in both modes.</summary>
    public static IEnumerable<Func<(string, byte[])>> CorpusFrames()
    {
        yield return static () => ("icmp-echo-payload", IcmpBasicTests.BuildIcmpFrame(8, 0, 0x00010001, [1, 2, 3, 4]));
        yield return static () => ("icmp-frag-needed", IcmpBasicTests.BuildIcmpFrame(3, 4, 1400, IcmpHardeningTests.QuotedIpv4Datagram(0x45)));
        yield return static () => ("icmp-param-problem", IcmpBasicTests.BuildIcmpFrame(12, 0, 0x14000000, IcmpHardeningTests.QuotedIpv4Datagram(0x45)));
        yield return static () => ("icmp-short-quote", IcmpBasicTests.BuildIcmpFrame(3, 3, 0, new byte[12]));
        yield return static () => ("icmpv6-echo-payload", Icmpv6BasicTests.BuildIcmpV6Frame(128, 0, [0x12, 0x34, 0x00, 0x01, 0xAA]));
        yield return static () => ("icmpv6-packet-too-big", Icmpv6BasicTests.BuildIcmpV6Frame(2, 0, Icmpv6BasicTests.ErrorBody(1280)));
        yield return static () => ("icmpv6-unreachable-rfc4884", Icmpv6BasicTests.BuildIcmpV6Frame(1, 4, Icmpv6BasicTests.ErrorBody(0x05000000)));
        yield return static () => ("icmpv6-short-quote", Icmpv6BasicTests.BuildIcmpV6Frame(1, 0, Icmpv6BasicTests.ErrorBody(0, quotedLength: 20)));
        yield return static () => ("mld-v1-query", Icmpv6PacketBuilder.Build(130, 0, new byte[20]));
        yield return static () => ("mld-v2-query", Icmpv6PacketBuilder.Build(130, 0, new byte[24]));
        yield return static () => ("mld-v2-report", Icmpv6PacketBuilder.Build(143, 0, new byte[4]));
        yield return static () => ("ndp-router-advertisement-options", _RouterAdvertisement(_MtuAndSourceAddress()));
        yield return static () => ("ndp-zero-length-option", _RouterAdvertisement(new byte[8]));
    }

    [Test]
    [MethodDataSource(nameof(CorpusFrames))]
    public async Task CorpusIndexIsTheSameWithAndWithoutAFieldTree(string name, byte[] frame)
    {
        HashSet<string> build = _PresentGroups(frame, FieldTreeMode.Build);
        HashSet<string> skip = _PresentGroups(frame, FieldTreeMode.Skip);

        await Assert.That(skip.SetEquals(build)).IsTrue().Because(Invariant($"{name} build: {string.Join(',', build)}; skip: {string.Join(',', skip)}"));
    }
}
