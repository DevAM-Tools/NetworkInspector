// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core.Protocols;

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for CAN protocol parsing (ISO 11898, SocketCAN format).
/// Verifies field extraction for classic CAN and CAN FD frames, and dispatch via
/// <c>can.id</c> and <c>can.extended_id</c> tables.
/// </summary>
internal sealed class CanProtocolTests
{
    /// <summary>
    /// Builds a stack and parses a SocketCAN frame (link type 227).
    /// </summary>
    private static (Stack Stack, Packet Packet) BuildAndParse(byte[] frameData)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            LinkType.CanSocketcan,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    [Test]
    public async Task Parse_ClassicCan_StandardId_FieldsCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateCanFrame(canId: 0x123);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? canIdField = stack.GetFieldId("can.id");
            await Assert.That(canIdField).IsNotNull();

            bool has = packet.TryGetFieldValue(canIdField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x123UL);
        }
    }

    [Test]
    public async Task Parse_ClassicCan_ExtendedId_FlagSet()
    {
        byte[] frameData = FrameBuilders.GenerateCanFrame(
            canId: 0x1ABCDEF, isExtended: true);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            // Extended Frame Format flag should be set
            FieldId? xtdField = stack.GetFieldId("can.flags.xtd");
            await Assert.That(xtdField).IsNotNull();
            bool hasXtd = packet.TryGetFieldValue(xtdField!.Value, out FieldValue xtdValue);
            await Assert.That(hasXtd).IsTrue();
            xtdValue.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // Verify the CAN ID is extended (29-bit)
            FieldId? canIdField = stack.GetFieldId("can.id");
            bool hasId = packet.TryGetFieldValue(canIdField!.Value, out FieldValue idValue);
            await Assert.That(hasId).IsTrue();
            idValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x1ABCDEFUL);
        }
    }

    [Test]
    public async Task Parse_ClassicCan_RtrFlag()
    {
        byte[] frameData = FrameBuilders.GenerateCanFrame(
            canId: 0x100, isRtr: true, payload: []);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? rtrField = stack.GetFieldId("can.flags.rtr");
            await Assert.That(rtrField).IsNotNull();
            bool has = packet.TryGetFieldValue(rtrField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ClassicCan_Dlc_MatchesPayload()
    {
        byte[] payload = [0x11, 0x22, 0x33];
        byte[] frameData = FrameBuilders.GenerateCanFrame(
            canId: 0x200, payload: payload);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? dlcField = stack.GetFieldId("can.len");
            await Assert.That(dlcField).IsNotNull();
            bool has = packet.TryGetFieldValue(dlcField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(3UL);
        }
    }

    [Test]
    public async Task Parse_ClassicCan_DataPayload()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        byte[] frameData = FrameBuilders.GenerateCanFrame(
            canId: 0x300, payload: payload);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? dataField = stack.GetFieldId("can.data");
            await Assert.That(dataField).IsNotNull();
            bool has = packet.TryGetFieldValue(dataField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();

            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data);
            await Assert.That(data.Length).IsEqualTo(4);
            await Assert.That(data.Span[0]).IsEqualTo((byte)0xDE);
            await Assert.That(data.Span[3]).IsEqualTo((byte)0xEF);
        }
    }

    [Test]
    public async Task Parse_CanFd_FdfFlag()
    {
        byte[] frameData = FrameBuilders.GenerateCanFdFrame(
            canId: 0x1ABCDEF);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? fdfField = stack.GetFieldId("can.flags.fd");
            await Assert.That(fdfField).IsNotNull();
            bool has = packet.TryGetFieldValue(fdfField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanFd_BrsFlag()
    {
        byte[] frameData = FrameBuilders.GenerateCanFdFrame(
            canId: 0x100, brs: true);

        (Stack stack, Packet packet) = BuildAndParse(frameData);
        using (stack)
        {
            FieldId? brsField = stack.GetFieldId("can.flags.brs");
            await Assert.That(brsField).IsNotNull();
            bool has = packet.TryGetFieldValue(brsField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ShortData_NoCanFields()
    {
        // Only 4 bytes — not enough for SocketCAN header
        byte[] shortFrame = [0x23, 0x01, 0x00, 0x00];

        (Stack stack, Packet packet) = BuildAndParse(shortFrame);
        using (stack)
        {
            // CAN ID field should not be present with too-short data
            FieldId? canIdField = stack.GetFieldId("can.id");
            await Assert.That(canIdField).IsNotNull();
            bool has = packet.TryGetFieldValue(canIdField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_ClassicCan_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateCanFrame(canId: 0x456);

        using SettingsManager settingsManager = new();

        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();
        using (stack)
        {
            NetworkInspector.Core.Index.PacketIndex index = new(stack);

            Frame frame = Frame.Create(
                new FrameId(0),
                Timestamp.FromSecs(0),
                frameData,
                LinkType.CanSocketcan,
                FrameInterfaceId.Invalid,
                stack.FrameInterfaceRegistry).Value;

            Packet.ParseFrameIndexed(new PacketId(0), stack, frame, index);

            // CAN protocol should be in the index
            ProtocolId? canProtocolId = stack.GetProtocolId("can");
            await Assert.That(canProtocolId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(canProtocolId!.Value).Contains(0)).IsTrue();

            // CAN ID field should be in the index
            FieldId? canIdField = stack.GetFieldId("can.id");
            await Assert.That(canIdField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(canIdField!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_ClassicCanExtended_DispatchesViaCanExtendedIdTable()
    {
        // A classic CAN extended frame (29-bit ID) must dispatch via can.extended_id in addition
        // to can.id, so protocols registered exclusively at can.extended_id are also invoked.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        FlagProtocol probe = new("probe.canext");
        ProtocolId probeId = builder.RegisterProtocol(probe);
        builder.RegisterParserInU64TableByName(
            NetworkInspector.Protocols.CanProtocol.ExtendedIdTableName, 0x1ABCDEF, probeId);
        Stack stack = builder.Build();

        byte[] frameData = FrameBuilders.GenerateCanFrame(canId: 0x1ABCDEF, isExtended: true,
            payload: [0xDE, 0xAD, 0xBE, 0xEF]);
        Frame frame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frameData,
            LinkType.CanSocketcan, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet.ParseFrame(new PacketId(0), stack, frame);

        using (stack)
        {
            await Assert.That(probe.WasCalled).IsTrue();
        }
    }

    [Test]
    public async Task Parse_MultipleProtocolsAtSameCanIdKey_PacketChoiceFieldHasChoicePrefix()
    {
        // When two protocols are registered at the same can.id key, MutField.DispatchMultipleProtocols
        // creates a packet.choice container whose CustomText must start with "Choice: can.id: ".
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        FlagProtocol probe1 = new("probe.choice1");
        FlagProtocol probe2 = new("probe.choice2");
        ProtocolId id1 = builder.RegisterProtocol(probe1);
        ProtocolId id2 = builder.RegisterProtocol(probe2);
        builder.RegisterParserInU64TableByName(NetworkInspector.Protocols.CanProtocol.IdTableName, 0x100, id1);
        builder.RegisterParserInU64TableByName(NetworkInspector.Protocols.CanProtocol.IdTableName, 0x100, id2);
        Stack stack = builder.Build();

        byte[] frameData = FrameBuilders.GenerateCanFrame(canId: 0x100, payload: [0x01, 0x02]);
        Frame frame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frameData,
            LinkType.CanSocketcan, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);

        using (stack)
        {
            // Both probes must have been called
            await Assert.That(probe1.WasCalled).IsTrue();
            await Assert.That(probe2.WasCalled).IsTrue();

            // Find the packet.choice field in the tree; its CustomText must start with "Choice: "
            FieldId? choiceFieldId = stack.GetFieldId("packet.choice");
            await Assert.That(choiceFieldId).IsNotNull();

            string? choiceLabel = FindCustomText(packet.RootField(), choiceFieldId!.Value);
            await Assert.That(choiceLabel).IsNotNull();
            // key=0x100=256; TryCallNextProtocolU64 uses key.ToString() for the keyDisplay
            await Assert.That(choiceLabel).IsEqualTo("Choice: can.id: 256");
        }
    }

    /// <summary>Depth-first search for the first field with <paramref name="fieldId"/>;
    /// returns its <c>CustomText.ToString()</c> or <see langword="null"/> if not found.</summary>
    private static string? FindCustomText(Field field, FieldId fieldId)
    {
        if (field.FieldId == fieldId && !field.CustomText.IsNull)
        {
            return field.CustomText.ToString();
        }

        foreach (Field child in field.Children())
        {
            string? result = FindCustomText(child, fieldId);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    /// <summary>A minimal protocol that records whether its <see cref="Parse"/> method was called.</summary>
    private sealed class FlagProtocol(string name) : IProtocol
    {
        /// <inheritdoc/>
        public string Name => name;

        /// <inheritdoc/>
        public string UiName => name;

        /// <summary><see langword="true"/> if <see cref="Parse"/> was invoked at least once.</summary>
        public bool WasCalled
        {
            get; private set;
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            WasCalled = true;
            return data.Length;
        }
    }
}