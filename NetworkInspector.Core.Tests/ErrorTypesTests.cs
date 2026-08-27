// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for Core error types and factories.</summary>
internal sealed class ErrorTypesTests
{
    [Test]
    public async Task FieldAppendException_FromError_WrapsParseError()
    {
        ParseError error = ParseError.FieldAppendFailed();
        FieldAppendException ex = FieldAppendException.FromError(error);

        await Assert.That(ex.ParseError.Kind).IsEqualTo(ParseErrorKind.FieldAppendFailed);
        await Assert.That(ex.Message).IsEqualTo(error.ToString());
    }

    [Test]
    public async Task InvalidNameSettingsException_ForUiName_ContainsName()
    {
        InvalidNameSettingsException ex = InvalidNameSettingsException.ForUiName("bad name");

        await Assert.That(ex.Name).IsEqualTo("bad name");
        await Assert.That(ex.Message).Contains("Invalid UI name");
    }

    [Test]
    public async Task ParseError_FactoryMethods_ProduceExpectedKinds()
    {
        ParseError internalError = ParseError.InternalError("boom");
        ParseError appendFailed = ParseError.FieldAppendFailed();
        ParseError mismatch = ParseError.FieldTypeMismatch("f", FieldType.U64, FieldType.String);

        await Assert.That(internalError.Kind).IsEqualTo(ParseErrorKind.InternalError);
        await Assert.That(appendFailed.Kind).IsEqualTo(ParseErrorKind.FieldAppendFailed);
        await Assert.That(mismatch.Kind).IsEqualTo(ParseErrorKind.FieldTypeMismatch);
        await Assert.That(mismatch.Message).Contains("Field type mismatch");
    }

    [Test]
    public async Task ParseError_ProtocolTableMissing_ProducesKindAndMessage()
    {
        ParseError error = ParseError.ProtocolTableMissing("udp", "Dispatch table is not registered on this stack.");

        await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.ProtocolTableMissing);
        await Assert.That(error.ProtocolName).IsEqualTo("udp");
        await Assert.That(error.Message).IsEqualTo("Dispatch table is not registered on this stack.");
    }

    [Test]
    public async Task ParseError_ProtocolTableMissing_NullMessage_StoresEmptyString()
    {
        ParseError error = ParseError.ProtocolTableMissing(null, null);

        await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.ProtocolTableMissing);
        await Assert.That(error.ProtocolName).IsNull();
        await Assert.That(error.Message).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ParseError_InvalidData_NullMessage_StoresEmptyString()
    {
        ParseError error = ParseError.InvalidData("tcp", null);

        await Assert.That(error.Message).IsEqualTo(string.Empty);
        await Assert.That(error.ToString()).IsEqualTo(ParseErrorKind.InvalidData.ToString());
    }

    [Test]
    public async Task ParseError_InvalidData_NullProtocolName_Throws()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
            () => ParseError.InvalidData(null!, "detail"));

        await Assert.That(ex.ParamName).IsEqualTo("protocolName");
    }

    [Test]
    public async Task ParseError_InsufficientDataWithInfo_UsesInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ParseError error = ParseError.InsufficientDataWithInfo("eth", 14, 6);

            await Assert.That(error.Message).IsEqualTo("Insufficient data: expected 14 bytes, got 6");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task ParseError_Default_ToString_ReturnsKindName()
    {
        ParseError error = default;

        await Assert.That(error.ToString()).IsEqualTo(ParseErrorKind.InsufficientData.ToString());
        await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.InsufficientData);
    }

    [Test]
    public async Task PersistenceSettingsException_ForIo_WrapsInnerException()
    {
        IOException io = new("disk full");
        PersistenceSettingsException ex = PersistenceSettingsException.ForIo(io);

        await Assert.That(ex.InnerException).IsSameReferenceAs(io);
        await Assert.That(ex.Message).Contains("I/O error");
    }

    [Test]
    public async Task PersistenceSettingsException_ForJson_WrapsInnerException()
    {
        JsonException json = new("bad json");
        PersistenceSettingsException ex = PersistenceSettingsException.ForJson(json);

        await Assert.That(ex.InnerException).IsSameReferenceAs(json);
        await Assert.That(ex.Message).Contains("JSON error");
    }

    [Test]
    public async Task RegistrationException_InnerConstructor_IsReachable()
    {
        InvalidOperationException inner = new("root");
        RegistrationException ex = new TestRegistrationException("wrapped", inner);

        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
        await Assert.That(ex.Message).IsEqualTo("wrapped");
    }

    [Test]
    public async Task SettingsException_InnerConstructor_IsReachable()
    {
        InvalidOperationException inner = new("root");
        SettingsException ex = new TestSettingsException("wrapped", inner);

        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
        await Assert.That(ex.Message).IsEqualTo("wrapped");
    }

    [Test]
    public async Task ThrowHelpers_ThrowFieldAppend_MaxFieldCount_ThrowsFieldAppendException()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ThrowFieldAppendProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(1),
            Timestamp.FromSecs(1000),
            new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame, protoId);
        // Slot reservation throws on _AllocatedFieldCount, not the reader-visible _FieldCount.
        // Poking _FieldCount leaves reservation below the cap and _PublishFieldCount spins forever.
        System.Reflection.FieldInfo allocatedField = typeof(Packet).GetField(
            "_AllocatedFieldCount",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        allocatedField.SetValue(packet, ushort.MaxValue - 1);

        FieldAppendException ex = Assert.Throws<FieldAppendException>(() =>
        {
            MutField mutRoot = packet.RootFieldMut();
            mutRoot.Append(new FieldId(99), FieldValue.NewU64(1));
        });

        await Assert.That(ex.ParseError.Message).Contains("Maximum field count exceeded");
    }

    [Test]
    public async Task ThrowHelpers_ThrowNonFiniteF64_SettingsJson_ThrowsInvalidOperationException()
    {
        MethodInfo? method = typeof(SettingsManager).GetMethod(
            "_SettingF64ToJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        SettingValue nonFinite = SettingValue.F64(double.NaN);
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => method!.Invoke(null, [nonFinite]));
        await Assert.That(ex.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(ex.InnerException!.Message).Contains("F64 setting value must be finite");
    }

    [Test]
    public async Task ThrowHelpers_ThrowNonFiniteF64_UsesInvariantCulture()
    {
        MethodInfo? method = typeof(SettingsManager).GetMethod(
            "_SettingF64ToJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            SettingValue nonFinite = SettingValue.F64(double.PositiveInfinity);
            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
                () => method!.Invoke(null, [nonFinite]));

            await Assert.That(ex.InnerException!.Message).IsEqualTo(
                "F64 setting value must be finite, got Infinity.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class ThrowFieldAppendProtocol : IProtocol
    {
        public string Name => "throw.append";
        public string UiName => "Throw Append";
        public string Description => string.Empty;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ = Description;
            builder.RegisterField(protocolId, "throw.append.root", "Root", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }

    private sealed class TestRegistrationException : RegistrationException
    {
        internal TestRegistrationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private sealed class TestSettingsException : SettingsException
    {
        internal TestSettingsException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
