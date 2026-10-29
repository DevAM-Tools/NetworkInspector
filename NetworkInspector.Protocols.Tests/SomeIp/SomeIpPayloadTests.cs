// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The payload decoder for sequential parameter lists: base types and their byte order, lookup names, strings in every length
/// form and encoding, padding, arrays, structures, aliases, the request and response direction, the switches and the two limits.
/// Every test builds a small service description in JSON, sends one UDP datagram and reads the typed fields back by their
/// registered names, so each test states the description, the wire bytes and the names a filter would use.
/// </summary>
internal sealed class SomeIpPayloadTests : SomeIpPayloadKit
{
    #region Base types

    [Test]
    public async Task EveryBasePrimitiveDecodesToItsTypedField()
    {
        string methods = Method(
            1,
            "Base",
            Sites(
                Site("u8", "uint8"), Site("i8", "int8"), Site("u16", "uint16"), Site("i16", "int16"), Site("u32", "uint32"),
                Site("i32", "int32"), Site("u64", "uint64"), Site("i64", "int64"), Site("f32", "float"), Site("f64", "double"),
                Site("flag", "bool"), Site("id", "uuid")),
            "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        byte[] payload = new SomeIpBytes()
            .U8(255).I8(-1).U16(258).I16(-2).U32(0xDEADBEEF).I32(int.MinValue).U64(ulong.MaxValue).I64(long.MinValue)
            .F32(1.5f).F64(-2.25).U8(1)
            .Bytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF])
            .ToArray();

        Packet packet = s.Parse(UdpFrame(payload));

        await Assert.That(s.U64s(packet, InName("base", "u8"))).IsEquivalentTo(One(255UL));
        await Assert.That(I64s(s, packet, InName("base", "i8"))).IsEquivalentTo(One(-1L));
        await Assert.That(s.U64s(packet, InName("base", "u16"))).IsEquivalentTo(One(258UL));
        await Assert.That(I64s(s, packet, InName("base", "i16"))).IsEquivalentTo(One(-2L));
        await Assert.That(s.U64s(packet, InName("base", "u32"))).IsEquivalentTo(One(0xDEADBEEFUL));
        await Assert.That(I64s(s, packet, InName("base", "i32"))).IsEquivalentTo(One((long)int.MinValue));
        await Assert.That(s.U64s(packet, InName("base", "u64"))).IsEquivalentTo(One(ulong.MaxValue));
        await Assert.That(I64s(s, packet, InName("base", "i64"))).IsEquivalentTo(One(long.MinValue));
        await Assert.That(F64s(s, packet, InName("base", "f32"))).IsEquivalentTo(One(1.5));
        await Assert.That(F64s(s, packet, InName("base", "f64"))).IsEquivalentTo(One(-2.25));
        await Assert.That(Bools(s, packet, InName("base", "flag"))).IsEquivalentTo(One(true));

        List<Field> uuid = FieldsOf(s, packet, InName("base", "id"));
        await Assert.That(uuid.Count).IsEqualTo(1);
        await Assert.That(uuid[0].Value.Data.TryGetAsUuid(out Values.Uuid value)).IsTrue();
        await Assert.That(value.ToString()).IsEqualTo("00112233-4455-6677-8899-AABBCCDDEEFF");
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task LittleEndianTypesReadTheirBytesInReverse()
    {
        string methods = Method(1, "Order", Sites(Site("le", "u16_le"), Site("be", "uint16"), Site("d", "double_le")), "[]");
        string types = Join(BaseType("u16_le", "uint16", ",\"byte_order\":\"little_endian\""), BaseType("double_le", "double", ",\"byte_order\":\"little_endian\""));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        byte[] doubleLe = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(doubleLe, 1.5);
        byte[] payload = new SomeIpBytes().U8(0x02).U8(0x01).U8(0x02).U8(0x01).Bytes(doubleLe).ToArray();

        Packet packet = s.Parse(UdpFrame(payload));

        await Assert.That(s.U64s(packet, InName("order", "le"))).IsEquivalentTo(One(258UL));
        await Assert.That(s.U64s(packet, InName("order", "be"))).IsEquivalentTo(One(513UL));
        await Assert.That(F64s(s, packet, InName("order", "d"))).IsEquivalentTo(One(1.5));
    }

    [Test]
    public async Task LookupNamesAreShownForKnownValuesOnly()
    {
        string methods = Method(1, "Look", Sites(Site("state", "state_t"), Site("other", "state_t"), Site("sign", "sign_t")), "[]");
        string types = Join(
            BaseType("state_t", "uint8", ",\"lookup_values\":{\"1\":\"Idle\",\"2\":\"Active\"}"),
            BaseType("sign_t", "int8", ",\"lookup_values\":{\"-1\":\"Minus\"}"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame([2, 3, 0xFF]));

        await Assert.That(Texts(s, packet, InName("look", "state"))).IsEquivalentTo(One("Active (2)"));
        await Assert.That(Texts(s, packet, InName("look", "other"))).IsEquivalentTo(One("<none>"));
        await Assert.That(s.U64s(packet, InName("look", "other"))).IsEquivalentTo(One(3UL));
        await Assert.That(Texts(s, packet, InName("look", "sign"))).IsEquivalentTo(One("Minus (-1)"));
    }

    #endregion

    #region Strings

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(4)]
    public async Task AStringReadsItsLengthFieldOfTheConfiguredSize(int lengthSize)
    {
        string methods = Method(1, "Text", Sites(Site("t", "string")), "[]", Transform(stringLength: lengthSize));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        SomeIpBytes payload = new();
        _ = lengthSize switch
        {
            1 => payload.U8(3),
            2 => payload.U16(3),
            _ => payload.U32(3),
        };
        _ = payload.Utf8("abc");

        Packet packet = s.Parse(UdpFrame(payload.ToArray()));

        await Assert.That(s.Strings(packet, InName("text", "t"))).IsEquivalentTo(One("abc"));
        await Assert.That(Texts(s, packet, InName("text", "t"))).IsEquivalentTo(One("\"abc\" (3 bytes)"));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task AFixedLengthStringConsumesExactlyItsBytesAndNoLengthField()
    {
        string methods = Method(1, "Fixed", Sites(Site("t", "code8"), Site("after", "uint8")), "[]");
        string types = BaseType("code8", "string", ",\"fixed_length\":8");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().Utf8("abcdefgh").U8(7).ToArray()));

