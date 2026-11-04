// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The service node and the method node of a SOME/IP message: both carry the names of the description as their filter names, exist
/// for every message whose header resolves them (whatever happens to its payload), keep the decoded values lazy below the method
/// node, and leave <c>someip.payload</c> as the raw bytes of every payload nobody took. Also the refusal of a name that another
/// component holds, and the display texts that follow the UI names.
/// </summary>
internal sealed class SomeIpIdentityTests : SomeIpPayloadKit
{
    private const string _Method = "Svc.v1.settemp";
    private const string _Service = "Svc.v1";
    private const string _Zone = "Svc.v1.settemp.req.zone";

    private static string _Config(string extraMethods = "") =>
        Json(Join(Method(1, "settemp", Sites(Site("zone", "uint8")), "[]"), Method(2, "other", "[]", "[]"), extraMethods).TrimEnd(','));

    private static byte[] _Frame(ushort service, ushort method, byte[] payload, byte messageType = 0x00, byte interfaceVersion = 1) =>
        FrameBuilderTestExtensions.WrapRawUdp(
            SomeIpBytes.Message(service, method, payload, messageType: messageType, interfaceVersion: interfaceVersion), 40000, 30490);

    private static Field _NodeOf(SomeIpTestStack s, Packet packet, string name)
    {
        FieldId id = s.Stack.GetFieldId(name)!.Value;
        return packet.IterFieldsDfs(materialize: false).First(f => f.FieldId == id);
    }

    private static byte[] _SixteenBytes(byte value) => Enumerable.Repeat(value, 16).ToArray();

    #region Service node

