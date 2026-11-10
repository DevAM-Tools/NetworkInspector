// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>
/// Filters on SOME/IP payload parameters: the registered names a service description produces select the right messages through
/// the real filter engine, the lazy payload container is opened by the filter itself and by nothing else, and the index group of a
/// member direction narrows the candidates without ever excluding a message that holds the parameter.
/// </summary>
internal sealed class SomeIpPayloadFilterTests
{
    private const string _Config =
        """
        {
          "schema_version": "1.0",
          "services": [
            {
              "service_id": 62002,
              "major_version": 2,
              "name": "ClimateControl",
              "methods": [
                {
                  "kind": "method", "id": 1, "name": "SetTemperature",
                  "in": [{ "name": "request", "type_reference": "temperature_request" }],
                  "out": {
                    "1": { "name": "current_c", "type_reference": "int16" },
                    "3": { "name": "fan_state", "type_reference": "uint8" }
                  }
                }
              ],
              "data_types": [
                {
                  "category": "structure", "name": "temperature_request",
                  "members": [
                    { "name": "target_c", "type_reference": "int16" },
                    { "name": "zone", "type_reference": "uint8" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static Stack _Build(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "someip.json"), _Config);
#pragma warning disable CA2000 // Ownership of the settings manager transfers to the stack.
        SettingsManager manager = new(directory, CoreSettingsLayout.Instance);
#pragma warning restore CA2000
        manager.PreloadValue("someip.config_file", "someip.json");
        StackBuilder builder = new(manager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static byte[] _Message(ushort method, byte messageType, byte[] payload)
    {
        // Header: service 62002 (0xF232), method, length = 8 + payload, client 1, session 1, protocol 1, interface 2, type, E_OK.
        int length = 8 + payload.Length;
        byte[] header =
        [
            0xF2, 0x32, (byte)(method >> 8), (byte)method,
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            0x00, 0x01, 0x00, 0x01, 0x01, 0x02, messageType, 0x00,
        ];
        return FilterTestHelper.BuildUdpFrame(40000, 30490, payload: [.. header, .. payload]);
    }

    [Test]
    public async Task TheNamesOfTheDescriptionSelectTheRequestAndOtherValuesDoNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using Stack stack = _Build(directory);
        try
        {
            // Structure with a 4-byte length: target_c = 21, zone = 7.
            byte[] frame = _Message(1, 0x00, [0, 0, 0, 3, 0, 21, 7]);
            Packet packet = FilterTestHelper.Parse(stack, frame);

            Filter target = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature.req.request.target_c == 21", stack);
            Filter zone = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature.req.request.zone == 7", stack);
            Filter wrongValue = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature.req.request.target_c == 22", stack);
            FilterResult<Filter> lowerCase = Filter.Compile("climatecontrol.v2.settemperature.req.request.zone == 7", stack);

            await Assert.That(FilterTestHelper.MatchOrThrow(target, packet)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(zone, packet)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(wrongValue, packet)).IsFalse();

            // The name is the name of the description: a lower-cased variant is an unknown field.
            await Assert.That(lowerCase.TryGetValue(out Filter? _)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AServiceAndAMethodNameSelectTheirMessagesAndCombineWithTheMessageType()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using Stack stack = _Build(directory);
        try
        {
            Packet request = FilterTestHelper.Parse(stack, _Message(1, 0x00, [0, 0, 0, 3, 0, 21, 7]), packetId: 0);
            Packet response = FilterTestHelper.Parse(stack, _Message(1, 0x80, [0x00, 0x03, 2]), packetId: 1);
            Packet error = FilterTestHelper.Parse(stack, _Message(1, 0x81, [1]), packetId: 2);
            Packet noReturn = FilterTestHelper.Parse(stack, _Message(1, 0x01, []), packetId: 3);
            Packet otherMethod = FilterTestHelper.Parse(stack, _Message(99, 0x00, [1]), packetId: 4);

            Filter service = FilterTestHelper.CompileOrThrow("ClimateControl.v2", stack);
            Filter method = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature", stack);
            Filter responses = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature && someip.msgtype == 0x80", stack);
            Filter missing = FilterTestHelper.CompileOrThrow("len(ClimateControl.v2.SetTemperature) == 0", stack);

            // The service filter selects every message of the service version, also the method the description does not define.
            await Assert.That(new[] { request, response, error, noReturn, otherMethod }.Count(p => FilterTestHelper.MatchOrThrow(service, p))).IsEqualTo(5);
            await Assert.That(new[] { request, response, error, noReturn }.All(p => FilterTestHelper.MatchOrThrow(method, p))).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(method, otherMethod)).IsFalse();
            await Assert.That(FilterTestHelper.MatchOrThrow(responses, response)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(responses, request)).IsFalse();
            await Assert.That(FilterTestHelper.MatchOrThrow(missing, noReturn)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(missing, request)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AFilterOnServiceOrMethodLeavesThePayloadUnmaterializedAndAValueFilterOpensIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using Stack stack = _Build(directory);
        try
        {
            Packet packet = FilterTestHelper.Parse(stack, _Message(1, 0x00, [0, 0, 0, 3, 0, 21, 7]));
            Filter service = FilterTestHelper.CompileOrThrow("ClimateControl.v2", stack);
            Filter method = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature", stack);
            Filter value = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature.req.request.zone == 7", stack);

            bool serviceMatched = FilterTestHelper.MatchOrThrow(service, packet);
            bool lazyAfterService = packet.HasUnpopulatedLazyFields;
            bool methodMatched = FilterTestHelper.MatchOrThrow(method, packet);
            bool lazyAfterMethod = packet.HasUnpopulatedLazyFields;
            bool valueMatched = FilterTestHelper.MatchOrThrow(value, packet);

            await Assert.That(serviceMatched).IsTrue();
            await Assert.That(lazyAfterService).IsTrue();
            await Assert.That(methodMatched).IsTrue();
            await Assert.That(lazyAfterMethod).IsTrue();
            await Assert.That(valueMatched).IsTrue();
            FieldId zone = FilterTestHelper.FieldIdOf(stack, "ClimateControl.v2.SetTemperature.req.request.zone");
            await Assert.That(packet.IterFieldsDfs(materialize: false).Count(f => f.FieldId == zone)).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TheFilterInputCompletesServiceMethodAndElementNames()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using Stack stack = _Build(directory);
        try
        {
            static List<string> Complete(string marked, Stack stack)
            {
                int caret = marked.IndexOf('|', StringComparison.Ordinal);
                return [.. Filter.GetCompletions(marked.Remove(caret, 1), caret, stack).Items.Select(static item => item.Text)];
            }

            await Assert.That(Complete("ClimateControl.v2.Set|", stack)).Contains("ClimateControl.v2.SetTemperature");
            await Assert.That(Complete("ClimateControl.v|", stack)).Contains("ClimateControl.v2");
            await Assert.That(Complete("ClimateControl.v2.SetTemperature.req.re|", stack)).Contains("ClimateControl.v2.SetTemperature.req.request");
            FilterCompletions kinds = Filter.GetCompletions("ClimateControl.v2.Set", 21, stack);
            await Assert.That(kinds.Items.First(static item => item.Text == "ClimateControl.v2.SetTemperature").Kind).IsEqualTo(FilterCompletionKind.Field);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AFilterOnATlvMemberSelectsOnlyTheMessageThatCarriesItAndOpensNoOtherPayload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using Stack stack = _Build(directory);
        try
        {
            // A: members 1 (= 25) and 3 (= 2). B: member 3 only. C: a method the description does not define.
            byte[] a = _Message(1, 0x80, [0x10, 0x01, 0x00, 25, 0x00, 0x03, 2]);
            byte[] b = _Message(1, 0x80, [0x00, 0x03, 2]);
            byte[] c = _Message(99, 0x80, [0x10, 0x01, 0x00, 25]);
            PacketIndex index = new(stack);
            Packet packetA = FilterTestHelper.ParseIndexed(stack, index, a, packetId: 0);
            Packet packetB = FilterTestHelper.ParseIndexed(stack, index, b, packetId: 1);
            Packet packetC = FilterTestHelper.ParseIndexed(stack, index, c, packetId: 2);

            // The member direction group is a superset: A and B hold a message of the member, C does not.
            IndexGroupId group = stack.GetIndexGroupId("someip.payload.ClimateControl.v2.SetTemperature.req")!.Value;
            IndexGroupId outGroup = stack.GetIndexGroupId("someip.payload.ClimateControl.v2.SetTemperature.resp")!.Value;
            await Assert.That(index.GetGroupBitmap(group).Contains(0)).IsFalse();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(0)).IsTrue();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(1)).IsTrue();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(2)).IsFalse();

            Filter filter = FilterTestHelper.CompileOrThrow("ClimateControl.v2.SetTemperature.resp.current_c == 25", stack);

            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packetA)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packetB)).IsFalse();
            await Assert.That(FilterTestHelper.MatchOrThrow(filter, packetC)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
