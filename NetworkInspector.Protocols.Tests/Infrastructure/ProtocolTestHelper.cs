// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Helper methods shared across protocol tests. Builds a full protocol stack
/// and parses a raw frame into a <see cref="Packet"/>.
/// </summary>
internal static class ProtocolTestHelper
{
    /// <summary>Lazily initialized shared stack for tests that need read-only access.</summary>
    private static readonly Lazy<Stack> _SharedStack = new(() =>
    {
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    });

    /// <summary>
    /// Returns a shared, lazily-initialized stack instance.
    /// This stack must NOT be disposed by callers — it is shared across all tests.
    /// Use this for simple parse-and-verify tests where no stack modification is needed.
    /// </summary>
    internal static Stack SharedStack => _SharedStack.Value;

    /// <summary>
    /// Creates a full protocol stack with all standard protocols registered,
    /// then parses <paramref name="frameData"/> as if it were a captured frame
    /// with the given <paramref name="linkType"/>.
    /// </summary>
    /// <returns>A tuple of the built <see cref="Stack"/> (caller must dispose) and the parsed <see cref="Packet"/>.</returns>
    internal static (Stack Stack, Packet Packet) BuildAndParse(
        byte[] frameData,
        LinkType linkType = LinkType.Ethernet)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    /// <summary>
    /// Creates a full protocol stack with custom settings, then parses <paramref name="frameData"/>.
    /// Use this when settings need to be modified before parsing (e.g. enabling checksum validation).
    /// </summary>
    internal static (Stack Stack, Packet Packet) BuildAndParse(
        byte[] frameData,
        Action<SettingsManager> configureSettings,
        LinkType linkType = LinkType.Ethernet)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        // Temp-file JSON configs (signal_message / pdu_transport) resolve under this base.
        SettingsManager settings = new(Path.GetTempPath());
#pragma warning restore CA2000
        configureSettings(settings);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Stack stack = builder.Build();

        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frame);
        return (stack, packet);
    }

    /// <summary>
    /// Parses <paramref name="frameData"/> using the <see cref="SharedStack"/>.
    /// Returns only the packet — the stack is shared and must not be disposed.
    /// </summary>
    internal static Packet ParseWithSharedStack(byte[] frameData, LinkType linkType = LinkType.Ethernet)
    {
        Stack stack = SharedStack;
        Frame frame = Frame.Create(
            new FrameId(0),
            Timestamp.FromSecs(0),
            frameData,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(0), stack, frame);
    }

    /// <summary>
    /// Asserts that a U64 field exists and has the expected value.
    /// Uses <c>materialize: true</c> so lazy fields are included in the assertion.
    /// </summary>
    internal static async Task AssertU64Field(Stack stack, Packet packet, string fieldName, ulong expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true);
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsU64(out ulong actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be U64");
        await Assert.That(actual).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that an F64 field exists and has the expected value (exact match).
    /// Uses <c>materialize: true</c> so lazy fields are included in the assertion.
    /// </summary>
    internal static async Task AssertF64Field(Stack stack, Packet packet, string fieldName, double expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true);
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsF64(out double actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be F64");
        await Assert.That(actual).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that an F64 field exists and its value is within <paramref name="tolerance"/> of <paramref name="expected"/>.
    /// Useful for RTT and timing fields where floating-point precision may vary.
    /// </summary>
    internal static async Task AssertF64FieldApprox(Stack stack, Packet packet, string fieldName, double expected, double tolerance)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsF64(out double actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be F64");
        double diff = Math.Abs(actual - expected);
        await Assert.That(diff).IsLessThanOrEqualTo(tolerance)
            .Because($"Field '{fieldName}': expected ~{expected}, got {actual} (tolerance {tolerance})");
    }

    /// <summary>
    /// Creates and returns a new protocol stack for multi-packet test scenarios.
    /// The caller is responsible for disposing the stack via <c>using</c>.
    /// </summary>
    internal static Stack BuildStack()
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>
    /// Creates and returns a new protocol stack with custom settings.
    /// Temp-file JSON configs (signal_message / pdu_transport) resolve under
    /// <see cref="Path.GetTempPath"/>. The caller is responsible for disposing the stack via <c>using</c>.
    /// </summary>
    internal static Stack BuildStack(Action<SettingsManager> configureSettings)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        SettingsManager settings = new(Path.GetTempPath());
#pragma warning restore CA2000
        configureSettings(settings);
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>
    /// Creates and returns a new protocol stack with overridden settings.
    /// Settings are pre-loaded into the <see cref="SettingsManager"/> BEFORE protocol
    /// registration so that protocols see the overridden values during their
    /// <c>RegisterFields</c> phase (which is where setting values are loaded into
    /// backing fields and where config-driven setup runs).
    /// Use this when you need to modify settings that are registered by protocols
    /// (e.g., <c>tcp.verify_checksum</c>).
    /// </summary>
    internal static Stack BuildStackWithSettings(params (string Name, SettingValue Value)[] settingOverrides)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        SettingsManager settings = new();
#pragma warning restore CA2000

        // Pre-load values BEFORE registration — these are applied at RegisterSetting time
        // so the protocol's RegisterFields sees the overridden value when it loads the
        // backing field.
        foreach ((string name, SettingValue value) in settingOverrides)
        {
            settings.PreloadValue(name, value);
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);

        return builder.Build();
    }

    /// <summary>
    /// Parses a single frame on an existing stack. Use for multi-packet scenarios
    /// where state must accumulate across sequential <see cref="Packet.ParseFrame"/> calls.
    /// </summary>
    internal static Packet ParseFrame(
        Stack stack,
        byte[] frameData,
        int packetIndex,
        Timestamp timestamp,
        LinkType linkType = LinkType.Ethernet)
    {
        Frame frame = Frame.Create(
            new FrameId(packetIndex),
            timestamp,
            frameData,
            linkType,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

        return Packet.ParseFrame(new PacketId(packetIndex), stack, frame);
    }

    /// <summary>
    /// Asserts that a Bool field exists and has the expected value.
    /// </summary>
    internal static async Task AssertBoolField(Stack stack, Packet packet, string fieldName, bool expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsBool(out bool actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be Bool");
        await Assert.That(actual).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that a String field exists and has the expected value.
    /// </summary>
    internal static async Task AssertStringField(Stack stack, Packet packet, string fieldName, string expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsString(out string? actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be String");
        await Assert.That(actual).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that a MAC address field exists and has the expected string representation.
    /// </summary>
    internal static async Task AssertMacField(Stack stack, Packet packet, string fieldName, string expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsMacAddress(out MacAddress actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be MacAddress");
        await Assert.That(actual.ToString()).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that an IPv4 address field exists and has the expected string representation.
    /// </summary>
    internal static async Task AssertIPv4Field(Stack stack, Packet packet, string fieldName, string expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsIPv4(out IPv4Address actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be IPv4Address");
        await Assert.That(actual.ToString()).IsEqualTo(expected).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that an IPv6 address field exists and has the expected string representation.
    /// </summary>
    internal static async Task AssertIPv6Field(Stack stack, Packet packet, string fieldName, string expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsIPv6(out IPv6Address actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be IPv6Address");
        // Use case-insensitive comparison — IPv6 hex digits may be upper- or lower-case
        await Assert.That(actual.ToString().ToUpperInvariant())
            .IsEqualTo(expected.ToUpperInvariant()).Because($"Field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that a Bytes field exists in the packet.
    /// </summary>
    internal static async Task AssertBytesField(Stack stack, Packet packet, string fieldName, byte[] expected)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");

        bool ok = value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> actual);
        await Assert.That(ok).IsTrue().Because($"Field '{fieldName}' must be Bytes");
        await Assert.That(actual.Span.SequenceEqual(expected)).IsTrue().Because($"Field '{fieldName}' bytes mismatch");
    }

    /// <summary>
    /// Asserts that a field exists in the packet (regardless of value).
    /// </summary>
    internal static async Task AssertFieldExists(Stack stack, Packet packet, string fieldName)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        bool found = packet.TryGetFieldValue(fieldId!.Value, out _, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that a field does NOT exist in the packet. Useful for verifying absence of optional fields.
    /// </summary>
    internal static async Task AssertFieldNotPresent(Stack stack, Packet packet, string fieldName)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        if (fieldId is null)
        {
            // Field not even registered — that counts as "not present"
            return;
        }

        bool found = packet.TryGetFieldValue(fieldId.Value, out _, materialize: true); // materialize: true — include lazy fields in assertion
        await Assert.That(found).IsFalse().Because($"Packet must NOT contain field '{fieldName}'");
    }

    /// <summary>
    /// Asserts that a protocol container field is present in the packet.
    /// </summary>
    internal static async Task AssertProtocolPresent(Stack stack, Packet packet, string protocolFieldName) =>
        await AssertFieldExists(stack, packet, protocolFieldName).ConfigureAwait(false);

    /// <summary>
    /// Asserts that a protocol container field is NOT present in the packet.
    /// </summary>
    internal static async Task AssertProtocolNotPresent(Stack stack, Packet packet, string protocolFieldName) =>
        await AssertFieldNotPresent(stack, packet, protocolFieldName).ConfigureAwait(false);

    /// <summary>
    /// Asserts that the custom display text of a field matches the expected value.
    /// Custom text is stored on the field tree node (<see cref="FieldBody.CustomText"/>)
    /// by protocols via <c>AppendWithCustomText</c>, not on the <see cref="FieldValue"/>.
    /// </summary>
    internal static async Task AssertDisplayText(Stack stack, Packet packet, string fieldName, string expectedDisplayText)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        // Search the field tree for the matching node to access its CustomText
        LazyString customText = _FindFieldCustomText(packet, fieldId!.Value, out bool found);
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");
        await Assert.That(customText.IsNull).IsFalse().Because($"Field '{fieldName}' must have custom display text");
        await Assert.That((string)customText).IsEqualTo(expectedDisplayText).Because($"Field '{fieldName}' display text");
    }

    /// <summary>
    /// Asserts that the custom display text of a field contains the expected substring.
    /// Useful when the exact display text format may vary.
    /// </summary>
    internal static async Task AssertDisplayTextContains(Stack stack, Packet packet, string fieldName, string expectedSubstring)
    {
        FieldId? fieldId = stack.GetFieldId(fieldName);
        await Assert.That(fieldId).IsNotNull().Because($"Field '{fieldName}' must be registered");

        // Search the field tree for the matching node to access its CustomText
        LazyString customText = _FindFieldCustomText(packet, fieldId!.Value, out bool found);
        await Assert.That(found).IsTrue().Because($"Packet must contain field '{fieldName}'");
        await Assert.That(customText.IsNull).IsFalse().Because($"Field '{fieldName}' must have custom display text");
        await Assert.That((string)customText).Contains(expectedSubstring).Because($"Field '{fieldName}' display text should contain '{expectedSubstring}'");
    }

    /// <summary>
    /// Scans the packet's field tree (DFS) for the first occurrence of <paramref name="fieldId"/>
    /// and returns its <see cref="Field.CustomText"/>.
    /// </summary>
    private static LazyString _FindFieldCustomText(Packet packet, FieldId fieldId, out bool found)
    {
        // DFS through the public field tree API
        Field root = packet.RootField();
        return _SearchCustomText(root, fieldId, out found);
    }

    /// <summary>
    /// Recursively searches for a field by ID in the field tree and returns its custom text.
    /// </summary>
    private static LazyString _SearchCustomText(Field field, FieldId fieldId, out bool found)
    {
        if (field.FieldId == fieldId)
        {
            found = true;
            return field.CustomText;
        }

        // materialize: true — custom text may live under a lazy container.
        foreach (Field child in field.Children(materialize: true))
        {
            LazyString result = _SearchCustomText(child, fieldId, out found);
            if (found)
            {
                return result;
            }
        }

        found = false;
        return default;
    }
}