        await Assert.That(s.Strings(packet, InName("fixed", "t"))).IsEquivalentTo(One("abcdefgh"));
        await Assert.That(s.U64s(packet, InName("fixed", "after"))).IsEquivalentTo(One(7UL));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task AZeroLengthStringIsEmptyWithByteCountZero()
    {
        string methods = Method(1, "Empty", Sites(Site("t", "string"), Site("after", "uint8")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(0).U8(9).ToArray()));

        await Assert.That(s.Strings(packet, InName("empty", "t"))).IsEquivalentTo(One(""));
        await Assert.That(Texts(s, packet, InName("empty", "t"))).IsEquivalentTo(One("\"\" (0 bytes)"));
        await Assert.That(s.U64s(packet, InName("empty", "after"))).IsEquivalentTo(One(9UL));
    }

    [Test]
    public async Task EachEncodingDecodesItsBytesAndEscapesWhatIsNotText()
    {
        string methods = Join(
            Method(1, "Utf8", Sites(Site("t", "string")), "[]"),
            Method(2, "Be", Sites(Site("t", "s_be")), "[]"),
            Method(3, "Le", Sites(Site("t", "s_le")), "[]"),
            Method(4, "Ascii", Sites(Site("t", "s_ascii")), "[]"));
        string types = Join(
            BaseType("s_be", "string", ",\"encoding\":\"utf-16be\""),
            BaseType("s_le", "string", ",\"encoding\":\"utf-16le\""),
            BaseType("s_ascii", "string", ",\"encoding\":\"ascii\""));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet utf8 = s.Parse(UdpFrame(new SomeIpBytes().U32(3).Bytes([0x41, 0xFF, 0x42]).ToArray(), methodId: 1));
        Packet be = s.Parse(UdpFrame(new SomeIpBytes().U32(4).Bytes([0x00, 0x41, 0x00, 0x42]).ToArray(), methodId: 2));
        Packet le = s.Parse(UdpFrame(new SomeIpBytes().U32(4).Bytes([0x41, 0x00, 0x42, 0x00]).ToArray(), methodId: 3));
        Packet ascii = s.Parse(UdpFrame(new SomeIpBytes().U32(3).Bytes([0x41, 0x42, 0x00]).ToArray(), methodId: 4));
        Packet odd = s.Parse(UdpFrame(new SomeIpBytes().U32(3).Bytes([0x00, 0x41, 0xFF]).ToArray(), methodId: 2));

        await Assert.That(s.Strings(utf8, InName("utf8", "t"))).IsEquivalentTo(One("A\\xFFB"));
        await Assert.That(Texts(s, utf8, InName("utf8", "t"))).IsEquivalentTo(One("\"A\\xFFB\" (3 bytes)"));
        await Assert.That(s.Strings(be, InName("be", "t"))).IsEquivalentTo(One("AB"));
        await Assert.That(s.Strings(le, InName("le", "t"))).IsEquivalentTo(One("AB"));
        await Assert.That(s.Strings(ascii, InName("ascii", "t"))).IsEquivalentTo(One("AB"));
        await Assert.That(Texts(s, ascii, InName("ascii", "t"))).IsEquivalentTo(One("\"AB\" (3 bytes)"));
        await Assert.That(s.Strings(odd, InName("be", "t"))).IsEquivalentTo(One("A\\xFF"));
    }

