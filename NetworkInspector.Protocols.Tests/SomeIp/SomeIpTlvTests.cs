// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The payload categories that carry their own structure on the wire: TLV-encoded lists and structures with all eight wire
/// types, unions with their length and selector fields, and bitfields with named bits. Every test builds a small service
/// description in JSON, sends one UDP datagram and reads the typed fields back by their registered names.
/// </summary>
internal sealed class SomeIpTlvTests : SomeIpPayloadKit
{
    #region Description helpers

    private static string _Tlv(params (int Id, string Name, string Type)[] members) =>
        "{" + string.Join(
            ",",
            members.Select(static m => string.Create(
                CultureInfo.InvariantCulture,
                $"\"{m.Id}\":{{\"name\":\"{m.Name}\",\"type_reference\":\"{m.Type}\"}}"))) + "}";

    private static SomeIpBytes _WithTag(SomeIpBytes bytes, int wireType, int dataId, bool reserved = false)
    {
        int tag = (wireType << 12) | dataId;
        if (reserved)
        {
            tag |= 0x8000;
        }

        return bytes.U16((ushort)tag);
    }

    private static string _Union(string name, string members, string extra = "") =>
        $$"""{"category":"union","name":"{{name}}","members":{{members}}{{extra}}}""";

    private static string _Bitfield(string name, int bits, string members, string extra = "") =>
        $$"""{"category":"bitfield","name":"{{name}}","bits":{{bits}},"members":{{members}}{{extra}}}""";

    #endregion

    #region Tags

    [Test]
    public async Task ATagSplitsIntoReservedBitWireTypeAndDataId()
    {
        SomeIpTlvTag plain = SomeIpTlvTag.Parse(0x2007);
        SomeIpTlvTag reserved = SomeIpTlvTag.Parse(0x8ABC);

        await Assert.That(plain.WireType).IsEqualTo((byte)2);
        await Assert.That(plain.DataId).IsEqualTo((ushort)7);
        await Assert.That(plain.Reserved).IsFalse();
        await Assert.That(reserved.WireType).IsEqualTo((byte)0);
        await Assert.That(reserved.DataId).IsEqualTo((ushort)0xABC);
        await Assert.That(reserved.Reserved).IsTrue();
    }

