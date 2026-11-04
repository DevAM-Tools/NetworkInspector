// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// End-to-end protection: the header layouts of profiles 04, 44 and 08, the checksum of 04 and 44 over its three regions, the
/// inner payload under the header, segmented messages, and the degraded forms (unknown profile, payload shorter than the header).
/// The reference checksum in this file is a bit-by-bit loop written independently of the table-driven production code, and both are
/// pinned by the published check value of the algorithm.
/// </summary>
internal sealed class SomeIpE2eTests : SomeIpPayloadKit
{
    /// <summary>The header region of a message that <see cref="SomeIpBytes.Message"/> builds with its defaults: client 1, session 0x10, version 1/1, request, E_OK.</summary>
    private static readonly byte[] _DefaultRegion = [0x00, 0x01, 0x00, 0x10, 0x01, 0x01, 0x00, 0x00];

    #region Reference

    /// <summary>CRC-32/AUTOSAR, bit by bit: polynomial 0xF4ACFB13 reflected (0xC8DF352F), initial value and final XOR all ones.</summary>
    private static uint _ReferenceCrc(params byte[][] regions)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte[] region in regions)
        {
            foreach (byte b in region)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0
                        ? (crc >> 1) ^ 0xC8DF_352F
                        : crc >> 1;
                }
            }
        }

        return ~crc;
    }

    /// <summary>The payload of a profile 04 or 44 message: length, counter, data id, the checksum over the three regions, the inner bytes.</summary>
    private static byte[] _Protected(byte[] inner, ushort length, ushort counter, uint dataId, byte[]? region = null, uint? crcOverride = null)
    {
        byte[] head = new SomeIpBytes().U16(length).U16(counter).U32(dataId).ToArray();
        uint crc = crcOverride ?? _ReferenceCrc(region ?? _DefaultRegion, head, inner);
        return new SomeIpBytes().Bytes(head).U32(crc).Bytes(inner).ToArray();
    }

    private static string _Config() => Json(
        Join(
            Method(1, "prot04", Sites(Site("a", "uint8"), Site("b", "uint16")), Sites(Site("r", "uint8")), ",\"e2e_profile\":\"04\""),
            Method(2, "prot44", Sites(Site("a", "uint8"), Site("b", "uint16")), "[]", ",\"e2e_profile\":\"44\""),
            Method(3, "prot08", Sites(Site("a", "uint8"), Site("b", "uint16")), "[]", ",\"e2e_profile\":\"08\""),
            Method(4, "plain", Sites(Site("a", "uint8"), Site("b", "uint16")), "[]"),
            Method(5, "bad", Sites(Site("a", "uint8")), "[]", ",\"e2e_profile\":\"99\"")));

    private static SomeIpTestStack _Stack(bool verify = false) =>
        SomeIpTestStack.Create(_Config(), ("someip.verify_e2e_crc", SettingValue.Bool(verify)));

    #endregion

    #region Algorithm

    [Test]
    public async Task TheChecksumOfTheNineDigitsMatchesThePublishedCheckValue()
    {
        byte[] digits = "123456789"u8.ToArray();

        await Assert.That(SomeIpE2e.Finish(SomeIpE2e.Update(SomeIpE2e.Start(), digits))).IsEqualTo(0x1697D06Au);
        await Assert.That(_ReferenceCrc(digits)).IsEqualTo(0x1697D06Au);
        await Assert.That(SomeIpE2e.Finish(SomeIpE2e.Start())).IsEqualTo(0u);
    }

    [Test]
    public async Task ChainingTwoHalvesGivesTheChecksumOfTheWholeBuffer()
    {
        byte[] buffer = new byte[1024 * 1024];
        new Random(7).NextBytes(buffer);

        uint whole = SomeIpE2e.Finish(SomeIpE2e.Update(SomeIpE2e.Start(), buffer));
        uint chained = SomeIpE2e.Finish(SomeIpE2e.Update(SomeIpE2e.Update(SomeIpE2e.Start(), buffer.AsSpan(0, 524_288)), buffer.AsSpan(524_288)));

        await Assert.That(chained).IsEqualTo(whole);
        await Assert.That(whole).IsEqualTo(_ReferenceCrc(buffer));
    }

    [Test]
    public async Task HeaderSizesFollowTheProfile()
    {
        await Assert.That(SomeIpE2e.HeaderSize(SomeIpE2eProfile.Profile04)).IsEqualTo(12);
        await Assert.That(SomeIpE2e.HeaderSize(SomeIpE2eProfile.Profile44)).IsEqualTo(12);
        await Assert.That(SomeIpE2e.HeaderSize(SomeIpE2eProfile.Profile08)).IsEqualTo(16);
        await Assert.That(SomeIpE2e.HeaderSize(SomeIpE2eProfile.None)).IsEqualTo(0);
        await Assert.That(SomeIpE2e.HeaderSize(SomeIpE2eProfile.Unknown)).IsEqualTo(0);
    }

    #endregion

    #region Headers

    [Test]
    public async Task Profile04AndProfile44ShowLengthCounterDataIdAndTheStoredChecksum()
    {
        using SomeIpTestStack s = _Stack();
        byte[] inner = [7, 0x01, 0x02];
        byte[] payload = new SomeIpBytes().Bytes([0x00, 0x10, 0x00, 0x07, 0x00, 0x00, 0x00, 0x2A]).U32(0xAABBCCDD).Bytes(inner).ToArray();

        Packet p04 = s.Parse(UdpFrame(payload, methodId: 1));
        Packet p44 = s.Parse(UdpFrame(payload, methodId: 2));

        await Assert.That(s.U64s(p04, "someip.e2e.length")).IsEquivalentTo(One(16UL));
        await Assert.That(s.U64s(p04, "someip.e2e.counter")).IsEquivalentTo(One(7UL));
        await Assert.That(s.U64s(p04, "someip.e2e.data_id")).IsEquivalentTo(One(42UL));
        await Assert.That(s.U64s(p04, "someip.e2e.crc")).IsEquivalentTo(One(0xAABBCCDDUL));
        await Assert.That(s.Strings(p04, "someip.e2e.profile")).IsEquivalentTo(One("04"));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, p04, "someip.e2e", "Profile 04");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, p44, "someip.e2e", "Profile 44");
        await Assert.That(s.U64s(p44, "someip.e2e.counter")).IsEquivalentTo(One(7UL));
        await Assert.That(s.Count(p04, "someip.e2e.crc_valid")).IsEqualTo(0);
    }

    [Test]
    public async Task Profile08ShowsTheChecksumFirstAndFourThirtyTwoBitFields()
    {
        using SomeIpTestStack s = _Stack(verify: true);
        byte[] payload = new SomeIpBytes().U32(0x11223344).U32(20).U32(5).U32(0xABCDEF01).U8(9).U16(0x0102).ToArray();

        Packet packet = s.Parse(UdpFrame(payload, methodId: 3));

        await Assert.That(s.U64s(packet, "someip.e2e.crc")).IsEquivalentTo(One(0x11223344UL));
        await Assert.That(s.U64s(packet, "someip.e2e.length")).IsEquivalentTo(One(20UL));
        await Assert.That(s.U64s(packet, "someip.e2e.counter")).IsEquivalentTo(One(5UL));
        await Assert.That(s.U64s(packet, "someip.e2e.data_id")).IsEquivalentTo(One(0xABCDEF01UL));
        await Assert.That(s.U64s(packet, InName("prot08", "a"))).IsEquivalentTo(One(9UL));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.e2e", "Profile 08");

        // No construction is pinned for this profile, so no verdict is shown even with verification on.
        await Assert.That(s.Count(packet, "someip.e2e.crc_valid")).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip.e2e.crc_calculated")).IsEqualTo(0);
    }

    [Test]
    public async Task TheInnerBytesDecodeToTheSameValuesAsUnderAnUnprotectedMember()
    {
        using SomeIpTestStack s = _Stack();
        byte[] inner = [7, 0x01, 0x02];

        Packet protectedPacket = s.Parse(UdpFrame(_Protected(inner, 15, 1, 9), methodId: 1));
        Packet plain = s.Parse(UdpFrame(inner, methodId: 4));

        await Assert.That(s.U64s(protectedPacket, InName("prot04", "a"))).IsEquivalentTo(s.U64s(plain, InName("plain", "a")));
        await Assert.That(s.U64s(protectedPacket, InName("prot04", "b"))).IsEquivalentTo(s.U64s(plain, InName("plain", "b")));
        await Assert.That(Errors(s, protectedPacket)).IsEmpty();
    }

    #endregion

    #region Verification

    [Test]
    public async Task AnIntactMessageVerifiesAndAFlippedByteOrAFlippedMessageTypeBitDoesNot()
    {
        using SomeIpTestStack s = _Stack(verify: true);
        byte[] inner = [7, 0x01, 0x02];
        byte[] intact = _Protected(inner, 15, 1, 9);
        byte[] flippedInner = [.. intact];
        flippedInner[^1] ^= 0x01;

        Packet valid = s.Parse(UdpFrame(intact, methodId: 1));
        Packet bad = s.Parse(UdpFrame(flippedInner, methodId: 1));

        // The ACK flag is part of the message type the sender protected, so a message that arrives with it set fails.
        Packet ack = s.Parse(UdpFrame(intact, methodId: 1, messageType: 0x40));

        await Assert.That(Bools(s, valid, "someip.e2e.crc_valid")).IsEquivalentTo(One(true));
        await Assert.That(s.U64s(valid, "someip.e2e.crc_calculated")).IsEquivalentTo(s.U64s(valid, "someip.e2e.crc"));
        await Assert.That(Bools(s, bad, "someip.e2e.crc_valid")).IsEquivalentTo(One(false));
        await Assert.That(s.U64s(bad, "someip.e2e.crc_calculated")[0]).IsNotEqualTo(s.U64s(bad, "someip.e2e.crc")[0]);
        await Assert.That(Bools(s, ack, "someip.e2e.crc_valid")).IsEquivalentTo(One(false));
    }

    [Test]
    public async Task SwitchingVerificationOnAddsExactlyTheTwoVerdictFields()
    {
        byte[] frame = UdpFrame(_Protected([7, 0x01, 0x02], 15, 1, 9), methodId: 1);
        using SomeIpTestStack off = _Stack(verify: false);
        using SomeIpTestStack on = _Stack(verify: true);

        Packet withoutVerdict = off.Parse(frame);
        Packet withVerdict = on.Parse(frame);

        List<string> before = _FieldNames(off, withoutVerdict);
        List<string> after = _FieldNames(on, withVerdict);
        await Assert.That(after.Count).IsEqualTo(before.Count + 2);
        await Assert.That(after.Except(before).Order().ToList()).IsEquivalentTo(new List<string> { "someip.e2e.crc_calculated", "someip.e2e.crc_valid" });
        await Assert.That(before.Except(after)).IsEmpty();
    }

    private static List<string> _FieldNames(SomeIpTestStack s, Packet packet)
    {
        List<string> names = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            string? name = s.Stack.GetField(field.FieldId)?.Name;
            if (name is not null && name.StartsWith("someip", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    [Test]
    public async Task ASegmentedMessageShowsTheProtectionOnlyOnTheCompletingSegmentAndVerifiesWithTheFlagMasked()
    {
        using SomeIpTestStack s = _Stack(verify: true);
        byte[] inner = new byte[36];
        inner[0] = 7;
        inner[1] = 0x01;
        inner[2] = 0x02;

        // Header bytes of the segments: client 1, session 1, versions 1/1, type 0x20 (TP flag set), E_OK. The sender protected the
        // message without the flag, which is the region below.
        byte[] region = [0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x00, 0x00];
        byte[] whole = _Protected(inner, 47, 3, 0x42, region);
        Packet first = s.Parse(SomeIpFrames.TpFrame(0, whole[..16], more: true, service: ServiceId, method: 1, messageType: 0x20));
        Packet second = s.Parse(SomeIpFrames.TpFrame(16, whole[16..32], more: true, service: ServiceId, method: 1, messageType: 0x20));
        Packet last = s.Parse(SomeIpFrames.TpFrame(32, whole[32..], more: false, service: ServiceId, method: 1, messageType: 0x20));

        await Assert.That(s.Count(first, "someip.e2e")).IsEqualTo(0);
        await Assert.That(s.Count(second, "someip.e2e")).IsEqualTo(0);
        await Assert.That(s.U64s(last, "someip.e2e.counter")).IsEquivalentTo(One(3UL));
        await Assert.That(Bools(s, last, "someip.e2e.crc_valid")).IsEquivalentTo(One(true));
    }

    #endregion

    #region Degraded forms

    [Test]
    public async Task AnUnknownProfileShowsTheNameRawBytesAndOneDiagnostic()
    {
        using SomeIpTestStack s = _Stack();

        Packet packet = s.Parse(UdpFrame([1, 2, 3, 4], methodId: 5));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.e2e", "(unknown profile 99)");
        await Assert.That(s.ByteLengths(packet, "someip.payload.unparsed")).IsEquivalentTo(One(4));
        await Assert.That(s.Count(packet, "someip.e2e.error")).IsEqualTo(1);
        await Assert.That(s.Warnings.Count(static w => w.Message.Contains("e2e_profile '99'", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.e2e.profile")).IsEqualTo(0);
        await Assert.That(s.Count(packet, InName("bad", "a"))).IsEqualTo(0);
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    [Arguments(1, 6, "6 of 12")]
    [Arguments(3, 15, "15 of 16")]
    public async Task APayloadShorterThanTheHeaderShowsRawBytesAndOneDiagnosticWithTheShortfall(int method, int length, string shortfall)
    {
        using SomeIpTestStack s = _Stack();

        Packet packet = s.Parse(UdpFrame(new byte[length], methodId: (ushort)method));

        await Assert.That(s.ByteLengths(packet, "someip.payload.unparsed")).IsEquivalentTo(One(length));
        List<string> errors = s.Strings(packet, "someip.e2e.error");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains(shortfall);
        await Assert.That(s.Count(packet, "someip.e2e.length")).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip.e2e.crc")).IsEqualTo(0);
        string profileName = method == 1
            ? "prot04"
            : "prot08";
        await Assert.That(s.Count(packet, InName(profileName, "a"))).IsEqualTo(0);
    }

    #endregion

    #region Index

    [Test]
    public async Task TheE2eGroupIsRecordedForEveryMessageOfAProtectedMemberAndTheIndexStaysExact()
    {
        using SomeIpTestStack s = _Stack(verify: true);
        byte[][] frames =
        [
            UdpFrame(_Protected([7, 0, 1], 15, 1, 9), methodId: 1),
            UdpFrame(new byte[6], methodId: 1),
            UdpFrame([1, 2], methodId: 5),
            UdpFrame(new byte[20], methodId: 3),
        ];

        // The protection container exists in every case, so its group is exact. The group of the member direction is a superset:
        // a payload that is too short or of an unknown profile leaves no parameter behind, which only the tree can tell.
        for (int i = 0; i < frames.Length; i++)
        {
            Packet packet = s.ParseIndexed(frames[i], out PacketIndex index);
            await Assert.That(s.HasGroup(index, packet, "someip.e2e")).IsTrue();
            if (i is 0 or 3)
            {
                await s.AssertIndexExact(index, packet);
            }
        }

        Packet plain = s.ParseIndexed(UdpFrame([1, 0, 2], methodId: 4), out PacketIndex plainIndex);
        await Assert.That(s.HasGroup(plainIndex, plain, "someip.e2e")).IsFalse();
    }

    #endregion
}
