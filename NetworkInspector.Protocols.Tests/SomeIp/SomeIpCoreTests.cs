// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using PacketIndex = NetworkInspector.Core.Index.PacketIndex;

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The message core of the SOME/IP parser: the header tree and its display texts, the names from the configuration, the length
/// rules and their diagnostics, several messages in one unit, and the identity of a re-parse. Every frame is a UDP datagram to
/// port 30490 built by hand from <see cref="SomeIpBytes"/>, so each test states the wire layout it checks.
/// </summary>
internal sealed class SomeIpCoreTests
{
    private const ushort _SomeIpPort = 30490;

    /// <summary>The size of the SOME/IP header in bytes.</summary>
    private const int _HeaderSize = 16;

    /// <summary>The full shipped example: service 0xF232 "ClimateControl" version 2, clients 1 and 2.</summary>
    private static string _FullExample => File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "full_service.json"));

    /// <summary>The minimal shipped example: service 62001 (0xF231) "VehicleSpeed" version 1.</summary>
    private static string _MinimalExample => File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "minimal_service.json"));

    private static byte[] _Udp(byte[] payload, ushort srcPort = 40000, ushort dstPort = _SomeIpPort) =>
        FrameBuilderTestExtensions.WrapRawUdp(payload, srcPort, dstPort);

    #region Header tree

    [Test]
    public async Task RequestShowsEveryHeaderFieldWithItsDisplayText()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0x1234, 0x0042, [1, 2, 3, 4], clientId: 1, sessionId: 0x10);

        Packet packet = s.Parse(_Udp(message));

        await Assert.That(s.U64s(packet, "someip.messageid")).IsEquivalentTo(_Single(0x12340042UL));
        await Assert.That(s.U64s(packet, "someip.serviceid")).IsEquivalentTo(_Single(0x1234UL));
        await Assert.That(s.U64s(packet, "someip.methodid")).IsEquivalentTo(_Single(0x0042UL));
        await Assert.That(s.U64s(packet, "someip.length")).IsEquivalentTo(_Single(12UL));
        await Assert.That(s.U64s(packet, "someip.clientid")).IsEquivalentTo(_Single(1UL));
        await Assert.That(s.U64s(packet, "someip.sessionid")).IsEquivalentTo(_Single(0x10UL));
        await Assert.That(s.U64s(packet, "someip.version")).IsEquivalentTo(_Single(1UL));
        await Assert.That(s.U64s(packet, "someip.ifversion")).IsEquivalentTo(_Single(1UL));
        await Assert.That(s.U64s(packet, "someip.msgtype")).IsEquivalentTo(_Single(0UL));
        await Assert.That(s.U64s(packet, "someip.returncode")).IsEquivalentTo(_Single(0UL));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.serviceid", "0x1234");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.messageid", "0x12340042");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.length", "12 (from client id)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.msgtype", "REQUEST (0x00)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.returncode", "E_OK (0x00)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip", "SOME/IP, Service: 0x1234, Method: 0x0042, REQUEST");
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Single(4));
    }

    [Test]
    public async Task EveryHeaderFieldSelectsOnlyItsOwnPacketOutOfThree()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet first = s.Parse(_Udp(SomeIpBytes.Message(0x1111, 0x0001)));
        Packet second = s.Parse(_Udp(SomeIpBytes.Message(0x2222, 0x0002, clientId: 7, sessionId: 9, messageType: 0x02)));
        Packet third = s.Parse(_Udp(SomeIpBytes.Message(0x3333, 0x0003, interfaceVersion: 5, returnCode: 0x01)));

        // A filter compares a field with a constant, so what has to hold is that each value occurs in exactly one packet.
        await Assert.That(s.U64s(first, "someip.serviceid")[0]).IsEqualTo(0x1111UL);
        await Assert.That(s.U64s(second, "someip.clientid")[0]).IsEqualTo(7UL);
        await Assert.That(s.U64s(second, "someip.sessionid")[0]).IsEqualTo(9UL);
        await Assert.That(s.U64s(second, "someip.msgtype")[0]).IsEqualTo(2UL);
        await Assert.That(s.U64s(third, "someip.ifversion")[0]).IsEqualTo(5UL);
        await Assert.That(s.U64s(third, "someip.returncode")[0]).IsEqualTo(1UL);
        await Assert.That(s.U64s(first, "someip.clientid")[0]).IsNotEqualTo(7UL);
    }

    #endregion

    #region Names

    [Test]
    public async Task ConfiguredNamesAppearAsFieldsAndInTheDisplayTexts()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0001, [0, 0], clientId: 1, interfaceVersion: 2)));

        await Assert.That(s.Strings(packet, "someip.service.name")).IsEquivalentTo(_Single("Climate Control"));
        await Assert.That(s.Strings(packet, "someip.method.name")).IsEquivalentTo(_Single("Set Temperature"));
        await Assert.That(s.Strings(packet, "someip.client.name")).IsEquivalentTo(_Single("CockpitECU"));
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.serviceid", "0xF232 (Climate Control)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.methodid", "0x0001 (Set Temperature)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.clientid", "0x0001 (CockpitECU)");
        await ProtocolTestHelper.AssertDisplayText(
            s.Stack, packet, "someip", "SOME/IP, Service: 0xF232 (Climate Control), Method: 0x0001 (Set Temperature), REQUEST");
    }

    [Test]
    public async Task AnUnknownServiceOfAConfiguredStackIsMarkedUnknownAndHasNoNames()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet packet = s.ParseIndexed(_Udp(SomeIpBytes.Message(0x2222, 0x0001, [9])), out PacketIndex index);

        await Assert.That(s.Count(packet, "someip.service.name")).IsEqualTo(0);
        await Assert.That(s.Count(packet, "someip.method.name")).IsEqualTo(0);
        await Assert.That(s.HasGroup(index, packet, "someip.name")).IsFalse();
        await Assert.That(s.HasGroup(index, packet, "someip.method.name")).IsFalse();
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.serviceid", "0x2222 (unknown)");
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Single(1));
    }

    [Test]
    public async Task AKnownServiceWithAnUnknownMethodMarksTheMethodUnknown()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0099, interfaceVersion: 2)));

        await Assert.That(s.Strings(packet, "someip.service.name")).IsEquivalentTo(_Single("Climate Control"));
        await Assert.That(s.Count(packet, "someip.method.name")).IsEqualTo(0);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.methodid", "0x0099 (unknown)");
    }

    [Test]
    public async Task AnOtherVersionOfAKnownServiceStillShowsTheServiceName()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);

        // The configuration defines versions 1 and 2 of the service; a message of interface version 3 has no definition, but the service
        // is still recognisably the same one.
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0001, interfaceVersion: 3)));

        await Assert.That(s.Strings(packet, "someip.service.name")).IsEquivalentTo(_Single("Climate Control"));
        await Assert.That(s.Count(packet, "someip.method.name")).IsEqualTo(0);
    }

    [Test]
    public async Task AClientWithoutAMappingShowsOnlyItsHexValue()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet mapped = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0001, clientId: 1, interfaceVersion: 2)));
        Packet unmapped = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0001, clientId: 9, interfaceVersion: 2)));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, mapped, "someip.clientid", "0x0001 (CockpitECU)");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, unmapped, "someip.clientid", "0x0009");
        await Assert.That(s.Count(unmapped, "someip.client.name")).IsEqualTo(0);
    }

    [Test]
    public async Task ANeverConfiguredStackShowsPlainHexWithoutUnknownMarkers()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0042)));

        await Assert.That(s.Warnings.Count).IsEqualTo(0);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.serviceid", "0x1234");
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.methodid", "0x0042");
    }

    [Test]
    public async Task ARuntimeMissOfAConfiguredStackIsNotALoadWarning()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_MinimalExample);

        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x2222, 0x0001)));

        await Assert.That(s.Warnings.Count).IsEqualTo(0);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.serviceid", "0x2222 (unknown)");
    }

    [Test]
    public async Task TwoStacksWithDifferentConfigurationsNeverMixTheirNames()
    {
        const string alpha = """{"schema_version":"1.0","services":[{"service_id":1,"major_version":1,"name":"Alpha","methods":[],"data_types":[]}]}""";
        const string beta = """{"schema_version":"1.0","services":[{"service_id":1,"major_version":1,"name":"Beta","methods":[],"data_types":[]}]}""";
        byte[] frame = _Udp(SomeIpBytes.Message(1, 1));

        using SomeIpTestStack a = SomeIpTestStack.Create(alpha);
        using SomeIpTestStack b = SomeIpTestStack.Create(beta);
        Packet inA = a.Parse(frame);
        Packet inB = b.Parse(frame);
        Packet inAAgain = a.Parse(frame);

        await Assert.That(a.Strings(inA, "someip.service.name")).IsEquivalentTo(_Single("Alpha"));
        await Assert.That(b.Strings(inB, "someip.service.name")).IsEquivalentTo(_Single("Beta"));
        await Assert.That(a.Strings(inAAgain, "someip.service.name")).IsEquivalentTo(_Single("Alpha"));
    }

    #endregion

    #region Message type and return code

    [Test]
    public async Task AnAcknowledgedSegmentedErrorShowsItsFlagsAndBaseType()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: 0xA1)));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.msgtype", "TP_ERROR (0xA1)");
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip.msgtype.tp", true);
        await ProtocolTestHelper.AssertBoolField(s.Stack, packet, "someip.msgtype.ack", false);
        await ProtocolTestHelper.AssertU64Field(s.Stack, packet, "someip.msgtype.base", 0x81);
        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.msgtype.base", "ERROR (0x81)");
    }

    [Test]
    [Arguments((byte)0x40, "REQUEST_ACK (0x40)", (byte)0x00)]
    [Arguments((byte)0x20, "TP_REQUEST (0x20)", (byte)0x00)]
    [Arguments((byte)0x22, "TP_NOTIFICATION (0x22)", (byte)0x02)]
    [Arguments((byte)0xC0, "RESPONSE_ACK (0xC0)", (byte)0x80)]
    [Arguments((byte)0x60, "TP_REQUEST_ACK (0x60)", (byte)0x00)]
    [Arguments((byte)0x03, "0x03", (byte)0x03)]
    public async Task MessageTypeNamesCoverFlagCombinations(byte messageType, string expectedText, byte expectedBase)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: messageType)));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.msgtype", expectedText);
        await ProtocolTestHelper.AssertU64Field(s.Stack, packet, "someip.msgtype.base", expectedBase);
    }

    [Test]
    public async Task TheBaseTypeIsTheSameForRequestAndSegmentedRequestButNotForANotification()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet request = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: 0x00)));
        Packet segmented = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: 0x20)));
        Packet notification = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, messageType: 0x02)));

        await Assert.That(s.U64s(request, "someip.msgtype.base")[0]).IsEqualTo(0UL);
        await Assert.That(s.U64s(segmented, "someip.msgtype.base")[0]).IsEqualTo(0UL);
        await Assert.That(s.U64s(notification, "someip.msgtype.base")[0]).IsEqualTo(2UL);
    }

    [Test]
    [Arguments((byte)0x03, "E_UNKNOWN_METHOD (0x03)")]
    [Arguments((byte)0x0F, "E_E2E_NO_NEW_DATA (0x0F)")]
    [Arguments((byte)0x7F, "0x7F")]
    public async Task ReturnCodesAreNamedAndAnUnnamedOneIsPlainHex(byte returnCode, string expectedText)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0001, returnCode: returnCode)));

        await ProtocolTestHelper.AssertDisplayText(s.Stack, packet, "someip.returncode", expectedText);
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, packet, "packet.error");
    }

    #endregion

    #region Length rules

    [Test]
    public async Task ALengthBelowTheMinimumConsumesOnlyTheHeaderAndTheNextMessageDecodes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] broken = SomeIpBytes.Message(0x1111, 0x0001, lengthOverride: 4);
        byte[] valid = SomeIpBytes.Message(0x2222, 0x0002, [1, 2]);

        Packet packet = s.Parse(_Udp(SomeIpBytes.Concat(broken, valid)));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(2);
        List<string> errors = s.Strings(packet, "someip.error.length_min");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("4");
        await Assert.That(errors[0]).Contains("8");
        await Assert.That(s.ByteLengths(packet, "someip")).IsEquivalentTo(_Items(16, 18));
        await Assert.That(s.U64s(packet, "someip.serviceid")).IsEquivalentTo(_Items(0x1111UL, 0x2222UL));
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Single(2));
    }

    [Test]
    [Arguments(64u)]
    [Arguments(0xFFFFFFFFu)]
    public async Task ALengthBeyondTheDataIsReportedAndConsumesWhatIsThere(uint declared)
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3, 4], lengthOverride: declared);

        Packet packet = s.Parse(_Udp(message));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
        List<string> errors = s.Strings(packet, "someip.error.truncated");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains(declared.ToString(CultureInfo.InvariantCulture));
        await Assert.That(errors[0]).Contains("only 20");
        await Assert.That(s.ByteLengths(packet, "someip")).IsEquivalentTo(_Single(20));
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, packet, "packet.unparsed_data");
    }

    [Test]
    public async Task TheLengthCountsFromTheClientIdSoLengthTwentyFourMeansThirtyTwoBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, new byte[16]);

        Packet exact = s.Parse(_Udp(message));
        Packet oneMore = s.Parse(_Udp(SomeIpBytes.Concat(message, [0xEE])));

        await ProtocolTestHelper.AssertU64Field(s.Stack, exact, "someip.length", 24);
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, exact, "packet.unparsed_data");

        // The extra byte is a trailing fragment of the unit (fewer than 16 bytes), shown under the message.
        await Assert.That(s.ByteLengths(oneMore, "someip.trailing")).IsEquivalentTo(_Single(1));
    }

    [Test]
    public async Task AMessageThatEndsTheDataStopsTheLoopAndAddsNoSecondUnit()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] valid = SomeIpBytes.Message(0x1111, 0x0001, [1]);
        byte[] cutOff = SomeIpBytes.Message(0x2222, 0x0002, new byte[2], lengthOverride: 200);
        byte[] datagram = SomeIpBytes.Concat(valid, cutOff);

        Packet packet = s.Parse(_Udp(datagram));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(2);
        await Assert.That(s.Count(packet, "someip.error.truncated")).IsEqualTo(1);
        await Assert.That(s.ByteLengths(packet, "someip").Sum()).IsEqualTo(datagram.Length);
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, packet, "packet.unparsed_data");
    }

    [Test]
    public async Task AnUnknownProtocolVersionIsReportedOnceAndAVersionOfOneIsNot()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet bad = s.ParseIndexed(_Udp(SomeIpBytes.Message(0x1234, 0x0001, protocolVersion: 2)), out PacketIndex badIndex);
        Packet good = s.ParseIndexed(_Udp(SomeIpBytes.Message(0x1234, 0x0001)), out PacketIndex goodIndex);

        List<string> errors = s.Strings(bad, "someip.error.version");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("2");
        await Assert.That(s.U64s(bad, "someip.version")).IsEquivalentTo(_Single(2UL));
        await Assert.That(s.HasGroup(badIndex, bad, "someip.error.version")).IsTrue();
        await Assert.That(s.Count(good, "someip.error.version")).IsEqualTo(0);
        await Assert.That(s.HasGroup(goodIndex, good, "someip.error.version")).IsFalse();
    }

    #endregion

    #region Several messages in one unit

    [Test]
    public async Task SeveralMessagesBecomeSiblingsAndTheInfoCountsThem()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] datagram = SomeIpBytes.Concat(
            SomeIpBytes.Message(0x1111, 0x0001),
            SomeIpBytes.Message(0x2222, 0x0002, new byte[4]),
            SomeIpBytes.Message(0x3333, 0x0003, new byte[9]));

        Packet packet = s.Parse(_Udp(datagram));
        Packet two = s.Parse(_Udp(SomeIpBytes.Concat(SomeIpBytes.Message(1, 1), SomeIpBytes.Message(2, 2))));
        Packet one = s.Parse(_Udp(SomeIpBytes.Message(0x1234, 0x0042)));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(3);
        await Assert.That(s.ByteLengths(packet, "someip.payload")).IsEquivalentTo(_Items(4, 9));
        await Assert.That(packet.Info).IsEqualTo("SOME/IP (3 messages)");
        await Assert.That(two.Info).IsEqualTo("SOME/IP (2 messages)");
        await Assert.That(one.Info).IsEqualTo("SOME/IP 0x1234 REQUEST");
    }

    [Test]
    public async Task APacketInfoNamesTheServiceWhenTheModelKnowsIt()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        Packet packet = s.Parse(_Udp(SomeIpBytes.Message(0xF232, 0x0001, interfaceVersion: 2)));

        await Assert.That(packet.Info).IsEqualTo("SOME/IP 0xF232 (Climate Control) REQUEST");
    }

    [Test]
    public async Task TheMessageLimitCutsTheUnitAndNamesTheSetting()
    {
        const int limit = 10;
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.max_messages_per_unit", SettingValue.U64(limit)));
        Packet cut = s.Parse(_Udp(_Messages(15)));
        Packet exact = s.Parse(_Udp(_Messages(limit)));

        await Assert.That(s.Count(cut, "someip")).IsEqualTo(limit);
        List<string> errors = s.Strings(cut, "someip.error.unit_limit");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("someip.max_messages_per_unit");
        await Assert.That(errors[0]).Contains("10");

        // The five messages behind the limit are shown as raw trailing bytes (80 bytes), not dropped and not left unparsed.
        byte[] all = _Messages(15);
        await Assert.That(s.ByteLengths(cut, "someip.trailing")).IsEquivalentTo(_Single(all.Length - (limit * _HeaderSize)));
        await ProtocolTestHelper.AssertFieldNotPresent(s.Stack, cut, "packet.unparsed_data");

        // A unit of exactly the limit raises nothing.
        await Assert.That(s.Count(exact, "someip")).IsEqualTo(limit);
        await Assert.That(s.Count(exact, "someip.error.unit_limit")).IsEqualTo(0);
    }

    [Test]
    public async Task ALimitRaisedAboveTheMessageCountDecodesEveryMessage()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.max_messages_per_unit", SettingValue.U64(20)));
        Packet packet = s.Parse(_Udp(_Messages(15)));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(15);
        await Assert.That(s.Count(packet, "someip.error.unit_limit")).IsEqualTo(0);
    }

    [Test]
    public async Task ALimitOfOneDecodesOnlyTheFirstMessage()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(null, ("someip.max_messages_per_unit", SettingValue.U64(1)));
        Packet packet = s.Parse(_Udp(_Messages(3)));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
        await Assert.That(s.Count(packet, "someip.error.unit_limit")).IsEqualTo(1);
    }

    private static byte[] _Messages(int count)
    {
        byte[][] parts = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            parts[i] = SomeIpBytes.Message(0x1000, (ushort)i);
        }

        return SomeIpBytes.Concat(parts);
    }

    #endregion

    #region Bytes that are not a message

    [Test]
    public async Task BytesBehindTheLastMessageAreATrailingFragmentAndASecondHeaderOfZerosIsAUnit()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] message = SomeIpBytes.Message(0x1234, 0x0001, [1]);

        Packet trailing = s.Parse(_Udp(SomeIpBytes.Concat(message, [1, 2, 3, 4, 5])));
        Packet zeros = s.Parse(_Udp(SomeIpBytes.Concat(message, new byte[16])));

        await Assert.That(s.ByteLengths(trailing, "someip.trailing")).IsEquivalentTo(_Single(5));
        await Assert.That(s.Count(trailing, "someip.error.trailing")).IsEqualTo(1);
        await Assert.That(s.Count(trailing, "someip")).IsEqualTo(1);

        await Assert.That(s.Count(zeros, "someip")).IsEqualTo(2);
        await Assert.That(s.Count(zeros, "someip.error.length_min")).IsEqualTo(1);
        await Assert.That(s.Count(zeros, "someip.trailing")).IsEqualTo(0);
    }

    [Test]
    public async Task ADatagramShorterThanAHeaderShowsAnIncompleteHeaderAndItsBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp([1, 2, 3, 4, 5, 6, 7, 8]));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(1);
        List<string> errors = s.Strings(packet, "someip.error.incomplete");
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0]).Contains("8 of 16");
        await Assert.That(s.ByteLengths(packet, "someip.trailing")).IsEquivalentTo(_Single(8));
        await Assert.That(packet.Info).IsEqualTo("SOME/IP (incomplete header)");
    }

    [Test]
    public async Task AnEmptyDatagramProducesNoSomeIpContainer()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        Packet packet = s.Parse(_Udp([]));

        await Assert.That(s.Count(packet, "someip")).IsEqualTo(0);
    }

    [Test]
    public async Task EveryInputIsFullyAccountedForByAContainerTheUnparsedDataOrTheTrailingBytes()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[][] inputs =
        [
            SomeIpBytes.Message(0x1234, 0x0001, [1, 2, 3]),
            [1, 2, 3, 4, 5, 6, 7, 8],
            SomeIpBytes.Message(0x1234, 0x0001, lengthOverride: 4),
            SomeIpBytes.Message(0x2222, 0x0001, [1, 2], lengthOverride: 500),
        ];

        foreach (byte[] input in inputs)
        {
            Packet packet = s.Parse(_Udp(input));

            // Either the full header is there or a named diagnostic says why not.
            bool hasHeader = s.Count(packet, "someip.messageid") == 1;
            bool hasDiagnostic = s.Count(packet, "someip.error.incomplete") + s.Count(packet, "someip.error.length_min")
                + s.Count(packet, "someip.error.truncated") == 1;
            await Assert.That(hasHeader || hasDiagnostic).IsTrue();

            // Consumed bytes (the containers; an incomplete header's container holds the bytes that are also its trailing field)
            // plus the unparsed data plus the trailing bytes of a complete header must equal the datagram.
            int accounted = s.ByteLengths(packet, "someip").Sum();
            if (s.Count(packet, "someip.error.incomplete") == 0)
            {
                accounted += s.ByteLengths(packet, "someip.trailing").Sum();
            }

            accounted += s.ByteLengths(packet, "packet.unparsed_data").Sum();
            await Assert.That(accounted).IsEqualTo(input.Length);
        }
    }

    #endregion

    #region Identity of a re-parse and allocation

    [Test]
    public async Task AReparseOfEveryKindOfUnitEqualsTheFirstParse()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        byte[][] frames =
        [
            _Udp(SomeIpBytes.Concat(
                SomeIpBytes.Message(0xF232, 0x0001, [1], clientId: 1, interfaceVersion: 2),
                SomeIpBytes.Message(0x1111, 0x0002, new byte[4]),
                SomeIpBytes.Message(0x3333, 0x0003, new byte[2], lengthOverride: 40))),
            _Udp(SomeIpBytes.Concat(SomeIpBytes.Message(0xF232, 0x0002, interfaceVersion: 2), [1, 2, 3])),
            _Udp(SomeIpBytes.Message(0x1234, 0x0001, lengthOverride: 3)),
        ];

        Packet[] first = [.. frames.Select(s.Parse)];
        for (int i = 0; i < frames.Length; i++)
        {
            Packet reparse = s.Reparse(frames[i], i);
            await PacketFieldComparer.AssertFieldIdentical(s.Stack, first[i], reparse);
        }
    }

    [Test]
    public async Task ConcurrentReparsesAllEqualTheFirstParse()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create(_FullExample);
        byte[] frame = _Udp(SomeIpBytes.Concat(
            SomeIpBytes.Message(0xF232, 0x0001, [1, 2], clientId: 1, interfaceVersion: 2),
            SomeIpBytes.Message(0x1111, 0x0002, new byte[4])));
        Packet first = s.Parse(frame);
        PacketFieldComparer.PacketFieldSnapshot snapshot = PacketFieldComparer.CaptureFields(first);

        Task[] workers = new Task[4];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = Task.Run(async () =>
            {
                for (int round = 0; round < 25; round++)
                {
                    Packet reparse = s.Reparse(frame, 0);
                    await PacketFieldComparer.AssertMatchesSnapshot(s.Stack, snapshot, reparse).ConfigureAwait(false);
                }
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    [Test]
    public async Task AParseThatBuildsNoTreeAddsAtMostOneSmallBoxOverTheSameDatagramWithoutSomeIp()
    {
        using SomeIpTestStack s = SomeIpTestStack.Create();
        byte[] someIp = _Udp(SomeIpBytes.Message(0x1234, 0x0042, [1, 2, 3, 4]));
        byte[] plain = _Udp(new byte[_HeaderSize + 4], dstPort: 40001);

        long withSomeIp = _AllocatedPerSkipParse(s, someIp);
        long withoutSomeIp = _AllocatedPerSkipParse(s, plain);

        // The datagram without SOME/IP carries the same number of bytes through the same lower layers; what remains is the
        // parser itself, which may allocate the state box of the lazy packet info and nothing else.
        long extra = withSomeIp - withoutSomeIp;
        await Assert.That(extra).IsLessThanOrEqualTo(64).Because($"SOME/IP parse allocated {extra} bytes more than the same datagram without it");
    }

    private static long _AllocatedPerSkipParse(SomeIpTestStack s, byte[] frame)
    {
        const int warmup = 100;
        const int rounds = 1000;
        PacketIndex index = new(s.Stack);
        ParseOptions options = new(index: index, fieldTree: FieldTreeMode.Skip);
        Frame parsed = Frame.Create(
            0, Timestamp.FromSecs(0), frame, LinkType.Ethernet, FrameInterfaceId.Invalid, s.Stack.FrameInterfaceRegistry).Value;

        for (int i = 0; i < warmup; i++)
        {
            _ = Packet.TryParse(0, s.Stack, parsed, in options, out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rounds; i++)
        {
            _ = Packet.TryParse(0, s.Stack, parsed, in options, out _, out _);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
    }

    #endregion

    private static T[] _Single<T>(T value) => [value];

    private static T[] _Items<T>(params T[] values) => values;
}