    [Test]
    public async Task TheMetadataViewShowsTheTagWithItsThreeParts()
    {
        string methods = Method(1, "tag", "[]", _Tlv((7, "speed", "uint32"), (0xABC, "flag", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods), ("someip.show_type_metadata", SettingValue.Bool(true)));
        byte[] payload = _WithTag(_WithTag(new(), 2, 7).U32(500), 0, 0xABC, reserved: true).U8(1).ToArray();

        Packet packet = s.Parse(UdpFrame(payload, messageType: 0x80));

        await Assert.That(s.U64s(packet, "someip.payload.tlv.tag")).IsEquivalentTo(new List<ulong> { 0x2007, 0x8ABC });
        await Assert.That(s.U64s(packet, "someip.payload.tlv.tag.wire_type")).IsEquivalentTo(new List<ulong> { 2, 0 });
        await Assert.That(s.U64s(packet, "someip.payload.tlv.tag.data_id")).IsEquivalentTo(new List<ulong> { 7, 0xABC });
        await Assert.That(Bools(s, packet, "someip.payload.tlv.tag.reserved")).IsEquivalentTo(new List<bool> { false, true });
        await Assert.That(s.U64s(packet, OutName("tag", "speed"))).IsEquivalentTo(One(500UL));
    }

    #endregion

    #region Wire types

    [Test]
    public async Task WireTypesZeroToThreeConsumeOneTwoFourAndEightValueBytes()
    {
        string methods = Method(1, "fixed", "[]", _Tlv((1, "a", "uint8"), (2, "b", "uint16"), (3, "c", "uint32"), (4, "d", "uint64")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 0, 1).U8(0xAA);
        _ = _WithTag(payload, 1, 2).U16(0xBBBB);
        _ = _WithTag(payload, 2, 3).U32(0xCCCCCCCC);
        _ = _WithTag(payload, 3, 4).U64(0xDDDDDDDDDDDDDDDD);

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.U64s(packet, OutName("fixed", "a"))).IsEquivalentTo(One(0xAAUL));
        await Assert.That(s.U64s(packet, OutName("fixed", "b"))).IsEquivalentTo(One(0xBBBBUL));
        await Assert.That(s.U64s(packet, OutName("fixed", "c"))).IsEquivalentTo(One(0xCCCCCCCCUL));
        await Assert.That(s.U64s(packet, OutName("fixed", "d"))).IsEquivalentTo(One(0xDDDDDDDDDDDDDDDDUL));
        await Assert.That(Errors(s, packet)).IsEmpty();
        await Assert.That(s.Count(packet, "someip.payload.unparsed")).IsEqualTo(0);
    }

    [Test]
    public async Task WireTypesFiveToSevenReadAOneTwoOrFourByteLengthField()
    {
        string methods = Method(1, "sized", "[]", _Tlv((5, "s5", "string"), (6, "s6", "string"), (7, "s7", "string")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 5, 5).U8(2).Utf8("ab");
        _ = _WithTag(payload, 6, 6).U16(2).Utf8("cd");
        _ = _WithTag(payload, 7, 7).U32(2).Utf8("ef");

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.Strings(packet, OutName("sized", "s5"))).IsEquivalentTo(One("ab"));
        await Assert.That(s.Strings(packet, OutName("sized", "s6"))).IsEquivalentTo(One("cd"));
        await Assert.That(s.Strings(packet, OutName("sized", "s7"))).IsEquivalentTo(One("ef"));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task WireTypeFourReadsTheLengthFieldOfTheMembersTypeFromTheTypeOrTheMethod()
    {
        string methods = Method(
            1, "four", "[]", _Tlv((8, "p", "pair"), (9, "t", "string")), Transform(stringLength: 1, structLength: 2));
        string types = StructType("pair", Site("a", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 4, 8).U16(1).U8(7);
        _ = _WithTag(payload, 4, 9).U8(2).Utf8("gh");

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.U64s(packet, OutName("four", "p.a"))).IsEquivalentTo(One(7UL));
        await Assert.That(s.Strings(packet, OutName("four", "t"))).IsEquivalentTo(One("gh"));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task WireTypeFourOnATypeWithoutALengthFieldStopsTheWalkWithOneDiagnostic()
    {
        string methods = Method(1, "bad", "[]", _Tlv((1, "n", "uint16"), (2, "m", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 4, 1).U16(5);
        _ = _WithTag(payload, 0, 2).U8(9);

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("Wire type 4").And.Contains("data id 1");
        await Assert.That(s.Count(packet, OutName("bad", "m"))).IsEqualTo(0);
        await Assert.That(s.ByteLengths(packet, "someip.payload.unparsed")).IsEquivalentTo(One(7));
    }

    #endregion

    #region Members

    [Test]
    public async Task MembersComeInAnyOrderAbsentOnesAreNoAnomalyAndARepeatedIdIsAnotherNode()
    {
        string methods = Method(1, "any", "[]", _Tlv((1, "a", "uint8"), (2, "t", "string"), (3, "c", "uint16")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes reordered = new();
        _ = _WithTag(reordered, 1, 3).U16(0x0102);
        _ = _WithTag(reordered, 0, 1).U8(5);
        SomeIpBytes repeated = new();
        _ = _WithTag(repeated, 0, 1).U8(1);
        _ = _WithTag(repeated, 0, 1).U8(2);

        Packet first = s.Parse(UdpFrame(reordered.ToArray(), messageType: 0x80));
        Packet second = s.Parse(UdpFrame(repeated.ToArray(), messageType: 0x80));

        await Assert.That(s.U64s(first, OutName("any", "a"))).IsEquivalentTo(One(5UL));
        await Assert.That(s.U64s(first, OutName("any", "c"))).IsEquivalentTo(One(0x0102UL));
        await Assert.That(s.Count(first, OutName("any", "t"))).IsEqualTo(0);
        await Assert.That(Errors(s, first)).IsEmpty();
        await Assert.That(s.U64s(second, OutName("any", "a"))).IsEquivalentTo(new List<ulong> { 1, 2 });
    }

    [Test]
    public async Task AnUnknownDataIdIsShownAsRawBytesAndTheWalkGoesOn()
    {
        string methods = Method(1, "unk", "[]", _Tlv((1, "a", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 2, 9).U32(0xDEADBEEF);
        _ = _WithTag(payload, 0, 1).U8(4);

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.ByteLengths(packet, "someip.payload.tlv.unknown")).IsEquivalentTo(One(4));
        await Assert.That(s.U64s(packet, OutName("unk", "a"))).IsEquivalentTo(One(4UL));
        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("data id 9");
    }

    [Test]
    public async Task TheTagLengthReplacesTheInlineLengthOfAStringAStructureAndAnArray()
    {
        string methods = Method(1, "supply", Sites(Site("direct", "string")), _Tlv((1, "t", "string"), (2, "p", "pair"), (3, "v", "bytes_t")));
        string types = Join(StructType("pair", Site("a", "uint8"), Site("b", "uint8")), ArrayType("bytes_t", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 7, 1).U32(6).Utf8("abcdef");
        _ = _WithTag(payload, 6, 2).U16(2).U8(1).U8(2);
        _ = _WithTag(payload, 5, 3).U8(3).U8(7).U8(8).U8(9);

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));
        Packet direct = s.Parse(UdpFrame(new SomeIpBytes().U32(3).Utf8("xyz").ToArray(), messageType: 0x00));

        await Assert.That(s.Strings(packet, OutName("supply", "t"))).IsEquivalentTo(One("abcdef"));
        await Assert.That(s.U64s(packet, OutName("supply", "p.a"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.U64s(packet, OutName("supply", "p.b"))).IsEquivalentTo(One(2UL));
        await Assert.That(s.U64s(packet, OutName("supply", "v.item"))).IsEquivalentTo(new List<ulong> { 7, 8, 9 });
        await Assert.That(Errors(s, packet)).IsEmpty();
        await Assert.That(s.Strings(direct, InName("supply", "direct"))).IsEquivalentTo(One("xyz"));
    }

    [Test]
    public async Task AZeroLengthValueIsAnEmptyNodeAndTheNextTagDecodes()
    {
        string methods = Method(1, "zero", "[]", _Tlv((1, "t", "string"), (2, "v", "bytes_t"), (3, "n", "uint32"), (4, "after", "uint8")));
        string types = ArrayType("bytes_t", "uint8");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        SomeIpBytes payload = new();
        _ = _WithTag(payload, 5, 1).U8(0);
        _ = _WithTag(payload, 7, 2).U32(0);
        _ = _WithTag(payload, 5, 3).U8(0);
        _ = _WithTag(payload, 0, 4).U8(9);

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.Strings(packet, OutName("zero", "t"))).IsEquivalentTo(One(string.Empty));
        await Assert.That(Texts(s, packet, OutName("zero", "v"))).IsEquivalentTo(One("array[0]"));
        await Assert.That(Texts(s, packet, OutName("zero", "n"))).IsEquivalentTo(One("(empty)"));
        await Assert.That(s.U64s(packet, OutName("zero", "after"))).IsEquivalentTo(One(9UL));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task ATlvStructureDecodesItsTaggedMembersInsideAPlainList()
    {
        string methods = Method(1, "nest", Sites(Site("rec", "status"), Site("after", "uint8")), "[]");
        string types = """{"category":"tlv_structure","name":"status","members":{"1":{"name":"a","type_reference":"uint8"},"2":{"name":"b","type_reference":"uint16"}}}""";
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        SomeIpBytes body = new();
        _ = _WithTag(body, 1, 2).U16(0x0304);
        _ = _WithTag(body, 0, 1).U8(6);
        byte[] payload = new SomeIpBytes().U32((uint)body.Length).Bytes(body.ToArray()).U8(0x55).ToArray();

        Packet packet = s.Parse(UdpFrame(payload));

        await Assert.That(s.U64s(packet, InName("nest", "rec.b"))).IsEquivalentTo(One(0x0304UL));
        await Assert.That(s.U64s(packet, InName("nest", "rec.a"))).IsEquivalentTo(One(6UL));
        await Assert.That(s.U64s(packet, InName("nest", "after"))).IsEquivalentTo(One(0x55UL));
        await Assert.That(Texts(s, packet, InName("nest", "rec"))).IsEquivalentTo(One("tlv struct status"));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task ATlvMemberAbsentFromAMessageIsInTheGroupOfItsMemberButNotOnTheTree()
    {
        string methods = Method(1, "lazy", "[]", _Tlv((1, "a", "uint8"), (3, "c", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        byte[] both = _WithTag(_WithTag(new(), 0, 1).U8(1), 0, 3).U8(3).ToArray();
        byte[] onlyThird = _WithTag(new(), 0, 3).U8(3).ToArray();

        Packet a = s.ParseIndexed(UdpFrame(both, messageType: 0x80), out PacketIndex indexA);
        Packet b = s.ParseIndexed(UdpFrame(onlyThird, messageType: 0x80), out PacketIndex indexB);
        Packet c = s.ParseIndexed(UdpFrame(onlyThird, methodId: 77, messageType: 0x80), out PacketIndex indexC);

        string group = GroupPrefix + "lazy.resp";
        await Assert.That(s.HasGroup(indexA, a, group)).IsTrue();
        await Assert.That(s.HasGroup(indexB, b, group)).IsTrue();
        await Assert.That(s.HasGroup(indexC, c, group)).IsFalse();
        await Assert.That(s.Count(a, OutName("lazy", "a"))).IsEqualTo(1);
        await Assert.That(s.Count(b, OutName("lazy", "a"))).IsEqualTo(0);
        await s.AssertIndexExact(indexA, a);
    }

    #endregion

    #region Unions

    private const string _RecordTypes =
        "{\"category\":\"base_type\",\"name\":\"u32_le\",\"type_reference\":\"uint32\",\"byte_order\":\"little_endian\"},"
        + "{\"category\":\"base_type\",\"name\":\"vin_t\",\"type_reference\":\"string\",\"fixed_length\":17},"
        + "{\"category\":\"base_type\",\"name\":\"note_t\",\"type_reference\":\"string\",\"size_of_length\":1},"
        + "{\"category\":\"union\",\"name\":\"record_value\",\"members\":{\"1\":{\"name\":\"odometer\",\"type_reference\":\"u32_le\"},"
        + "\"2\":{\"name\":\"vin\",\"type_reference\":\"vin_t\"},\"3\":{\"name\":\"note\",\"type_reference\":\"note_t\"}}}";

    private static string _RecordMethod() => Method(1, "rec", Sites(Site("value", "record_value"), Site("after", "uint8")), "[]");

    [Test]
    public async Task AUnionDecodesTheAlternativeThatItsSelectorNames()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(_RecordMethod(), _RecordTypes));
        byte[] odometer = new SomeIpBytes().U32(8).U32(1).U32Le(123456).U8(1).ToArray();
        byte[] vin = new SomeIpBytes().U32(4 + 17).U32(2).Utf8("WVWZZZ1JZXW000001").U8(2).ToArray();
        byte[] note = new SomeIpBytes().U32(4 + 1 + 2).U32(3).U8(2).Utf8("ok").U8(3).ToArray();

        Packet first = s.Parse(UdpFrame(odometer));
        Packet second = s.Parse(UdpFrame(vin));
        Packet third = s.Parse(UdpFrame(note));

        await Assert.That(s.U64s(first, InName("rec", "value.odometer"))).IsEquivalentTo(One(123456UL));
        await Assert.That(s.U64s(first, InName("rec", "after"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.Strings(second, InName("rec", "value.vin"))).IsEquivalentTo(One("WVWZZZ1JZXW000001"));
        await Assert.That(s.U64s(second, InName("rec", "after"))).IsEquivalentTo(One(2UL));
        await Assert.That(s.Strings(third, InName("rec", "value.note"))).IsEquivalentTo(One("ok"));
        await Assert.That(s.U64s(third, InName("rec", "after"))).IsEquivalentTo(One(3UL));
        await Assert.That(Errors(s, first)).IsEmpty();
        await Assert.That(Errors(s, second)).IsEmpty();
        await Assert.That(Errors(s, third)).IsEmpty();
    }

    [Test]
    public async Task SelectorZeroIsAnEmptyUnionThatConsumesLengthAndSelectorOnly()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(_RecordMethod(), _RecordTypes));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(4).U32(0).U8(9).ToArray()));

        await Assert.That(Texts(s, packet, InName("rec", "value"))).IsEquivalentTo(One("union record_value (empty)"));
        await Assert.That(s.U64s(packet, InName("rec", "after"))).IsEquivalentTo(One(9UL));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task AnUnknownSelectorShowsTheValueBytesRawWithADiagnosticAndAShortUnionReportsTruncation()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(_RecordMethod(), _RecordTypes));

        Packet unknown = s.Parse(UdpFrame(new SomeIpBytes().U32(4 + 3).U32(7).Bytes([1, 2, 3]).U8(5).ToArray()));
        Packet shortUnion = s.Parse(UdpFrame(new SomeIpBytes().U32(4 + 2).U32(1).U8(1).U8(2).U8(5).ToArray()));

        await Assert.That(s.ByteLengths(unknown, "someip.payload.tlv.unknown")).IsEquivalentTo(One(3));
        await Assert.That(Errors(s, unknown).Count).IsEqualTo(1);
        await Assert.That(Errors(s, unknown)[0]).Contains("type selector 7");
        await Assert.That(s.U64s(unknown, InName("rec", "after"))).IsEquivalentTo(One(5UL));
        await Assert.That(Errors(s, shortUnion).Count).IsEqualTo(1);
        await Assert.That(Errors(s, shortUnion)[0]).Contains("needs 4 bytes but only 2 remain");
        await Assert.That(s.U64s(shortUnion, InName("rec", "after"))).IsEquivalentTo(One(5UL));
    }

    [Test]
    public async Task AUnionWithSmallerLengthAndSelectorFieldsDecodesAccordingly()
    {
        string methods = Method(1, "small", Sites(Site("value", "small_union")), "[]");
        string types = Join(
            _Union("small_union", """{"1":{"name":"x","type_reference":"uint8"}}""", ",\"size_of_length\":2,\"size_of_type\":1"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U16(2).U8(1).U8(0x42).ToArray()));

        await Assert.That(s.U64s(packet, InName("small", "value.x"))).IsEquivalentTo(One(0x42UL));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    #endregion

    #region Bitfields

    [Test]
    public async Task ABitfieldShowsTheRawWordAndOneBooleanPerNamedBit()
    {
        string methods = Method(1, "flags", "[]", Sites(Site("status", "status_flags")));
        string types = _Bitfield("status_flags", 16, """{"0":{"name":"ready"},"3":{"name":"fault"},"15":{"name":"test_mode"}}""");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet all = s.Parse(UdpFrame(new SomeIpBytes().U16(0x8009).ToArray(), messageType: 0x80));
        Packet some = s.Parse(UdpFrame(new SomeIpBytes().U16(0x0008).ToArray(), messageType: 0x80));

        await Assert.That(s.U64s(all, OutName("flags", "status"))).IsEquivalentTo(One(32777UL));
        await Assert.That(Bools(s, all, OutName("flags", "status.ready"))).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, all, OutName("flags", "status.fault"))).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, all, OutName("flags", "status.test_mode"))).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, some, OutName("flags", "status.ready"))).IsEquivalentTo(One(false));
        await Assert.That(Bools(s, some, OutName("flags", "status.fault"))).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, some, OutName("flags", "status.test_mode"))).IsEquivalentTo(One(false));
    }

    [Test]
    public async Task ASixtyFourBitBitfieldAndALittleEndianBitfieldDecode()
    {
        string methods = Method(1, "wide", "[]", Sites(Site("wide", "wide_t"), Site("le", "le_t")));
        string types = Join(
            _Bitfield("wide_t", 64, """{"63":{"name":"top"},"0":{"name":"bottom"}}"""),
            _Bitfield("le_t", 16, """{"15":{"name":"high"}}""", ",\"byte_order\":\"little_endian\""));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U64(0x8000000000000000UL).U8(0x09).U8(0x80).ToArray(), messageType: 0x80));

