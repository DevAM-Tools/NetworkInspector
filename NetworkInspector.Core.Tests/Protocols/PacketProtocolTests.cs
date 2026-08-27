// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests.Protocols;

/// <summary>
/// Exit-point coverage for <see cref="PacketProtocol"/> exception formatting.
/// </summary>
internal sealed class PacketProtocolTests
{
    [Test]
    public async Task MainDispatch_ExceptionWithStackTrace_RecordsFormattedError()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry())
        {
            IncludeExceptionStackTrace = true
        };
        ThrowingFrameProto frame = new();
        builder.RegisterProtocol(frame);
        using Stack stack = builder.Build();

        Frame frameData = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), new byte[42],
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frameData);

        FieldLookupCookie cookie = FieldLookupCookie.Start;
        bool found = packet.TryGetNextFieldValue(stack.PacketErrorFieldId, ref cookie, out FieldValue err, materialize: true); // materialize: true — need complete field tree for assertion
        err.Data.TryGetAsString(out string msg);

        await Assert.That(found).IsTrue();
        await Assert.That(msg.Contains("frame dispatch failed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(msg.Contains('\n')).IsTrue();
    }

    [Test]
    public async Task Parse_FrameConsumesPartial_AppendsUnparsedData()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.RegisterProtocol(new PartialConsumeFrame());
        using Stack stack = builder.Build();

        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        Frame frameData = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), payload,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frameData);

        FieldId unparsedId = stack.GetFieldId("packet.unparsed_data")!.Value;
        bool found = packet.TryGetFieldValue(unparsedId, out FieldValue value, materialize: true);
        _ = value.Data.TryGetAsBytes(out ReadOnlyMemory<byte> leftover);
        byte[] expected = [3, 4, 5, 6, 7, 8];

        await Assert.That(found && leftover.Span.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task Parse_FrameConsumesAll_DoesNotAppendUnparsedData()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        builder.RegisterProtocol(new FullConsumeFrame());
        using Stack stack = builder.Build();

        byte[] payload = [1, 2, 3, 4];
        Frame frameData = Frame.Create(
            new FrameId(1), Timestamp.FromSecs(0), payload,
            LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        Packet packet = Packet.ParseFrame(new PacketId(0), stack, frameData);

        FieldId unparsedId = stack.GetFieldId("packet.unparsed_data")!.Value;
        bool found = packet.TryGetFieldValue(unparsedId, out _, materialize: true);

        await Assert.That(found).IsFalse();
    }

    private sealed class PartialConsumeFrame : IProtocol
    {
        public string Name => "frame";
        public string UiName => "Frame";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => 2;
    }

    private sealed class FullConsumeFrame : IProtocol
    {
        public string Name => "frame";
        public string UiName => "Frame";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => data.Length;
    }

    private sealed class ThrowingFrameProto : IProtocol
    {
        public string Name => "frame";
        public string UiName => "Frame";

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
            => throw new InvalidOperationException("frame dispatch failed");
    }
}