    [Test]
    public async Task TheServiceNodeExistsForEveryMessageOfThatServiceVersionAndNoOther()
    {
        const string Json = """
            {"schema_version":"1.0","services":[
              {"service_id":62002,"major_version":2,"name":"ClimateControl","ui_name":"Climate Control","methods":[
                {"kind":"method","id":1,"name":"SetTemperature","in":[],"out":[]}],"data_types":[]},
              {"service_id":62002,"major_version":1,"name":"ClimateControl","methods":[
                {"kind":"method","id":1,"name":"SetTemperature","in":[],"out":[]}],"data_types":[]}]}
            """;
        using SomeIpTestStack s = SomeIpTestStack.Create(Json);

        Packet v2Known = s.Parse(_Frame(62002, 1, [], interfaceVersion: 2));
        Packet v2Unknown = s.Parse(_Frame(62002, 99, [1], interfaceVersion: 2));
        Packet v1 = s.Parse(_Frame(62002, 1, [], interfaceVersion: 1));
        Packet other = s.Parse(_Frame(0x9999, 1, [1], interfaceVersion: 2));
        Packet discovery = s.Parse(_Frame(0xFFFF, 0x8100, new byte[12], messageType: 0x02));

        await Assert.That(s.Count(v2Known, "ClimateControl.v2")).IsEqualTo(1);
        await Assert.That(s.Count(v2Unknown, "ClimateControl.v2")).IsEqualTo(1);
        await Assert.That(s.Count(v1, "ClimateControl.v2")).IsEqualTo(0);
        await Assert.That(s.Count(v1, "ClimateControl.v1")).IsEqualTo(1);
        await Assert.That(s.Count(other, "ClimateControl.v2")).IsEqualTo(0);
        await Assert.That(s.Count(other, "ClimateControl.v1")).IsEqualTo(0);
        await Assert.That(s.Count(discovery, "ClimateControl.v2")).IsEqualTo(0);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, v2Known, "ClimateControl.v2", "Climate Control");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, v1, "ClimateControl.v1", "ClimateControl");
    }

    #endregion

    #region Method node

    [Test]
    [Arguments((byte)0x00)]
    [Arguments((byte)0x80)]
    [Arguments((byte)0x81)]
    [Arguments((byte)0x01)]
    public async Task TheMethodNodeExistsWhateverTheMessageTypeAndHangsBelowTheServiceNode(byte messageType)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());

        Packet packet = s.Parse(UdpFrame([5], methodId: 1, messageType: messageType));
        Packet unknownMethod = s.Parse(UdpFrame([5], methodId: 99, messageType: messageType));

        Field service = _NodeOf(s, packet, _Service);
        await Assert.That(s.Count(packet, _Method)).IsEqualTo(1);
        await Assert.That(service.ChildCount(materialize: false)).IsEqualTo((ushort)1);
        await Assert.That(s.Count(packet, "Svc.v1.other")).IsEqualTo(0);
        await Assert.That(s.Count(unknownMethod, _Method)).IsEqualTo(0);
        await Assert.That(s.Count(unknownMethod, _Service)).IsEqualTo(1);
    }

    [Test]
    public async Task TheMethodNodeShowsInEveryConstellationOfThePayload()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());
        using SomeIpTestStack switchedOff = SomeIpTestStack.Create(_Config(), ("someip.decode_payload", SettingValue.Bool(false)));

        // (1) empty payload  (2) decoding off  (3) TP segment that delivers nothing  (4) TP segment that completes the message
        // (5) payload taken by a parser of the message id (own test below)  (6) error message.
        Packet empty = s.Parse(UdpFrame([], methodId: 1));
        Packet off = switchedOff.Parse(UdpFrame([5], methodId: 1));
        Packet tpGap = s.Parse(SomeIpFrames.TpFrame(16, _SixteenBytes(1), more: true, service: ServiceId, method: 1, messageType: 0x20, session: 7));
        _ = s.Parse(SomeIpFrames.TpFrame(0, _SixteenBytes(5), more: true, service: ServiceId, method: 1, messageType: 0x20, session: 8));
        Packet tpDone = s.Parse(SomeIpFrames.TpFrame(16, _SixteenBytes(6), more: false, service: ServiceId, method: 1, messageType: 0x20, session: 8));
        Packet error = s.Parse(UdpFrame([1, 2], methodId: 1, messageType: 0x81));

        await Assert.That(s.ByteLengths(empty, _Method)).IsEquivalentTo(One(0));
        await Assert.That(s.Count(empty, "someip.payload")).IsEqualTo(0);
        await Assert.That(s.Count(empty, "someip.payload.error")).IsEqualTo(1);

        await Assert.That(switchedOff.ByteLengths(off, _Method)).IsEquivalentTo(One(1));
        await Assert.That(_NodeOf(switchedOff, off, _Method).ChildCount(materialize: true)).IsEqualTo((ushort)0);
        await Assert.That(switchedOff.Count(off, "someip.payload.disabled")).IsEqualTo(1);

        await Assert.That(s.ByteLengths(tpGap, _Method)).IsEquivalentTo(One(0));
        await Assert.That(s.Count(tpGap, "someip.payload")).IsEqualTo(0);

        await Assert.That(s.ByteLengths(tpDone, _Method)).IsEquivalentTo(One(32));
        await Assert.That(s.U64s(tpDone, _Zone)).IsEquivalentTo(One(5UL));
        await Assert.That(s.ByteLengths(tpDone, "someip.payload")).IsEquivalentTo(One(32));

        await Assert.That(s.ByteLengths(error, _Method)).IsEquivalentTo(One(2));
        await Assert.That(s.Count(error, _Zone)).IsEqualTo(0);
        await Assert.That(s.ByteLengths(error, "someip.payload")).IsEquivalentTo(One(2));
    }

    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task ATpFlagWithoutRoomForTheTpHeaderKeepsTheMethodNodeAndTheRawBytes(int payloadLength)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());

        // Request with the TP flag (0x20) whose payload is shorter than the 4-byte TP header: malformed, never decoded as parameters.
        Packet packet = s.ParseIndexed(UdpFrame(new byte[payloadLength], methodId: 1, messageType: 0x20), out PacketIndex index);

        await Assert.That(s.Count(packet, _Service)).IsEqualTo(1);
        await Assert.That(s.ByteLengths(packet, _Method)).IsEquivalentTo(One(payloadLength));
        await Assert.That(_NodeOf(s, packet, _Method).ChildCount(materialize: true)).IsEqualTo((ushort)0);
        await Assert.That(s.Count(packet, "someip.tp.error.malformed")).IsEqualTo(1);
        await Assert.That(s.Count(packet, _Zone)).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip.payload")).IsEqualTo(payloadLength == 0 ? 0 : 1);
        await Assert.That(s.HasGroup(index, packet, "someip.payload.Svc.v1.settemp")).IsTrue();
        await s.AssertIndexExact(index, packet);
    }

    [Test]
    public async Task APayloadTakenByAMessageIdParserKeepsTheMethodNodeAndShowsNoRawPayload()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(
            _Config(),
            [],
            customize: static b => _ = b.RegisterProtocol<ConsumingProtocol>(new(), static (builder, id, _) =>
                builder.RegisterParserInU64TableByName(SomeIpProtocol.MessageIdTableName, ((ulong)ServiceId << 16) | 1UL, id)));

        Packet packet = s.Parse(UdpFrame([9, 9, 9], methodId: 1));

        await Assert.That(s.ByteLengths(packet, _Method)).IsEquivalentTo(One(3));
        await Assert.That(_NodeOf(s, packet, _Method).ChildCount(materialize: true)).IsEqualTo((ushort)0);
        await Assert.That(s.Count(packet, "someip.payload")).IsEqualTo(0);
        await Assert.That(s.Count(packet, _Zone)).IsEqualTo(0);
    }

    #endregion

    #region Index

    [Test]
    public async Task TheServiceAndMethodGroupsAreExactAndTheValueGroupHoldsOnlyDecodableRequests()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());
        string service = "someip.payload.Svc.v1";
        string method = service + ".settemp";
        string request = method + ".req";

        Packet decoded = s.ParseIndexed(UdpFrame([5], methodId: 1), out PacketIndex decodedIndex);
        Packet empty = s.ParseIndexed(UdpFrame([], methodId: 1), out PacketIndex emptyIndex);
        Packet error = s.ParseIndexed(UdpFrame([1], methodId: 1, messageType: 0x81), out PacketIndex errorIndex);
        Packet unconfigured = s.ParseIndexed(UdpFrame([1], methodId: 99), out PacketIndex unconfiguredIndex);

        await Assert.That(s.HasGroup(decodedIndex, decoded, service)).IsTrue();
        await Assert.That(s.HasGroup(decodedIndex, decoded, method)).IsTrue();
        await Assert.That(s.HasGroup(decodedIndex, decoded, request)).IsTrue();
        await Assert.That(s.HasGroup(emptyIndex, empty, method)).IsTrue();
        await Assert.That(s.HasGroup(emptyIndex, empty, request)).IsFalse();
        await Assert.That(s.HasGroup(errorIndex, error, method)).IsTrue();
        await Assert.That(s.HasGroup(errorIndex, error, request)).IsFalse();
        await Assert.That(s.HasGroup(unconfiguredIndex, unconfigured, service)).IsTrue();
        await Assert.That(s.HasGroup(unconfiguredIndex, unconfigured, method)).IsFalse();
        await s.AssertIndexExact(decodedIndex, decoded);
        await s.AssertIndexExact(emptyIndex, empty);
        await s.AssertIndexExact(errorIndex, error);
        await s.AssertIndexExact(unconfiguredIndex, unconfigured);
    }

    #endregion

    #region Replay

    [Test]
    public async Task ParallelReparsesGiveTheSameIdentityNodesAndRawPayloadAsTheFirstParse()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());
        byte[][] frames =
        [
            UdpFrame([5, 6, 7], methodId: 1),
            SomeIpFrames.TpFrame(0, _SixteenBytes(5), more: true, service: ServiceId, method: 1, messageType: 0x20, session: 8),
            SomeIpFrames.TpFrame(16, _SixteenBytes(6), more: false, service: ServiceId, method: 1, messageType: 0x20, session: 8),
        ];
        Packet[] first = [.. frames.Select(s.Parse)];
        foreach (Packet packet in first)
        {
            packet.MaterializeAll();
        }

        Task<Packet[]>[] workers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            Packet[] again = new Packet[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                again[i] = s.Reparse(frames[i], i);
                again[i].MaterializeAll();
            }

            return again;
        }))];
        Packet[][] replays = await Task.WhenAll(workers);

        foreach (Packet[] replay in replays)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], replay[i]);
            }
        }

        await Assert.That(s.ByteLengths(first[2], _Method)).IsEquivalentTo(One(32));
    }

    #endregion

    #region Raw payload

    [Test]
    public async Task SomeIpPayloadIsTheRawBytesOfEveryPayloadNobodyTookAlsoWhenDecoded()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_Config());

        Packet decoded = s.Parse(UdpFrame(new SomeIpBytes().U8(1).Fill(2, 6).ToArray(), methodId: 1));
        Packet unconfiguredMethod = s.Parse(UdpFrame([1, 2, 3], methodId: 99));
        Packet empty = s.Parse(UdpFrame([], methodId: 1));
        Packet discovery = s.Parse(_Frame(0xFFFF, 0x8100, new byte[12], messageType: 0x02));

        await Assert.That(s.ByteLengths(decoded, "someip.payload")).IsEquivalentTo(One(7));
        await Assert.That(s.Count(decoded, _Service)).IsEqualTo(1);
        await Assert.That(s.ByteLengths(unconfiguredMethod, "someip.payload")).IsEquivalentTo(One(3));
        await Assert.That(s.Count(unconfiguredMethod, _Service)).IsEqualTo(1);
        await Assert.That(s.Count(unconfiguredMethod, _Method)).IsEqualTo(0);
        await Assert.That(s.Count(empty, "someip.payload")).IsEqualTo(0);
        await Assert.That(s.Count(discovery, "someip.payload")).IsEqualTo(0);
    }

    #endregion

    #region Display

    [Test]
    public async Task TheDisplayTextsFollowTheUiNames()
    {
        const string Json = """
            {"schema_version":"1.0","services":[{"service_id":62002,"major_version":2,"name":"ClimateControl","ui_name":"Climate Control","methods":[
              {"kind":"method","id":1,"name":"SetTemperature","in":[],"out":[]}],"data_types":[]}]}
            """;
        using SomeIpTestStack s = SomeIpTestStack.Create(Json);

        Packet packet = s.Parse(_Frame(62002, 1, [], interfaceVersion: 2));

        await ProtocolTestHelper.AssertDisplayText(
            s.Stack, packet, "someip", "SOME/IP, Service: 0xF232 (Climate Control), Method: 0x0001 (SetTemperature), REQUEST");
        await ProtocolTestHelper.AssertStringField(s.Stack, packet, "someip.service.name", "Climate Control");
        await ProtocolTestHelper.AssertStringField(s.Stack, packet, "someip.method.name", "SetTemperature");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "ClimateControl.v2", "Climate Control");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "ClimateControl.v2.SetTemperature", "SetTemperature");
    }

    #endregion

    #region Names held by other components

    private static string _FullExample => File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "full_service.json"));

    [Test]
    public async Task AHeldMethodNameSkipsThatMemberAndRegistersTheOthers()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(
            _FullExample,
            [],
            customize: null,
            prepare: static b => b.RegisterField(b.RegisterProtocol(new ConsumingProtocol("other")), "ClimateControl.v2.SetTemperature", "Held", FieldType.None));

        Packet setTemperature = s.Parse(_Frame(62002, 1, [1, 2, 3], interfaceVersion: 2));
        Packet getStatus = s.Parse(_Frame(62002, 2, [], messageType: 0x80, interfaceVersion: 2));

        await Assert.That(s.Warnings.Count(static w => w.Message.Contains("ClimateControl.v2.SetTemperature", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(s.Warnings.Any(static w => w.Message.Contains("field", StringComparison.Ordinal))).IsTrue();
        await Assert.That(s.Stack.GetFieldId("ClimateControl.v2.SetTemperature.req.request")).IsNull();
        await ProtocolTestHelper.AssertDisplayText(s.Stack, setTemperature, "someip.methodid", "0x0001 (unknown)");
        await Assert.That(s.ByteLengths(setTemperature, "someip.payload")).IsEquivalentTo(One(3));
        await Assert.That(s.Count(setTemperature, "ClimateControl.v2")).IsEqualTo(1);
        await Assert.That(s.Count(setTemperature, "ClimateControl.v2.SetTemperature")).IsEqualTo(0);
        await Assert.That(s.Count(getStatus, "ClimateControl.v2.GetStatus")).IsEqualTo(1);
        await Assert.That(s.Stack.GetFieldId("ClimateControl.v2.GetStatus.resp.member_1")).IsNotNull();
    }

    [Test]
    public async Task AHeldServiceNameSkipsThatServiceAndRegistersTheOtherVersion()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(
            _FullExample,
            [],
            customize: null,
            prepare: static b => b.RegisterProtocol(new ConsumingProtocol("ClimateControl.v2")));

        Packet skipped = s.Parse(_Frame(62002, 1, [1, 2, 3], interfaceVersion: 2));
        Packet other = s.Parse(_Frame(62002, 1, [], interfaceVersion: 1));

        await Assert.That(s.Warnings.Count(static w => w.Message.Contains("ClimateControl.v2", StringComparison.Ordinal) && w.Message.Contains("protocol", StringComparison.Ordinal)))
            .IsEqualTo(1);
        await Assert.That(s.Stack.GetFieldId("ClimateControl.v2.SetTemperature")).IsNull();
        await Assert.That(s.ByteLengths(skipped, "someip.payload")).IsEquivalentTo(One(3));
        await Assert.That(s.Count(skipped, "someip.methodid")).IsEqualTo(1);
        await Assert.That(s.Stack.GetFieldId("ClimateControl.v1")).IsNotNull();
        await Assert.That(s.Count(other, "ClimateControl.v1")).IsEqualTo(1);
    }

    #endregion

    /// <summary>A protocol that takes whatever it is given; it holds a name, or consumes the payload of a message id.</summary>
    private sealed class ConsumingProtocol(string name = "test.consumer") : IProtocol
    {
        public string Name => name;

        public string UiName => "Consumer";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => data.Length;
    }
}
