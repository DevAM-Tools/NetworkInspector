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
                    { "name": "target_c", "filter_name": "climate.settemp.target_c", "type_reference": "int16" },
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
        SettingsManager manager = new(directory);
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
    public async Task ADerivedNameAndAConfiguredFilterNameSelectTheRequestAndOtherValuesDoNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N"));
        using Stack stack = _Build(directory);
        try
        {
            // Structure with a 4-byte length: target_c = 21, zone = 7.
            byte[] frame = _Message(1, 0x00, [0, 0, 0, 3, 0, 21, 7]);
            Packet packet = FilterTestHelper.Parse(stack, frame);

            Filter byFilterName = FilterTestHelper.CompileOrThrow("climate.settemp.target_c == 21", stack);
            Filter byDerivedName = FilterTestHelper.CompileOrThrow(
                "someip.payload.climatecontrol.v2.settemperature.in.request.zone == 7", stack);
            Filter wrongValue = FilterTestHelper.CompileOrThrow("climate.settemp.target_c == 22", stack);

            await Assert.That(FilterTestHelper.MatchOrThrow(byFilterName, packet)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(byDerivedName, packet)).IsTrue();
            await Assert.That(FilterTestHelper.MatchOrThrow(wrongValue, packet)).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AFilterOnATlvMemberSelectsOnlyTheMessageThatCarriesItAndOpensNoOtherPayload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ni-someip-filter-" + Guid.NewGuid().ToString("N"));
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
            IndexGroupId group = stack.GetIndexGroupId("someip.payload.climatecontrol.v2.settemperature.in")!.Value;
            IndexGroupId outGroup = stack.GetIndexGroupId("someip.payload.climatecontrol.v2.settemperature.out")!.Value;
            await Assert.That(index.GetGroupBitmap(group).Contains(0)).IsFalse();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(0)).IsTrue();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(1)).IsTrue();
            await Assert.That(index.GetGroupBitmap(outGroup).Contains(2)).IsFalse();

            Filter filter = FilterTestHelper.CompileOrThrow("someip.payload.climatecontrol.v2.settemperature.out.current_c == 25", stack);

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