    [Test]
    public async Task ATypeLengthSizeBeatsTheMethodAndTheMethodBeatsTheDefault()
    {
        string methods = Method(1, "Mix", Sites(Site("short_note", "note_t"), Site("plain", "string")), "[]", Transform(stringLength: 4));
        string types = BaseType("note_t", "string", ",\"size_of_length\":1");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U8(2).Utf8("hi").U32(3).Utf8("xyz").ToArray()));

        await Assert.That(s.Strings(packet, InName("mix", "short_note"))).IsEquivalentTo(One("hi"));
        await Assert.That(s.Strings(packet, InName("mix", "plain"))).IsEquivalentTo(One("xyz"));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task TwoMethodsShareOneStringTypeWithTheirOwnLengthSizes()
    {
        string methods = Join(
            Method(1, "Two", Sites(Site("z", "zone")), "[]", Transform(stringLength: 2)),
            Method(2, "Four", Sites(Site("z", "zone")), "[]", Transform(stringLength: 4)));
        string types = BaseType("zone", "string");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet two = s.Parse(UdpFrame(new SomeIpBytes().U16(2).Utf8("ab").ToArray(), methodId: 1));
        Packet four = s.Parse(UdpFrame(new SomeIpBytes().U32(2).Utf8("cd").ToArray(), methodId: 2));

        await Assert.That(s.Strings(two, InName("two", "z"))).IsEquivalentTo(One("ab"));
        await Assert.That(s.Strings(four, InName("four", "z"))).IsEquivalentTo(One("cd"));
        await Assert.That(Errors(s, two)).IsEmpty();
        await Assert.That(Errors(s, four)).IsEmpty();
    }

    [Test]
    public async Task TheTextDecoderEscapesControlCharactersLoneSurrogatesAndStripsFraming()
    {
        // Control characters would let a payload rewrite the display of a terminal; they are shown as the bytes they are.
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0x1B, 0x5B, 0x32, 0x4A }, SomeIpStringEncoding.Utf8)).IsEqualTo("\\x1B[2J");
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0x0A, 0x7F }, SomeIpStringEncoding.Ascii)).IsEqualTo("\\x0A\\x7F");

        // One terminating NUL and a byte order mark are framing, not text.
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xEF, 0xBB, 0xBF, 0x41, 0x00 }, SomeIpStringEncoding.Utf8)).IsEqualTo("A");
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x00, 0x00 }, SomeIpStringEncoding.Utf16BigEndian)).IsEqualTo("A");
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, SomeIpStringEncoding.Utf16LittleEndian)).IsEqualTo("A");

        // A lone surrogate is shown as the unit it is; a valid pair is one character; an interior NUL is a control character.
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xD8, 0x00, 0x00, 0x41 }, SomeIpStringEncoding.Utf16BigEndian)).IsEqualTo("\\uD800A");
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xD8, 0x3D, 0xDE, 0x00 }, SomeIpStringEncoding.Utf16BigEndian)).IsEqualTo("\U0001F600");
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0x41, 0x00, 0x42 }, SomeIpStringEncoding.Utf8)).IsEqualTo("A\\x00B");

        // A truncated multi-byte sequence escapes its bytes one by one and the text behind it survives.
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0xE2, 0x82, 0x41 }, SomeIpStringEncoding.Utf8)).IsEqualTo("\\xE2\\x82A");
        await Assert.That(SomeIpTextDecoder.Decode(ReadOnlyMemory<byte>.Empty, SomeIpStringEncoding.Utf8)).IsEqualTo(string.Empty);
        await Assert.That(SomeIpTextDecoder.Decode(new byte[] { 0x00 }, SomeIpStringEncoding.Utf8)).IsEqualTo(string.Empty);
    }

    #endregion

    #region Padding

    [Test]
    public async Task APaddedValueMovesTheNextParameterToTheAlignedOffset()
    {
        string methods = Method(1, "Pad", Sites(Site("t", "padded"), Site("after", "uint32")), "[]");
        string types = BaseType("padded", "string", ",\"pad_to\":4");
        byte[] payload = new SomeIpBytes().U32(2).Utf8("ab").U8(0xEE).U8(0xEE).U32(0xCAFEBABE).ToArray();

        using (SomeIpTestStack plain = SomeIpTestStack.Create(Json(methods, types)))
        {
            Packet packet = plain.Parse(UdpFrame(payload));
            await Assert.That(plain.U64s(packet, InName("pad", "after"))).IsEquivalentTo(One(0xCAFEBABEUL));
            await Assert.That(plain.Count(packet, "someip.payload.padding")).IsEqualTo(0);
        }

        using SomeIpTestStack metadata = SomeIpTestStack.Create(Json(methods, types), ("someip.show_type_metadata", SettingValue.Bool(true)));
        Packet shown = metadata.Parse(UdpFrame(payload));
        await Assert.That(metadata.U64s(shown, InName("pad", "after"))).IsEquivalentTo(One(0xCAFEBABEUL));
        await Assert.That(metadata.ByteLengths(shown, "someip.payload.padding")).IsEquivalentTo(One(2));
    }

    #endregion

    #region Arrays

    [Test]
    public async Task AVariableArrayShowsIndexedElementsUnderOneField()
    {
        string methods = Method(1, "Arr", Sites(Site("values", "bytes_t")), "[]");
        string types = ArrayType("bytes_t", "uint8");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(3).U8(10).U8(20).U8(30).ToArray()));

        string element = InName("arr", "values.item");
        await Assert.That(s.U64s(packet, element)).IsEquivalentTo(new List<ulong> { 10, 20, 30 });
        await Assert.That(Texts(s, packet, element)).IsEquivalentTo(new List<string> { "[0] 10", "[1] 20", "[2] 30" });
        await Assert.That(Texts(s, packet, InName("arr", "values"))).IsEquivalentTo(One("array[3]"));

        Packet empty = s.Parse(UdpFrame(new SomeIpBytes().U32(0).ToArray()));
        await Assert.That(Texts(s, empty, InName("arr", "values"))).IsEquivalentTo(One("array[0]"));
        await Assert.That(s.Count(empty, element)).IsEqualTo(0);
    }

    [Test]
    public async Task AnArrayThatDeclaresMoreBytesThanThereAreDecodesWhatFitsAndSaysSo()
    {
        string methods = Method(1, "Arr", Sites(Site("values", "words_t")), "[]");
        string types = ArrayType("words_t", "uint16");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(6).U16(1).U16(2).U8(3).ToArray()));

        await Assert.That(s.U64s(packet, InName("arr", "values.item"))).IsEquivalentTo(new List<ulong> { 1, 2 });
        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("declares 6 bytes but only 5 remain");
    }

    [Test]
    public async Task AFixedArrayHasNoLengthFieldAndConsumesExactlyItsElements()
    {
        string methods = Method(1, "Fix", Sites(Site("triple", "triple_t"), Site("after", "uint8")), "[]");
        string types = ArrayType("triple_t", "uint16", ",\"size\":3");
        byte[] payload = new SomeIpBytes().U16(1).U16(2).U16(3).U8(99).ToArray();

        using (SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types)))
        {
            Packet packet = s.Parse(UdpFrame(payload));
            await Assert.That(s.U64s(packet, InName("fix", "triple.item"))).IsEquivalentTo(new List<ulong> { 1, 2, 3 });
            await Assert.That(s.U64s(packet, InName("fix", "after"))).IsEquivalentTo(One(99UL));
            await Assert.That(Texts(s, packet, InName("fix", "triple"))).IsEquivalentTo(One("array (fixed 3)"));
            await Assert.That(Errors(s, packet)).IsEmpty();
        }

        using SomeIpTestStack metadata = SomeIpTestStack.Create(Json(methods, types), ("someip.show_type_metadata", SettingValue.Bool(true)));
        Packet shown = metadata.Parse(UdpFrame(payload));
        await Assert.That(metadata.Strings(shown, "someip.payload.type")).Contains("array fixed 3 of uint16");
    }

    [Test]
    public async Task ElementBoundsOutsideTheDescriptionAreReportedAndTheElementsStayVisible()
    {
        string methods = Method(1, "Lim", Sites(Site("v", "bounded")), "[]");
        string types = ArrayType("bounded", "uint8", ",\"min_elements\":2,\"max_elements\":2");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet three = s.Parse(UdpFrame(new SomeIpBytes().U32(3).U8(1).U8(2).U8(3).ToArray()));
        Packet one = s.Parse(UdpFrame(new SomeIpBytes().U32(1).U8(1).ToArray()));
        Packet two = s.Parse(UdpFrame(new SomeIpBytes().U32(2).U8(1).U8(2).ToArray()));

        await Assert.That(s.Count(three, InName("lim", "v.item"))).IsEqualTo(3);
        await Assert.That(Errors(s, three).Count).IsEqualTo(1);
        await Assert.That(Errors(s, three)[0]).Contains("at most 2");
        await Assert.That(Errors(s, one).Count).IsEqualTo(1);
        await Assert.That(Errors(s, one)[0]).Contains("at least 2");
        await Assert.That(Errors(s, two)).IsEmpty();
    }

    [Test]
    public async Task NestedArraysDecodeIntoRowsWithDerivedItemItemNames()
    {
        string methods = Method(1, "Mat", Sites(Site("matrix", "matrix_t")), "[]");
        string types = Join(
            ArrayType("row_t", "int16", ",\"size\":3"),
            ArrayType("matrix_t", "row_t", ",\"max_elements\":2"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        SomeIpBytes twoRows = new SomeIpBytes().U32(12).I16(1).I16(2).I16(3).I16(-4).I16(-5).I16(-6);

        Packet packet = s.Parse(UdpFrame(twoRows.ToArray()));
        Packet threeRows = s.Parse(UdpFrame(new SomeIpBytes().U32(18).Fill(0, 18).ToArray()));

        await Assert.That(I64s(s, packet, InName("mat", "matrix.item.item"))).IsEquivalentTo(new List<long> { 1, 2, 3, -4, -5, -6 });
        await Assert.That(Errors(s, packet)).IsEmpty();
        await Assert.That(s.Count(threeRows, InName("mat", "matrix.item.item"))).IsEqualTo(9);
        await Assert.That(Errors(s, threeRows).Count).IsEqualTo(1);
    }

    #endregion

    #region Structures and aliases

    [Test]
    public async Task AStructureConsumesItsLengthFieldAndMembersOrOnlyTheMembersWithoutOne()
    {
        string methods = Join(
            Method(1, "WithLen", Sites(Site("p", "pair"), Site("after", "uint8")), "[]"),
            Method(2, "NoLen", Sites(Site("p", "pair"), Site("after", "uint8")), "[]", Transform(structLength: 0)));
        string types = StructType("pair", Site("a", "uint16"), Site("b", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet withLength = s.Parse(UdpFrame(new SomeIpBytes().U32(3).U16(0x0102).U8(3).U8(77).ToArray(), methodId: 1));
        Packet withoutLength = s.Parse(UdpFrame(new SomeIpBytes().U16(0x0102).U8(3).U8(77).ToArray(), methodId: 2));

        await Assert.That(s.U64s(withLength, InName("withlen", "p.a"))).IsEquivalentTo(One(0x0102UL));
        await Assert.That(s.U64s(withLength, InName("withlen", "p.b"))).IsEquivalentTo(One(3UL));
        await Assert.That(s.U64s(withLength, InName("withlen", "after"))).IsEquivalentTo(One(77UL));
        await Assert.That(Texts(s, withLength, InName("withlen", "p"))).IsEquivalentTo(One("struct pair"));
        await Assert.That(s.U64s(withoutLength, InName("nolen", "p.b"))).IsEquivalentTo(One(3UL));
        await Assert.That(s.U64s(withoutLength, InName("nolen", "after"))).IsEquivalentTo(One(77UL));
    }

    [Test]
    public async Task AStructureLengthShorterThanItsMembersReportsTheMissingMemberAndTheNextParameterStartsBehindIt()
    {
        string methods = Method(1, "Short", Sites(Site("p", "pair"), Site("after", "uint8")), "[]");
        string types = StructType("pair", Site("a", "uint16"), Site("b", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(2).U16(0x0102).U8(0xAA).ToArray()));

        await Assert.That(s.U64s(packet, InName("short", "p.a"))).IsEquivalentTo(One(0x0102UL));
        await Assert.That(s.Count(packet, InName("short", "p.b"))).IsEqualTo(0);
        await Assert.That(s.U64s(packet, InName("short", "after"))).IsEquivalentTo(One(0xAAUL));
        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("does not fit");
    }

    [Test]
    public async Task AliasesDecodeLikeTheirTargetAndKeepTheirOwnName()
    {
        string methods = Method(1, "Als", Sites(Site("v", "speed"), Site("p", "pair_alias"), Site("w", "speed_alias")), "[]");
        string types = Join(
            BaseType("speed_base", "uint16"),
            """{"category":"alias","name":"speed","type_reference":"speed_base"}""",
            StructType("pair", Site("a", "uint8")),
            """{"category":"alias","name":"pair_alias","type_reference":"pair"}""",
            """{"category":"alias","name":"speed_alias","type_reference":"speed"}""");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U16(258).U32(1).U8(5).U16(259).ToArray()));

        await Assert.That(s.U64s(packet, InName("als", "v"))).IsEquivalentTo(One(258UL));
        await Assert.That(s.U64s(packet, InName("als", "p.a"))).IsEquivalentTo(One(5UL));
        await Assert.That(s.U64s(packet, InName("als", "w"))).IsEquivalentTo(One(259UL));
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task AMemberWithAMissingTypeShowsTheMarkerAndTheMembersAroundItDecode()
    {
        string methods = Method(1, "Ref", Sites(Site("p", "broken")), "[]");
        string types = StructType("broken", Site("a", "uint8"), Site("b", "no_such_type"), Site("c", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(2).U8(1).U8(3).ToArray()));

        await Assert.That(s.U64s(packet, InName("ref", "p.a"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.U64s(packet, InName("ref", "p.c"))).IsEquivalentTo(One(3UL));
        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("no_such_type");
    }

    #endregion

    #region Direction and member kinds

    [Test]
    public async Task ARequestDecodesTheInputListAndAResponseTheOutputList()
    {
        string methods = Method(1, "Dir", Sites(Site("a", "uint8"), Site("b", "uint16")), Sites(Site("r", "uint32")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        byte[] payload = [1, 0, 2];

        Packet request = s.Parse(UdpFrame(payload, messageType: 0x00));
        Packet noReturn = s.Parse(UdpFrame(payload, messageType: 0x01));
        Packet response = s.Parse(UdpFrame(payload, messageType: 0x80));

        await Assert.That(s.U64s(request, InName("dir", "a"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.U64s(request, InName("dir", "b"))).IsEquivalentTo(One(2UL));
        await Assert.That(s.U64s(noReturn, InName("dir", "a"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.Count(response, InName("dir", "a"))).IsEqualTo(0);
        await Assert.That(s.Count(response, OutName("dir", "r"))).IsEqualTo(0);
        await Assert.That(Errors(s, response).Count).IsEqualTo(1);
        await Assert.That(Errors(s, response)[0]).Contains("needs 4 bytes but only 3 remain");
    }

    [Test]
    public async Task AnErrorMessageShowsRawBytesAndNoParametersOrDiagnostics()
    {
        string methods = Method(1, "Err", Sites(Site("a", "uint8")), Sites(Site("r", "uint32")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet error = s.Parse(UdpFrame([0xDE, 0xAD], messageType: 0x81, methodId: 1));

        await Assert.That(s.ByteLengths(error, "someip.payload")).IsEquivalentTo(One(2));
        await Assert.That(s.Count(error, OutName("err", "r"))).IsEqualTo(0);
        await Assert.That(s.Count(error, InName("err", "a"))).IsEqualTo(0);
        await Assert.That(s.Count(error, "someip.payload.error")).IsEqualTo(0);
    }

    [Test]
    public async Task EventsAndTheAccessorsOfAFieldDecodeTheirType()
    {
        string methods = Join(
            """{"kind":"event","id":2,"name":"Evt","type_reference":"uint16"}""",
            """{"kind":"field","name":"Fan","get_id":10,"set_id":11,"notifier_id":12,"type_reference":"uint8"}""");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet notification = s.Parse(UdpFrame([0x01, 0x02], methodId: 2, messageType: 0x02));
        Packet getRequest = s.Parse(UdpFrame([], methodId: 10));
        Packet getResponse = s.Parse(UdpFrame([7], methodId: 10, messageType: 0x80));
        Packet setRequest = s.Parse(UdpFrame([8], methodId: 11));
        Packet setResponse = s.Parse(UdpFrame([9], methodId: 11, messageType: 0x80));
        Packet fieldNotification = s.Parse(UdpFrame([10], methodId: 12, messageType: 0x02));

        await Assert.That(s.U64s(notification, Prefix + "evt.uint16")).IsEquivalentTo(One(258UL));
        await Assert.That(s.Count(getRequest, "someip.payload.error")).IsEqualTo(0);
        await Assert.That(s.U64s(getResponse, Prefix + "fan_get.out.uint8")).IsEquivalentTo(One(7UL));
        await Assert.That(s.U64s(setRequest, Prefix + "fan_set.in.uint8")).IsEquivalentTo(One(8UL));
        await Assert.That(s.U64s(setResponse, Prefix + "fan_set.out.uint8")).IsEquivalentTo(One(9UL));
        await Assert.That(s.U64s(fieldNotification, Prefix + "fan_notifier.uint8")).IsEquivalentTo(One(10UL));
    }

    [Test]
    public async Task AMethodTheModelDoesNotDefineKeepsRawBytesAndTheUnknownMarker()
    {
        string methods = Method(1, "Known", Sites(Site("a", "uint8")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet packet = s.ParseIndexed(UdpFrame([1, 2, 3], methodId: 99), out PacketIndex index);

        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(One(3));
        await Assert.That(s.Count(packet, InName("known", "a"))).IsEqualTo(0);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.methodid", "0x0063 (unknown)");
        await Assert.That(s.HasGroup(index, packet, "someip.payload.svc.v1.known.in")).IsFalse();
    }

    #endregion

    #region Remainder, truncation and switches

    [Test]
    public async Task BytesBehindTheLastParameterAreShownAsUnparsedWithOneDiagnostic()
    {
        string methods = Method(1, "Rest", Sites(Site("a", "uint8")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet packet = s.Parse(UdpFrame([1, 2, 3]));

        await Assert.That(s.U64s(packet, InName("rest", "a"))).IsEquivalentTo(One(1UL));
        await Assert.That(s.ByteLengths(packet, "someip.payload.unparsed")).IsEquivalentTo(One(2));
        List<string> errors = Errors(s, packet);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("2 bytes remain");
    }

    [Test]
    public async Task DecodingSwitchedOffShowsRawBytesTheMarkerAndNoMemberGroup()
    {
        string methods = Method(1, "Off", Sites(Site("a", "uint8")), "[]");
        string json = Json(methods);
        byte[] frame = UdpFrame([5]);

        using (SomeIpTestStack off = SomeIpTestStack.Create(json, ("someip.decode_payload", SettingValue.Bool(false))))
        {
            Packet packet = off.ParseIndexed(frame, out PacketIndex index);
            await Assert.That(off.ByteLengths(packet, "someip.payload")).IsEquivalentTo(One(1));
            await Assert.That(off.Count(packet, "someip.payload.disabled")).IsEqualTo(1);
            await Assert.That(off.Count(packet, InName("off", "a"))).IsEqualTo(0);
            await Assert.That(off.HasGroup(index, packet, "someip.payload.svc.v1.off.in")).IsFalse();
        }

        using SomeIpTestStack on = SomeIpTestStack.Create(json);
        Packet decoded = on.ParseIndexed(frame, out PacketIndex onIndex);
        await Assert.That(on.U64s(decoded, InName("off", "a"))).IsEquivalentTo(One(5UL));
        await Assert.That(on.Count(decoded, "someip.payload.disabled")).IsEqualTo(0);
        await Assert.That(on.HasGroup(onIndex, decoded, "someip.payload.svc.v1.off.in")).IsTrue();
    }

    [Test]
    public async Task TruncatedPayloadsDecodeWhatFitsAndNeverThrow()
    {
        string methods = Join(
            Method(1, "Arr", Sites(Site("v", "bytes_t"), Site("after", "uint8")), "[]"),
            Method(2, "Txt", Sites(Site("t", "string"), Site("after", "uint8")), "[]"),
            Method(3, "Nop", Sites(Site("a", "uint8")), "[]"));
        string types = ArrayType("bytes_t", "uint8");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet array = s.Parse(UdpFrame(new SomeIpBytes().U32(100).Fill(7, 10).ToArray(), methodId: 1));
        Packet text = s.Parse(UdpFrame(new SomeIpBytes().U32(50).Bytes([0x41, 0x1B, 0x42, 0xFF]).ToArray(), methodId: 2));
        Packet empty = s.Parse(UdpFrame([], methodId: 3));

        await Assert.That(s.Count(array, InName("arr", "v.item"))).IsEqualTo(10);
        await Assert.That(s.Count(array, InName("arr", "after"))).IsEqualTo(0);
        await Assert.That(Errors(s, array).Count).IsEqualTo(1);
        await Assert.That(s.Strings(text, InName("txt", "t"))).IsEquivalentTo(One("A\\x1BB\\xFF"));
        await Assert.That(s.Count(text, InName("txt", "after"))).IsEqualTo(0);
        await Assert.That(Errors(s, text).Count).IsEqualTo(1);
        await Assert.That(Errors(s, empty).Count).IsEqualTo(1);
        await Assert.That(Errors(s, empty)[0]).Contains("no payload");
    }

    [Test]
    public async Task EveryPrefixOfAMessageDecodesWithDiagnosticsOnly()
    {
        string methods = Method(1, "Cut", Sites(Site("p", "rec"), Site("v", "words_t"), Site("t", "string")), "[]");
        string types = Join(StructType("rec", Site("a", "uint16"), Site("b", "double")), ArrayType("words_t", "uint16"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        byte[] full = new SomeIpBytes().U32(10).U16(1).F64(2.5).U32(4).U16(5).U16(6).U32(2).Utf8("hi").ToArray();

        for (int length = 0; length <= full.Length; length++)
        {
            Packet packet = s.Parse(UdpFrame(full.AsSpan(0, length).ToArray()));
            packet.MaterializeAll();
            await Assert.That(packet.IterFieldsDfs(materialize: false).Any()).IsTrue();
        }
    }

    #endregion

    #region Metadata

    [Test]
    public async Task TheMetadataViewAddsTypeLengthAndCountNodesWithoutChangingAnyValue()
    {
        string methods = Method(1, "Meta", Sites(Site("p", "pair"), Site("v", "bytes_t")), "[]");
        string types = Join(StructType("pair", Site("a", "uint16")), ArrayType("bytes_t", "uint8"));
        byte[] payload = new SomeIpBytes().U32(2).U16(7).U32(2).U8(1).U8(2).ToArray();

        using SomeIpTestStack plain = SomeIpTestStack.Create(Json(methods, types));
        using SomeIpTestStack meta = SomeIpTestStack.Create(Json(methods, types), ("someip.show_type_metadata", SettingValue.Bool(true)));
        Packet without = plain.Parse(UdpFrame(payload));
        Packet with = meta.Parse(UdpFrame(payload));

        await Assert.That(plain.Count(without, "someip.payload.type")).IsEqualTo(0);
        await Assert.That(plain.Count(without, "someip.payload.length")).IsEqualTo(0);
        await Assert.That(plain.Count(without, "someip.payload.array.count")).IsEqualTo(0);
        await Assert.That(meta.Strings(with, "someip.payload.type")).Contains("structure pair");
        await Assert.That(meta.Strings(with, "someip.payload.type")).Contains("array of uint8");
        await Assert.That(meta.U64s(with, "someip.payload.length")).IsEquivalentTo(new List<ulong> { 2, 2 });
        await Assert.That(meta.U64s(with, "someip.payload.array.count")).IsEquivalentTo(One(2UL));
        await Assert.That(meta.U64s(with, InName("meta", "p.a"))).IsEquivalentTo(plain.U64s(without, InName("meta", "p.a")));
        await Assert.That(meta.U64s(with, InName("meta", "v.item"))).IsEquivalentTo(plain.U64s(without, InName("meta", "v.item")));
    }

    #endregion

    #region Laziness and replay

    [Test]
    public async Task ParsingDecodesNothingAndMaterializingDecodesExactlyThatPacket()
    {
        string methods = Method(1, "Lazy", Sites(Site("a", "uint8")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));
        List<Packet> packets = [];
        for (int i = 0; i < 100; i++)
        {
            packets.Add(s.Parse(UdpFrame([(byte)i])));
        }

        FieldId payloadId = s.Stack.GetFieldId("someip.payload")!.Value;
        foreach (Packet packet in packets)
        {
            Field container = packet.IterFieldsDfs(materialize: false).First(f => f.FieldId == payloadId);
            await Assert.That(container.ChildCount(materialize: false)).IsEqualTo((ushort)0);
        }

        // Reading one container's children materializes that one only.
        Field first = packets[0].IterFieldsDfs(materialize: false).First(f => f.FieldId == payloadId);
        await Assert.That(first.ChildCount(materialize: true)).IsEqualTo((ushort)1);
        Field second = packets[1].IterFieldsDfs(materialize: false).First(f => f.FieldId == payloadId);
        await Assert.That(second.ChildCount(materialize: false)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task AReparseGivesTheIdenticalMaterializedTree()
    {
        string methods = Method(1, "Rep", Sites(Site("p", "rec"), Site("v", "bytes_t")), "[]");
        string types = Join(StructType("rec", Site("a", "uint16"), Site("t", "string")), ArrayType("bytes_t", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types), ("someip.show_type_metadata", SettingValue.Bool(true)));
        byte[][] frames =
        [
            UdpFrame(new SomeIpBytes().U32(8).U16(1).U32(2).Utf8("ab").U32(2).U8(1).U8(2).ToArray()),
            UdpFrame(new SomeIpBytes().U32(50).U16(1).Fill(7, 10).ToArray()),
        ];

        for (int i = 0; i < frames.Length; i++)
        {
            Packet first = s.Parse(frames[i]);
            first.MaterializeAll();
            Packet again = s.Reparse(frames[i], i);
            again.MaterializeAll();
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first, again);
        }

    }

    [Test]
    public async Task ConcurrentMaterializationOfOnePacketGivesIdenticalValues()
    {
        string methods = Method(1, "Par", Sites(Site("v", "bytes_t")), "[]");
        string types = ArrayType("bytes_t", "uint8");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(64).Fill(3, 64).ToArray()));

        Task<int>[] workers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() => s.Count(packet, InName("par", "v.item"))))];
        int[] counts = await Task.WhenAll(workers);

        await Assert.That(counts.All(static c => c == 64)).IsTrue();
    }

    #endregion

    #region Limits

    [Test]
    public async Task ADeepTypeChainIsCutAtTheDepthLimitAndTheSettingNamesIt()
    {
        string methods = Method(1, "Deep", Sites(Site("top", "level1")), "[]", Transform(structLength: 0));
        List<string> types = [];
        for (int i = 1; i < 70; i++)
        {
            types.Add(StructType("level" + i, Site("next", "level" + (i + 1))));
        }

        types.Add(StructType("level70", Site("leaf", "uint8")));
        string json = Json(methods, string.Join(",", types));
        byte[] frame = UdpFrame([42]);

        using (SomeIpTestStack defaults = SomeIpTestStack.Create(json))
        {
            Packet packet = defaults.Parse(frame);
            int nodes = _CountContainers(defaults, packet, Prefix + "deep.in");
            await Assert.That(nodes).IsEqualTo(64);
            List<string> errors = Errors(defaults, packet);
            await Assert.That(errors.Count).IsEqualTo(1);
            await Assert.That(errors[0]).Contains("someip.max_type_depth").And.Contains("64");
        }

        using (SomeIpTestStack raised = SomeIpTestStack.Create(json, ("someip.max_type_depth", SettingValue.U64(128))))
        {
            Packet packet = raised.Parse(frame);
            await Assert.That(_CountContainers(raised, packet, Prefix + "deep.in")).IsEqualTo(70);
            await Assert.That(Errors(raised, packet)).IsEmpty();
        }

        using SomeIpTestStack one = SomeIpTestStack.Create(json, ("someip.max_type_depth", SettingValue.U64(1)));
        Packet shallow = one.Parse(frame);
        await Assert.That(_CountContainers(one, shallow, Prefix + "deep.in")).IsEqualTo(1);
        await Assert.That(Errors(one, shallow).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ALargeArrayIsCutAtTheNodeLimitAndTheSettingNamesIt()
    {
        string methods = Method(1, "Big", Sites(Site("v", "bytes_t")), "[]");
        string types = ArrayType("bytes_t", "uint8");
        string json = Json(methods, types);
        byte[] datagram = new SomeIpBytes().U32(20000).Fill(1, 20000).ToArray();

        using (SomeIpTestStack defaults = SomeIpTestStack.Create(json))
        {
            Packet packet = _ParseDatagram(defaults, datagram);
            await Assert.That(defaults.Count(packet, InName("big", "v.item"))).IsEqualTo(16383);
            List<string> errors = Errors(defaults, packet);
            await Assert.That(errors.Count).IsEqualTo(1);
            await Assert.That(errors[0]).Contains("someip.max_payload_nodes").And.Contains("16384");
            await Assert.That(defaults.ByteLengths(packet, "someip.payload.unparsed").Count).IsEqualTo(1);
        }

        using SomeIpTestStack small = SomeIpTestStack.Create(json, ("someip.max_payload_nodes", SettingValue.U64(1000)));
        Packet limited = _ParseDatagram(small, datagram);
        await Assert.That(small.Count(limited, InName("big", "v.item"))).IsEqualTo(999);
        await Assert.That(Errors(small, limited)[0]).Contains("1000");
    }

    /// <summary>
    /// Parses a SOME/IP message that is larger than one Ethernet frame: the datagram travels as IPv4 fragments and the last
    /// fragment completes it, so the packet that holds the message is the last one.
    /// </summary>
    private static Packet _ParseDatagram(SomeIpTestStack s, byte[] payload)
    {
        Packet? last = null;
        byte[] message = SomeIpBytes.Message(ServiceId, 1, payload);
        foreach (byte[] fragment in SomeIpFrames.FragmentedUdp(message, 40000, 30490))
        {
            last = s.Parse(fragment);
        }

        return last!;
    }

    private static int _CountContainers(SomeIpTestStack s, Packet packet, string prefix)
    {
        int count = 0;
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            FieldInfo? info = s.Stack.GetField(field.FieldId);
            if (info is not null && info.FieldType == FieldType.None && info.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    #endregion

    #region Names

    [Test]
    public async Task ADerivedNameAndAConfiguredFilterNameSelectTheSameValue()
    {
        string methods = Method(
            1, "SetTemp", Sites(Site("request", "req_t")), "[]");
        string types = Join(
            StructType("req_t", Site("target_c", "int16", ",\"filter_name\":\"climate.settemp.target_c\"")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(2).I16(21).ToArray()));

        await Assert.That(I64s(s, packet, "climate.settemp.target_c")).IsEquivalentTo(One(21L));
        await Assert.That(s.Stack.GetFieldId(InName("settemp", "request.target_c"))).IsNull();
        await Assert.That(s.Stack.GetFieldId(InName("settemp", "request"))).IsNotNull();
        await Assert.That(s.Warnings).IsEmpty();
    }

    [Test]
    public async Task AFilterNameThatEqualsABuiltInFieldFallsBackToTheGenericFieldWithAWarning()
    {
        string methods = Method(1, "Clash", Sites(Site("a", "uint8", ",\"filter_name\":\"someip.payload.error\"")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet packet = s.Parse(UdpFrame([5]));

        await Assert.That(s.U64s(packet, "someip.payload.value.u64")).IsEquivalentTo(One(5UL));
        await Assert.That(s.Warnings.Any(static w => w.Message.Contains("someip.payload.error", StringComparison.Ordinal))).IsTrue();
    }

    #endregion

    #region Registration

    [Test]
    public async Task TheSameTypeUnderTwoMethodsRegistersTwoFieldsAndOnlyTheDecodedOneIsInThePacket()
    {
        string methods = Join(
            Method(1, "A", Sites(Site("p", "holder")), "[]"),
            Method(2, "B", Sites(Site("p", "holder")), "[]"));
        string types = StructType("holder", Site("x", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        FieldId? first = s.Stack.GetFieldId(InName("a", "p.x"));
        FieldId? second = s.Stack.GetFieldId(InName("b", "p.x"));
        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(1).U8(5).ToArray(), methodId: 1));

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(s.U64s(packet, InName("a", "p.x"))).IsEquivalentTo(One(5UL));
        await Assert.That(s.Count(packet, InName("b", "p.x"))).IsEqualTo(0);
    }

    [Test]
    public async Task AFilterNameAndADisplayNameAreRegisteredExactlyAndDefaultsFollowTheMemberName()
    {
        string methods = Method(1, "Name", Sites(Site("request", "req_t")), "[]");
        string types = StructType(
            "req_t",
            Site("target_c", "int16", ",\"filter_name\":\"climate.settemp.target_c\",\"ui_name\":\"Target Temperature (Celsius)\""),
            Site("zone", "uint8"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        FieldInfo? overridden = s.Stack.GetField(s.Stack.GetFieldId("climate.settemp.target_c")!.Value);
        FieldInfo? derived = s.Stack.GetField(s.Stack.GetFieldId(InName("name", "request.zone"))!.Value);

        await Assert.That(overridden!.UiName).IsEqualTo("Target Temperature (Celsius)");
        await Assert.That(derived!.UiName).IsEqualTo("zone");
        await Assert.That(derived.Name).IsEqualTo(InName("name", "request.zone"));
        await Assert.That(s.Stack.GetIndexGroup(s.Stack.GetFieldIndexGroup(derived.Id))?.Name).IsEqualTo(Prefix + "name.in");
    }

    [Test]
    public async Task ThePayloadFieldsOfTheFullExampleAreListedWithTheirMemberDirectionGroups()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "full_service.json")));

        FieldId target = s.Stack.GetFieldId("climate.settemp.target_c")!.Value;
        FieldId zone = s.Stack.GetFieldId("someip.payload.climatecontrol.v2.settemperature.in.request.zone")!.Value;
        FieldId result = s.Stack.GetFieldId("someip.payload.climatecontrol.v2.settemperature.out.ack_result")!.Value;
        FieldId fan = s.Stack.GetFieldId("someip.payload.climatecontrol.v2.fanspeed_notifier.fan_speed")!.Value;

        await Assert.That(s.Stack.GetIndexGroup(s.Stack.GetFieldIndexGroup(target))?.Name)
            .IsEqualTo("someip.payload.climatecontrol.v2.settemperature.in");
        await Assert.That(s.Stack.GetIndexGroup(s.Stack.GetFieldIndexGroup(zone))?.Name)
            .IsEqualTo("someip.payload.climatecontrol.v2.settemperature.in");
        await Assert.That(s.Stack.GetIndexGroup(s.Stack.GetFieldIndexGroup(result))?.Name)
            .IsEqualTo("someip.payload.climatecontrol.v2.settemperature.out");
        await Assert.That(s.Stack.GetIndexGroup(s.Stack.GetFieldIndexGroup(fan))?.Name)
            .IsEqualTo("someip.payload.climatecontrol.v2.fanspeed_notifier");
        await Assert.That(s.Stack.GetField(target)!.FieldType).IsEqualTo(FieldType.I64);
        await Assert.That(s.Stack.GetField(zone)!.FieldType).IsEqualTo(FieldType.String);
    }

    #endregion

    #region Index

    [Test]
    public async Task TheMemberDirectionGroupIsRecordedForADecodedMessageAndTheIndexStaysExact()
    {
        string methods = Method(1, "Idx", Sites(Site("a", "uint8"), Site("b", "uint8")), Sites(Site("r", "uint8")));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods));

        Packet request = s.ParseIndexed(UdpFrame([1, 2]), out PacketIndex requestIndex);
        Packet response = s.ParseIndexed(UdpFrame([3], messageType: 0x80), out PacketIndex responseIndex);

        await Assert.That(s.HasGroup(requestIndex, request, "someip.payload.svc.v1.idx.in")).IsTrue();
        await Assert.That(s.HasGroup(requestIndex, request, "someip.payload.svc.v1.idx.out")).IsFalse();
        await Assert.That(s.HasGroup(responseIndex, response, "someip.payload.svc.v1.idx.out")).IsTrue();
        await s.AssertIndexExact(requestIndex, request);
        await s.AssertIndexExact(responseIndex, response);
    }


    [Test]
    public async Task TheIndexStaysExactForEveryShapeOfDecodedPayload()
    {
        string methods = Method(1, "Shape", Sites(Site("p", "rec"), Site("v", "words_t"), Site("t", "string")), "[]");
        string types = Join(StructType("rec", Site("a", "uint16"), Site("b", "uint8")), ArrayType("words_t", "uint16"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        byte[][] payloads =
        [
            new SomeIpBytes().U32(3).U16(1).U8(2).U32(4).U16(5).U16(6).U32(2).Utf8("hi").ToArray(),
            new SomeIpBytes().U32(3).U16(1).U8(2).U32(100).U16(5).ToArray(),
            new SomeIpBytes().U32(3).U16(1).U8(2).U32(0).U32(0).U8(9).ToArray(),
        ];

        foreach (byte[] payload in payloads)
        {
            Packet packet = s.ParseIndexed(UdpFrame(payload), out PacketIndex index);
            await s.AssertIndexExact(index, packet);
        }
    }

    [Test]
    public async Task TheMemberGroupIsASupersetWhenNoParameterSurvivesAndTheTreeStaysTheJudge()
    {
        string methods = Method(1, "Sup", Sites(Site("p", "rec")), "[]");
        string types = StructType("rec", Site("a", "uint16"));
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        // Two bytes cannot hold the four-byte length field of the structure: no parameter exists, but the message is a message of
        // this member, so the index offers it as a candidate and the filter finds no field on the tree.
        Packet packet = s.ParseIndexed(UdpFrame([1, 2]), out PacketIndex index);

        await Assert.That(s.HasGroup(index, packet, Prefix + "sup.in")).IsTrue();
        await Assert.That(s.Count(packet, InName("sup", "p.a"))).IsEqualTo(0);
        await Assert.That(Errors(s, packet).Count).IsEqualTo(1);
    }
    #endregion
}
