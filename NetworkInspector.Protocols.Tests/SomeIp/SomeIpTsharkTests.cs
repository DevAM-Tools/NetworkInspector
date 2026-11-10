// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// Cross-validation of the SOME/IP parser against the external dissector: header, service discovery, segmentation, TCP framing and
/// the decoded payload of a configuration that is written to both sides. Each test builds the frames by hand, lets the parser and the
/// tool dissect the same bytes, and compares the values field by field.
/// <para>
/// The tool is mandatory (<see cref="TsharkVerifier"/>); a developer machine without it may set the documented escape hatch, which
/// turns the test into a deliberate skip. The tool does not claim port 30490 by itself, so every call passes an explicit
/// decode-as rule. Known intended differences are asserted as differences, with the reason beside the assertion.
/// </para>
/// <para><b>Thread-safety:</b> stateless tests; every test uses its own temporary files.</para>
/// </summary>
internal sealed class SomeIpTsharkTests : SomeIpPayloadKit
{
    private const string _UdpDecodeAs = "udp.port==30490,someip";
    private const string _TcpDecodeAs = "tcp.port==30490,someip";

    private static byte[] _Udp(byte[] message) => FrameBuilderTestExtensions.WrapRawUdp(message, 12345, 30490);

    #region Header

    [Test]
    [Arguments((byte)0x00, (byte)0x00)]
    [Arguments((byte)0x80, (byte)0x00)]
    [Arguments((byte)0x81, (byte)0x05)]
    [Arguments((byte)0x02, (byte)0x00)]
    public async Task EveryHeaderFieldEqualsTheToolsValue(byte messageType, byte returnCode)
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] frame = _Udp(SomeIpBytes.Message(0x1234, 0x0042, [0xDE, 0xAD, 0xBE, 0xEF], 0x0001, 0x0010, messageType, returnCode, 3));
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(
                stack,
                packet,
                frame,
                1,
                null,
                _UdpDecodeAs,
                ("someip.messageid", "someip.messageid"),
                ("someip.serviceid", "someip.serviceid"),
                ("someip.methodid", "someip.methodid"),
                ("someip.length", "someip.length"),
                ("someip.clientid", "someip.clientid"),
                ("someip.sessionid", "someip.sessionid"),
                ("someip.msgtype", "someip.messagetype"),
                ("someip.msgtype.ack", "someip.messagetype.ack"),
                ("someip.msgtype.tp", "someip.messagetype.tp"),
                ("someip.returncode", "someip.returncode")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Service discovery

    [Test]
    public async Task AnOfferWithAnEndpointOptionEqualsTheTool()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] frame = new SomeIpSdBuilder()
            .Offer(0x1234, 0x0001, 2, 7, 3)
            .Ipv4Option([192, 168, 1, 10], 0x11, 30501)
            .BuildFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(
                stack,
                packet,
                frame,
                1,
                null,
                _UdpDecodeAs,
                ("someip_sd.flags.reboot", "someipsd.flags.reboot"),
                ("someip_sd.flags.unicast", "someipsd.flags.unicast"),
                ("someip_sd.entries_length", "someipsd.length_entriesarray"),
                ("someip_sd.options_length", "someipsd.length_optionsarray"),
                ("someip_sd.entry.type", "someipsd.entry.type"),
                ("someip_sd.entry.index1", "someipsd.entry.index1"),
                ("someip_sd.entry.n_opt_1", "someipsd.entry.numopt1"),
                ("someip_sd.entry.serviceid", "someipsd.entry.serviceid"),
                ("someip_sd.entry.instanceid", "someipsd.entry.instanceid"),
                ("someip_sd.entry.majorver", "someipsd.entry.majorver"),
                ("someip_sd.entry.ttl", "someipsd.entry.ttl"),
                ("someip_sd.entry.minorver", "someipsd.entry.minorver"),
                ("someip_sd.option.length", "someipsd.option.length"),
                ("someip_sd.option.type", "someipsd.option.type"),
                ("someip_sd.option.ipv4", "someipsd.option.ipv4address"),
                ("someip_sd.option.port", "someipsd.option.port"),
                ("someip_sd.option.proto", "someipsd.option.proto")).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task AConfigurationOptionEqualsTheToolsLengthAndType()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        // One option per frame: the tool lists every occurrence of a repeated field and the comparison reads the first.
        byte[] frame = new SomeIpSdBuilder()
            .Offer(0x1234, 0x0001, 2, 7, 3)
            .ConfigurationOption("key=value")
            .BuildFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(
                stack,
                packet,
                frame,
                1,
                null,
                _UdpDecodeAs,
                ("someip_sd.option.length", "someipsd.option.length"),
                ("someip_sd.option.type", "someipsd.option.type")).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task ASubscribeEntryAndALoadBalancingOptionEqualTheTool()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] frame = new SomeIpSdBuilder()
            .Subscribe(0x1234, 0x0001, 2, 5, eventgroup: 0x0010, counter: 3, initial: true)
            .LoadBalancingOption(priority: 4, weight: 9)
            .BuildFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(
                stack,
                packet,
                frame,
                1,
                null,
                _UdpDecodeAs,
                ("someip_sd.entry.type", "someipsd.entry.type"),
                ("someip_sd.entry.eventgroupid", "someipsd.entry.eventgroupid"),
                ("someip_sd.entry.counter", "someipsd.entry.counter"),
                ("someip_sd.entry.ttl", "someipsd.entry.ttl"),
                ("someip_sd.option.type", "someipsd.option.type"),
                ("someip_sd.option.lb_priority", "someipsd.option.priority"),
                ("someip_sd.option.lb_weight", "someipsd.option.weight")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Segmentation

    [Test]
    public async Task TheSegmentFieldsAndTheReassembledLengthEqualTheTool()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[][] frames =
        [
            SomeIpFrames.TpFrame(0, new byte[16], more: true, messageType: 0x20),
            SomeIpFrames.TpFrame(16, new byte[16], more: true, messageType: 0x20),
            SomeIpFrames.TpFrame(32, new byte[8], more: false, messageType: 0x20),
        ];
        (Stack stack, Packet[] packets) = _ParseAll(frames);
        using (stack)
        {
            string[] fields = ["frame.number", "someip.messagetype.tp", "someip.tp.offset", "someip.tp.flags.more_segments", "someip.tp.reassembled.length"];
            List<string?[]> tool = TsharkVerifier.GetFieldValuesPerPacket(frames, fields, 1, _UdpDecodeAs);

            for (int i = 0; i < frames.Length; i++)
            {
                await Assert.That(_IsTrue(tool[i][1])).IsTrue().Because("the TP flag of every segment");
                await Assert.That(ulong.Parse(tool[i][2]!, CultureInfo.InvariantCulture)).IsEqualTo(_U64(stack, packets[i], "someip.tp.offset"));
                await Assert.That(_IsTrue(tool[i][3])).IsEqualTo(_Bool(stack, packets[i], "someip.tp.more"));
            }

            // The tool reports the reassembled length on the segment that completes the message; the parser does the same.
            await Assert.That(ulong.Parse(tool[2][4]!, CultureInfo.InvariantCulture)).IsEqualTo(_U64(stack, packets[2], "someip.tp.reassembled_length"));
            await Assert.That(_U64(stack, packets[2], "someip.tp.reassembled_length")).IsEqualTo(40UL);
        }
    }

    #endregion

    #region TCP framing

    [Test]
    public async Task TwoMessagesSplitAcrossThreeSegmentsAreReportedOnTheSamePacketsAsByTheTool()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] first = SomeIpBytes.Message(0x1234, 0x0001, new byte[24]);
        byte[] second = SomeIpBytes.Message(0x1234, 0x0002, new byte[8]);
        TcpTestConversation conversation = new(30490);
        conversation.Handshake();
        conversation.ClientSend(SomeIpBytes.Concat(first, second), mss: 28);
        byte[][] frames = [.. conversation.Frames];
        (Stack stack, Packet[] packets) = _ParseAll(frames);
        using (stack)
        {
            List<string?[]> tool = TsharkVerifier.GetFieldValuesPerPacket(frames, ["frame.number", "someip.messageid"], 1, _TcpDecodeAs);

            for (int i = 0; i < frames.Length; i++)
            {
                List<ulong> own = [];
                if (stack.GetFieldId("someip.messageid") is { } id)
                {
                    own.AddRange(_AllU64(packets[i], id));
                }

                List<ulong> theirs = [];
                if (tool[i][1] is { Length: > 0 } text)
                {
                    theirs.AddRange(text.Split(',').Select(static t => _ParseHex(t)));
                }

                await Assert.That(own).IsEquivalentTo(theirs).Because(Invariant($"message ids reported on packet {i}"));
            }
        }
    }

    #endregion

    #region Payload

    private static readonly string _PayloadConfig = Json(
        Join(
            Method(
                1,
                "seq",
                Sites(
                    Site("a", "uint8"),
                    Site("b", "i16le"),
                    Site("c", "float"),
                    Site("d", "state_t"),
                    Site("e", "words_t"),
                    Site("f", "triple_t"),
                    Site("g", "rec"),
                    Site("h", "flags_t"),
                    Site("i", "string"),
                    Site("j", "uint64")),
                "[]"),
            Method(
                2,
                "tlv",
                "[]",
                "{\"1\":{\"name\":\"t1\",\"type_reference\":\"uint8\"},\"2\":{\"name\":\"t2\",\"type_reference\":\"uint16\"},"
                + "\"3\":{\"name\":\"t3\",\"type_reference\":\"string\"}}"),
            Method(3, "uni", "[]", Sites(Site("u", "union_t"))),
            "{\"kind\":\"event\",\"id\":4,\"name\":\"Ev\",\"type_reference\":\"wide_flags_t\"}"),
        Join(
            "{\"category\":\"base_type\",\"name\":\"i16le\",\"type_reference\":\"int16\",\"byte_order\":\"little_endian\"}",
            "{\"category\":\"base_type\",\"name\":\"state_t\",\"type_reference\":\"uint8\",\"lookup_values\":{\"1\":\"Idle\",\"2\":\"Active\"}}",
            "{\"category\":\"array\",\"name\":\"words_t\",\"type_reference\":\"uint16\"}",
            "{\"category\":\"array\",\"name\":\"triple_t\",\"type_reference\":\"uint8\",\"size\":3}",
            "{\"category\":\"structure\",\"name\":\"rec\",\"members\":[{\"name\":\"x\",\"type_reference\":\"uint8\"},{\"name\":\"y\",\"type_reference\":\"uint16\"}]}",
            "{\"category\":\"bitfield\",\"name\":\"flags_t\",\"bits\":8,\"members\":{\"0\":{\"name\":\"ready\"},\"3\":{\"name\":\"fault\"}}}",
            "{\"category\":\"bitfield\",\"name\":\"wide_flags_t\",\"bits\":16,\"members\":{\"0\":{\"name\":\"low\"},\"15\":{\"name\":\"high\"}}}",
            "{\"category\":\"union\",\"name\":\"union_t\",\"members\":{\"1\":{\"name\":\"ua\",\"type_reference\":\"uint16\"},\"2\":{\"name\":\"ub\",\"type_reference\":\"uint8\"}}}"));

    [Test]
    public async Task EveryDecodedLeafOfSequentialTlvUnionAndBitfieldPayloadsEqualsTheToolsValue()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        string work = Path.Combine(Path.GetTempPath(), "ni_someip_uat_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(work);
        try
        {
            using SomeIpTestStack s = SomeIpTestStack.Create(_PayloadConfig);
            SomeIpCompiledModel model = _Compile(_PayloadConfig);
            TsharkSomeIpUatProfile profile = TsharkSomeIpUatProfile.Emit(work, "ni_someip", model, out string profileDirectory);

            SomeIpBytes sequential = new SomeIpBytes()
                .U8(200).U8(0x34).U8(0x12).F32(1.5f).U8(2)
                .U32(4).U16(10).U16(20)
                .U8(7).U8(8).U8(9)
                .U32(3).U8(5).U16(0x0102)
                .U8(0x09)
                .U32(5).Utf8("hello")
                .U64(0x0102030405060708UL);
            SomeIpBytes tlv = new();
            tlv.U16(0x0001).U8(0x7F).U16(0x1002).U16(0xBEEF).U16(0x5003).U8(2).Utf8("hi");
            SomeIpBytes union = new SomeIpBytes().U32(4 + 2).U32(1).U16(0x1234);
            byte[] eventBits = new SomeIpBytes().U16(0x8001).ToArray();

            await _AssertLeaves(s, profile, profileDirectory, UdpFrame(sequential.ToArray(), 1, 0x00), "seq.req").ConfigureAwait(false);
            await _AssertLeaves(s, profile, profileDirectory, UdpFrame(tlv.ToArray(), 2, 0x80), "tlv.resp").ConfigureAwait(false);
            await _AssertLeaves(s, profile, profileDirectory, UdpFrame(union.ToArray(), 3, 0x80), "uni.resp").ConfigureAwait(false);
            await _AssertLeaves(s, profile, profileDirectory, UdpFrame(eventBits, 4, 0x02), ".Ev.").ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static SomeIpCompiledModel _Compile(string json)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        _ = SomeIpRegistration.TryLoadConfig(stream, out SomeIpConfig? config, out _);
        return SomeIpConfigCompiler.Compile(config!, "", new List<SettingsLoadWarning>());
    }

    /// <summary>Compares every leaf the parser decoded under <paramref name="prefix"/> with the value the tool reports for its filter.</summary>
    private static async Task _AssertLeaves(
        SomeIpTestStack s, TsharkSomeIpUatProfile profile, string profileDirectory, byte[] frame, string prefix)
    {
        Packet packet = s.Parse(frame);
        int compared = 0;
        foreach (string name in _LeafNames(s, packet, profile, prefix))
        {
            // A string: the tool names the value after the parameter, so the decoded text is compared with the tail of its display
            // string. An enumeration: the tool shows "2 (Active)" where the parser shows "Active (2)"; the name has to appear in both.
            List<Field> own = FieldsOf(s, packet, name);
            if (own.Count == 1 && own[0].Value.Data.TryGetAsString(out string? text))
            {
                PdmlField? shown = TsharkVerifier.GetPdmlField(frame, profile.FilterOf(name), 1, _UdpDecodeAs, profileDirectory);
                await Assert.That(shown).IsNotNull().Because(Invariant($"the tool reports '{profile.FilterOf(name)}' for '{name}'"));
                await Assert.That(shown!.Value.ShowName!.EndsWith(": " + text, StringComparison.Ordinal)).IsTrue()
                    .Because(Invariant($"'{name}': parser '{text}' vs tool '{shown.Value.ShowName}'"));
                compared++;
                continue;
            }

            if (own.Count == 1
                && own[0].Value.Data.TryGetAsU64(out _)
                && own[0].TryGetCustomText(out LazyString named)
                && !named.IsNull
                && ((string)named).Contains(" (", StringComparison.Ordinal))
            {
                string enumName = ((string)named)[..((string)named).IndexOf(" (", StringComparison.Ordinal)];
                PdmlField? shown = TsharkVerifier.GetPdmlField(frame, profile.FilterOf(name), 1, _UdpDecodeAs, profileDirectory);
                await Assert.That(shown!.Value.ShowName!).Contains("(" + enumName + ")").Because(Invariant($"the enumeration name of '{name}'"));
            }

            List<string?> values = [.. own.Select(static f => _Text(f))];
            if (values.Any(static t => t is null))
            {
                continue;
            }

            string? tool = TsharkVerifier.GetFieldValue(frame, profile.FilterOf(name), 1, _UdpDecodeAs, profileDirectory);
            await Assert.That(tool).IsNotNull().Because(Invariant($"the tool reports '{profile.FilterOf(name)}' for '{name}'"));
            List<string?> theirs = [.. tool!.Split(',')];
            await Assert.That(TsharkEquivalence.AreEquivalentSet(values, theirs)).IsTrue()
                .Because(Invariant($"'{name}': parser [{string.Join(", ", values)}] vs tool [{tool}]"));
            compared++;
        }

        await Assert.That(compared).IsGreaterThan(0).Because(Invariant($"leaves of '{prefix}' compared"));
    }

    /// <summary>The registered names of the fields under <paramref name="prefix"/> that have a counterpart in the tool, in tree order.</summary>
    private static List<string> _LeafNames(SomeIpTestStack s, Packet packet, TsharkSomeIpUatProfile profile, string prefix)
    {
        List<string> names = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            string? name = s.Stack.GetField(field.FieldId)?.Name;
            if (name is not null
                && name.StartsWith(Prefix, StringComparison.Ordinal)
                && name.Contains(prefix, StringComparison.Ordinal)
                && profile.HasFilter(name)
                && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>The value of a parser leaf as text, or null for a container (no value).</summary>
    private static string? _Text(Field field)
    {
        FieldValueData data = field.Value.Data;
        if (data.TryGetAsU64(out ulong u))
        {
            return u.ToString(CultureInfo.InvariantCulture);
        }

        if (data.TryGetAsI64(out long i))
        {
            return i.ToString(CultureInfo.InvariantCulture);
        }

        if (data.TryGetAsF64(out double d))
        {
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        if (!data.TryGetAsBool(out bool b))
        {
            return null;
        }

        if (b)
        {
            return "1";
        }

        return "0";
    }

    #endregion

    #region Helpers

    private static (Stack Stack, Packet[] Packets) _ParseAll(byte[][] frames)
    {
#pragma warning disable CA2000 // The caller disposes the stack.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        Packet[] packets = new Packet[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            packets[i] = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromMillis(i));
        }

        return (stack, packets);
    }

    /// <summary>The tool prints ids as hex text with a 0x prefix; the parser stores numbers.</summary>
    private static bool _IsTrue(string? text) => text is "1" or "True" or "true";

    private static ulong _ParseHex(string text) =>
        ulong.Parse(text.Replace("0x", string.Empty, StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static ulong _U64(Stack stack, Packet packet, string name) =>
        _AllU64(packet, stack.GetFieldId(name)!.Value).First();

    private static bool _Bool(Stack stack, Packet packet, string name)
    {
        FieldId id = stack.GetFieldId(name)!.Value;
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id && field.Value.Data.TryGetAsBool(out bool value))
            {
                return value;
            }
        }

        return false;
    }

    private static List<ulong> _AllU64(Packet packet, FieldId id)
    {
        List<ulong> values = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id && field.Value.Data.TryGetAsU64(out ulong value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    #endregion
}
