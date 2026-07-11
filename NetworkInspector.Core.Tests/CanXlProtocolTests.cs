// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for CAN XL protocol parsing via <see cref="NetworkInspector.Protocols.CanProtocol"/>
/// (ISO 11898-1:2024, SocketCAN format).
/// Verifies field extraction for CAN XL frames including priority, VCID,
/// flags, SDU type, acceptance field, and payload data.
/// Also verifies that CAN XL dispatches via <c>can.id</c> (key = priority) and
/// <c>can.extended_id</c> (key = acceptance field).
/// </summary>
internal sealed class CanProtocolXlTests
{
    /// <summary>
    /// Builds a stack and parses a SocketCAN frame (link type 227).
    /// </summary>
    private static (Stack Stack, Packet Packet) _BuildAndParse(byte[] frameData)
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
    public async Task Parse_CanXl_PriorityCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(priority: 7);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? prioField = stack.GetFieldId("canxl.priority");
            await Assert.That(prioField).IsNotNull();

            bool has = packet.TryGetFieldValue(prioField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(7UL);
        }
    }

    [Test]
    public async Task Parse_CanXl_MaxPriority()
    {
        // Maximum 11-bit priority = 2047 (0x7FF)
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(priority: 0x7FF);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? prioField = stack.GetFieldId("canxl.priority");
            bool has = packet.TryGetFieldValue(prioField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x7FFUL);
        }
    }

    [Test]
    public async Task Parse_CanXl_VcidCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(vcid: 0xAB);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? vcidField = stack.GetFieldId("canxl.vcid");
            await Assert.That(vcidField).IsNotNull();

            bool has = packet.TryGetFieldValue(vcidField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xABUL);
        }
    }

    [Test]
    public async Task Parse_CanXl_XlfFlagAlwaysSet()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame();

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? xlfField = stack.GetFieldId("canxl.flags.xlf");
            await Assert.That(xlfField).IsNotNull();

            bool has = packet.TryGetFieldValue(xlfField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanXl_SecFlag()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(sec: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? secField = stack.GetFieldId("canxl.flags.sec");
            await Assert.That(secField).IsNotNull();

            bool has = packet.TryGetFieldValue(secField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanXl_SecFlagNotSet()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(sec: false);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? secField = stack.GetFieldId("canxl.flags.sec");
            bool has = packet.TryGetFieldValue(secField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsFalse();
        }
    }

    [Test]
    public async Task Parse_CanXl_RrsFlag()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(rrs: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? rrsField = stack.GetFieldId("canxl.flags.rrs");
            await Assert.That(rrsField).IsNotNull();

            bool has = packet.TryGetFieldValue(rrsField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanXl_SduTypeCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(sduType: 0x42);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? sduField = stack.GetFieldId("canxl.sdu_type");
            await Assert.That(sduField).IsNotNull();

            bool has = packet.TryGetFieldValue(sduField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x42UL);
        }
    }

    [Test]
    public async Task Parse_CanXl_PayloadLengthCorrect()
    {
        byte[] payload = new byte[64];
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? lenField = stack.GetFieldId("canxl.len");
            await Assert.That(lenField).IsNotNull();

            bool has = packet.TryGetFieldValue(lenField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(64UL);
        }
    }

    [Test]
    public async Task Parse_CanXl_AcceptanceFieldCorrect()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(acceptanceField: 0xDEADBEEF);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? afField = stack.GetFieldId("canxl.acceptance_field");
            await Assert.That(afField).IsNotNull();

            bool has = packet.TryGetFieldValue(afField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xDEADBEEFUL);
        }
    }

    [Test]
    public async Task Parse_CanXl_DataPayload()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE];
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? dataField = stack.GetFieldId("canxl.data");
            await Assert.That(dataField).IsNotNull();

            bool has = packet.TryGetFieldValue(dataField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();

            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data);
            await Assert.That(data.Length).IsEqualTo(6);
            await Assert.That(data.Span[0]).IsEqualTo((byte)0xDE);
            await Assert.That(data.Span[5]).IsEqualTo((byte)0xFE);
        }
    }

    [Test]
    public async Task Parse_CanXl_LargePayload()
    {
        // Test with a large CAN XL payload (256 bytes)
        byte[] payload = new byte[256];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(payload: payload);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? dataField = stack.GetFieldId("canxl.data");
            bool has = packet.TryGetFieldValue(dataField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();

            value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> data);
            await Assert.That(data.Length).IsEqualTo(256);
            await Assert.That(data.Span[0]).IsEqualTo((byte)0x00);
            await Assert.That(data.Span[255]).IsEqualTo((byte)0xFF);
        }
    }

    [Test]
    public async Task Parse_CanXl_DoesNotInterferWithClassicCan()
    {
        // A classic CAN frame must still be parsed by CanProtocol, not CanXlProtocol
        byte[] frameData = FrameBuilders.GenerateCanFrame(canId: 0x123);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // Classic CAN fields should still be present
            FieldId? canIdField = stack.GetFieldId("can.id");
            await Assert.That(canIdField).IsNotNull();
            bool hasCan = packet.TryGetFieldValue(canIdField!.Value, out FieldValue canValue);
            await Assert.That(hasCan).IsTrue();
            canValue.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0x123UL);

            // CAN XL fields should NOT be present
            FieldId? xlPrioField = stack.GetFieldId("canxl.priority");
            await Assert.That(xlPrioField).IsNotNull();
            bool hasXl = packet.TryGetFieldValue(xlPrioField!.Value, out _);
            await Assert.That(hasXl).IsFalse();
        }
    }

    [Test]
    public async Task Parse_CanXl_DoesNotInterferWithCanFd()
    {
        // A CAN FD frame must still be parsed by CanProtocol, not CanXlProtocol
        byte[] frameData = FrameBuilders.GenerateCanFdFrame(canId: 0x456);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            // CAN FD fields should still be present
            FieldId? fdField = stack.GetFieldId("can.flags.fd");
            await Assert.That(fdField).IsNotNull();
            bool hasFd = packet.TryGetFieldValue(fdField!.Value, out FieldValue fdValue);
            await Assert.That(hasFd).IsTrue();
            fdValue.Data.TryGetAsBool(out bool boolVal);
            await Assert.That(boolVal).IsTrue();

            // CAN XL fields should NOT be present
            FieldId? xlPrioField = stack.GetFieldId("canxl.priority");
            bool hasXl = packet.TryGetFieldValue(xlPrioField!.Value, out _);
            await Assert.That(hasXl).IsFalse();
        }
    }

    [Test]
    public async Task Parse_CanXl_ShortData_ReturnZero()
    {
        // Only 4 bytes — not enough to check XLF flag. CanProtocol returns an error for
        // frames shorter than MinHeaderSize (8 bytes), so no fields are appended.
        byte[] shortFrame = [0x23, 0x01, 0x00, 0x00];

        (Stack stack, Packet packet) = _BuildAndParse(shortFrame);
        using (stack)
        {
            // Neither CAN nor CAN XL fields should be present
            FieldId? xlPrioField = stack.GetFieldId("canxl.priority");
            await Assert.That(xlPrioField).IsNotNull();
            bool has = packet.TryGetFieldValue(xlPrioField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_CanXl_HeaderOnlyTooShort()
    {
        // 5 bytes with XLF set — enough to detect CAN XL but not enough for full header
        byte[] frame = [0x05, 0x00, 0x00, 0x00, 0x80];

        (Stack stack, Packet packet) = _BuildAndParse(frame);
        using (stack)
        {
            // CAN XL parsing should fail (insufficient data for 12-byte header)
            FieldId? xlPrioField = stack.GetFieldId("canxl.priority");
            bool has = packet.TryGetFieldValue(xlPrioField!.Value, out _);
            await Assert.That(has).IsFalse();
        }
    }

    [Test]
    public async Task Parse_CanXl_IndexPresence()
    {
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(priority: 3);

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

            // CAN protocol (which now handles all variants including XL) should be in the index
            ProtocolId? canProtocolId = stack.GetProtocolId("can");
            await Assert.That(canProtocolId).IsNotNull();
            await Assert.That(index.GetProtocolBitmap(canProtocolId!.Value).Contains(0)).IsTrue();

            // CAN XL priority field should be in the index
            FieldId? prioField = stack.GetFieldId("canxl.priority");
            await Assert.That(prioField).IsNotNull();
            await Assert.That(index.GetFieldBitmap(prioField!.Value).Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanXl_AllFlagsSet()
    {
        // Test with all optional flags enabled
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(sec: true, rrs: true);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? secField = stack.GetFieldId("canxl.flags.sec");
            FieldId? rrsField = stack.GetFieldId("canxl.flags.rrs");
            FieldId? xlfField = stack.GetFieldId("canxl.flags.xlf");

            bool hasSec = packet.TryGetFieldValue(secField!.Value, out FieldValue secValue);
            bool hasRrs = packet.TryGetFieldValue(rrsField!.Value, out FieldValue rrsValue);
            bool hasXlf = packet.TryGetFieldValue(xlfField!.Value, out FieldValue xlfValue);

            await Assert.That(hasSec).IsTrue();
            await Assert.That(hasRrs).IsTrue();
            await Assert.That(hasXlf).IsTrue();
            secValue.Data.TryGetAsBool(out bool boolVal);
            rrsValue.Data.TryGetAsBool(out bool boolVal2);
            xlfValue.Data.TryGetAsBool(out bool boolVal3);
            await Assert.That(boolVal).IsTrue();
            await Assert.That(boolVal2).IsTrue();
            await Assert.That(boolVal3).IsTrue();
        }
    }

    [Test]
    public async Task Parse_CanXl_ZeroPriorityAndVcid()
    {
        // Edge case: priority=0, vcid=0
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(priority: 0, vcid: 0);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? prioField = stack.GetFieldId("canxl.priority");
            FieldId? vcidField = stack.GetFieldId("canxl.vcid");

            bool hasPrio = packet.TryGetFieldValue(prioField!.Value, out FieldValue prioValue);
            bool hasVcid = packet.TryGetFieldValue(vcidField!.Value, out FieldValue vcidValue);

            await Assert.That(hasPrio).IsTrue();
            await Assert.That(hasVcid).IsTrue();
            prioValue.Data.TryGetAsU64(out ulong u64Val);
            vcidValue.Data.TryGetAsU64(out ulong u64Val2);
            await Assert.That(u64Val).IsEqualTo(0UL);
            await Assert.That(u64Val2).IsEqualTo(0UL);
        }
    }

    [Test]
    public async Task Parse_CanXl_MaxVcid()
    {
        // Maximum 8-bit VCID = 255 (0xFF)
        byte[] frameData = FrameBuilders.GenerateCanXlFrame(vcid: 0xFF);

        (Stack stack, Packet packet) = _BuildAndParse(frameData);
        using (stack)
        {
            FieldId? vcidField = stack.GetFieldId("canxl.vcid");
            bool has = packet.TryGetFieldValue(vcidField!.Value, out FieldValue value);
            await Assert.That(has).IsTrue();
            value.Data.TryGetAsU64(out ulong u64Val);
            await Assert.That(u64Val).IsEqualTo(0xFFUL);
        }
    }

    [Test]
    public async Task Parse_CanXl_DispatchesViaCanIdTable_UsingPriority()
    {
        // Register a probe protocol at can.id key=42.
        // CAN protocol dispatches CAN XL via can.id using priority as the key,
        // so a CAN XL frame with priority=42 must invoke the probe.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        FlagProtocol probe = new("probe.canid");
        ProtocolId probeId = builder.RegisterProtocol(probe);
        builder.RegisterParserInU64TableByName(NetworkInspector.Protocols.CanProtocol.IdTableName, 42, probeId);
        Stack stack = builder.Build();

        byte[] frameData = FrameBuilders.GenerateCanXlFrame(priority: 42, payload: [0x01, 0x02, 0x03]);
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
    public async Task Parse_CanXl_DispatchesViaCanExtendedIdTable_UsingAcceptanceField()
    {
        // Register a probe protocol at can.extended_id key=0xDEADBEEF.
        // CAN protocol dispatches CAN XL via can.extended_id using acceptanceField as the key,
        // so a CAN XL frame with acceptanceField=0xDEADBEEF must invoke the probe.
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        NetworkInspector.Protocols.ProtocolRegistration.RegisterStandardProtocols(builder);
        FlagProtocol probe = new("probe.canextended");
        ProtocolId probeId = builder.RegisterProtocol(probe);
        builder.RegisterParserInU64TableByName(
            NetworkInspector.Protocols.CanProtocol.ExtendedIdTableName, 0xDEADBEEF, probeId);
        Stack stack = builder.Build();

        byte[] frameData = FrameBuilders.GenerateCanXlFrame(acceptanceField: 0xDEADBEEF, payload: [0xAA, 0xBB]);
        Frame frame = Frame.Create(
            new FrameId(0), Timestamp.FromSecs(0), frameData,
            LinkType.CanSocketcan, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet.ParseFrame(new PacketId(0), stack, frame);

        using (stack)
        {
            await Assert.That(probe.WasCalled).IsTrue();
        }
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