        await Assert.That(Bools(s, packet, OutName("wide", "wide.top"))).IsEquivalentTo(One(true));
        await Assert.That(Bools(s, packet, OutName("wide", "wide.bottom"))).IsEquivalentTo(One(false));
        await Assert.That(s.U64s(packet, OutName("wide", "le"))).IsEquivalentTo(One(0x8009UL));
        await Assert.That(Bools(s, packet, OutName("wide", "le.high"))).IsEquivalentTo(One(true));
    }

    #endregion

    #region Robustness

    [Test]
    public async Task EveryPrefixOfATlvUnionAndBitfieldMessageDecodesWithDiagnosticsOnly()
    {
        string methods = Method(
            1, "cut", Sites(Site("value", "record_value"), Site("flags", "status_flags")), _Tlv((1, "a", "uint8"), (2, "t", "string"), (3, "n", "uint32")));
        string types = _RecordTypes + "," + _Bitfield("status_flags", 16, """{"0":{"name":"ready"}}""");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types), ("someip.show_type_metadata", SettingValue.Bool(true)));
        SomeIpBytes tlv = new();
        _ = _WithTag(tlv, 0, 1).U8(1);
        _ = _WithTag(tlv, 7, 2).U32(3).Utf8("abc");
        _ = _WithTag(tlv, 2, 3).U32(77);
        _ = _WithTag(tlv, 2, 9).U32(78);
        byte[] request = new SomeIpBytes().U32(4 + 17).U32(2).Utf8("WVWZZZ1JZXW000001").U16(0x0001).ToArray();
        byte[] response = tlv.ToArray();

        for (int length = 0; length <= request.Length; length++)
        {
            Packet packet = s.Parse(UdpFrame(request.AsSpan(0, length).ToArray()));
            packet.MaterializeAll();
            await Assert.That(packet.IterFieldsDfs(materialize: false).Any()).IsTrue();
        }

        for (int length = 0; length <= response.Length; length++)
        {
            Packet packet = s.Parse(UdpFrame(response.AsSpan(0, length).ToArray(), messageType: 0x80));
            packet.MaterializeAll();
            await Assert.That(packet.IterFieldsDfs(materialize: false).Any()).IsTrue();
        }
    }

    [Test]
    [Arguments("arrays_and_e2e08.json")]
    [Arguments("full_service.json")]
    [Arguments("minimal_service.json")]
    [Arguments("unions_bitfields_strings.json")]
    public async Task EveryShippedExampleDecodesArbitraryBytesWithoutThrowing(string example)
    {
        string json = File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", example));
        using SomeIpTestStack s = SomeIpTestStack.Create(
            json, ("someip.show_type_metadata", SettingValue.Bool(true)), ("someip.verify_e2e_crc", SettingValue.Bool(true)));
        Random random = new(20260101);
        int parsed = 0;

        // Every member of every service, as request, response and notification, with payloads of every length up to 40 bytes
        // filled from a fixed seed: the decoder meets lengths that are too small, exactly right, and too large, and declared
        // lengths that are arbitrary, which is what the format hands a parser that trusts nothing.
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement service in document.RootElement.GetProperty("services").EnumerateArray())
        {
            ushort serviceId = service.GetProperty("service_id").GetUInt16();
            byte major = service.GetProperty("major_version").GetByte();
            foreach (JsonElement member in service.GetProperty("methods").EnumerateArray())
            {
                foreach (string idProperty in new[] { "id", "get_id", "set_id", "notifier_id" })
                {
                    if (!member.TryGetProperty(idProperty, out JsonElement id))
                    {
                        continue;
                    }

                    foreach (byte messageType in new byte[] { 0x00, 0x80, 0x02 })
                    {
                        for (int length = 0; length <= 40; length++)
                        {
                            byte[] payload = new byte[length];
                            random.NextBytes(payload);
                            byte[] message = SomeIpBytes.Message(serviceId, id.GetUInt16(), payload, messageType: messageType, interfaceVersion: major);
                            Packet packet = s.Parse(SomeIpFrames.Udp(message));
                            packet.MaterializeAll();
                            parsed++;
                        }
                    }
                }
            }
        }

        await Assert.That(parsed).IsGreaterThan(0);
    }

    [Test]
    public async Task RandomMutationsOfEveryFrameOfTheMixedCaptureNeverThrow()
    {
        SomeIpMixedCapture capture = SomeIpMixedCapture.Build();
        using SomeIpTestStack s = SomeIpTestStack.Create(SomeIpMixedCapture.Config, SomeIpMixedCapture.Settings);
        Random random = new(99);
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            _ = s.ParseAt(capture.Frames[i], SomeIpMixedCapture.TimeOf(i));
        }

        // Each frame is flipped at a few random positions and re-parsed under its own id: the tree of a damaged message may differ,
        // but parsing and materializing it must neither throw nor hang.
        int mutated = 0;
        for (int i = 0; i < capture.Frames.Count; i++)
        {
            for (int round = 0; round < 3; round++)
            {
                byte[] damaged = [.. capture.Frames[i]];
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    damaged[random.Next(Math.Min(damaged.Length, 120))] ^= (byte)random.Next(1, 256);
                }

                Packet packet = s.ReparseAt(damaged, i, SomeIpMixedCapture.TimeOf(i));
                packet.MaterializeAll();
                mutated++;
            }
        }

        await Assert.That(mutated).IsEqualTo(capture.Frames.Count * 3);
    }

    [Test]
    public async Task AnArrayAtTheLargestNodeLimitWithMetadataStaysWithinTheHostFieldCap()
    {
        // 60,000 nodes is the largest allowed limit; with the metadata view every element also carries a type node, so the tree
        // would exceed the 65,533 fields a packet may hold. The host must report that instead of throwing.
        string methods = Method(1, "huge", "[]", Sites(Site("v", "bytes_t")));
        string types = ArrayType("bytes_t", "uint8");
        using SomeIpTestStack s = SomeIpTestStack.Create(
            Json(methods, types), ("someip.max_payload_nodes", SettingValue.U64(60_000)), ("someip.show_type_metadata", SettingValue.Bool(true)));
        SomeIpBytes body = new SomeIpBytes().U32(40_000).Fill(1, 40_000);
        List<byte[]> fragments = SomeIpFrames.FragmentedUdp(SomeIpBytes.Message(ServiceId, 1, body.ToArray(), messageType: 0x80), 40000, 30490);
        Packet last = null!;
        foreach (byte[] fragment in fragments)
        {
            last = s.Parse(fragment);
        }

        last.MaterializeAll();

        await Assert.That(last.FieldCount(materialize: true)).IsLessThanOrEqualTo(65_534);
        await Assert.That(s.Count(last, "someip.payload.error") + s.Count(last, "packet.error")).IsGreaterThan(0);
    }

    [Test]
    public async Task TheDeepestAllowedTypeChainDecodesOnAWorkerThreadWithoutExhaustingTheStack()
    {
        // The depth limit may be raised to 1,024. A chain of 1,000 nested structures is the deepest the compiler expands; the decode
        // runs on a pool thread, whose stack is smaller than the main thread's.
        List<string> types = [];
        for (int i = 1; i < 1000; i++)
        {
            types.Add(StructType("level" + i, Site("next", "level" + (i + 1))));
        }

        types.Add(StructType("level1000", Site("leaf", "uint8")));
        string methods = Method(1, "deep", Sites(Site("top", "level1")), "[]", Transform(structLength: 0));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, string.Join(",", types)), ("someip.max_type_depth", SettingValue.U64(1024)));
        byte[] frame = UdpFrame([42]);

        int nodes = await Task.Run(() =>
        {
            Packet packet = s.Parse(frame);
            packet.MaterializeAll();
            return s.U64s(packet, Prefix + "deep.req.top.next.next.next".Replace(".next.next.next", string.Concat(Enumerable.Repeat(".next", 999)) + ".leaf")).Count;
        }).ConfigureAwait(false);

        await Assert.That(nodes).IsEqualTo(1);
    }

    [Test]
    public async Task ManyUnknownTagsAreBoundedByTheNodeLimit()
    {
        string methods = Method(1, "flood", "[]", _Tlv((1, "a", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods), ("someip.max_payload_nodes", SettingValue.U64(16)));
        SomeIpBytes payload = new();
        for (int i = 0; i < 100; i++)
        {
            _ = _WithTag(payload, 0, 9).U8(1);
        }

        Packet packet = s.Parse(UdpFrame(payload.ToArray(), messageType: 0x80));

        await Assert.That(s.Count(packet, "someip.payload.tlv.unknown")).IsEqualTo(16);
        await Assert.That(Errors(s, packet).Any(static e => e.Contains("someip.max_payload_nodes", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s.Count(packet, "someip.payload.unparsed")).IsEqualTo(1);
    }

    #endregion
}
