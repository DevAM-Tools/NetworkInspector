// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// <see cref="Field.CustomText"/> refers to the field's own storage and never evaluates a deferred text by itself: looking at the
/// text costs nothing, and reading its string builds it once in the field, for every later reader.
/// </summary>
internal sealed class FieldCustomTextTests
{
    #region Helpers

    /// <summary>Parses a static UDP frame with a field tree.</summary>
    /// <param name="stack">The stack to parse with.</param>
    /// <returns>The parsed packet.</returns>
    private static Packet _ParseWithTree(Stack stack)
    {
        ParseOptions options = new(FieldTreeMode.Build);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromSecs(0),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        _ = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out _);
        return packet!;
    }

    /// <summary>Finds the first field with the given id.</summary>
    /// <param name="packet">The packet to search.</param>
    /// <param name="id">The field id.</param>
    /// <returns>The field cursor; default when not found.</returns>
    private static Field _Find(Packet packet, FieldId id)
    {
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id)
            {
                return field;
            }
        }

        return default;
    }

    #endregion

    #region Tests

    [Test]
    public async Task ReadingTheTextOfAFieldDoesNotEvaluateItAndTheStringIsBuiltOnce()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        Packet packet = _ParseWithTree(stack);
        Field field = _Find(packet, stack.GetFieldId("udp")!.Value);

        bool isNull = field.CustomText.IsNull;
        bool lazyAfterLooking = field.CustomText.IsLazy;
        string first = field.CustomText.AsString;
        string second = field.CustomText.AsString;
        // A copy taken after the read carries the string that was built in the field.
        LazyString copyAfter = field.CustomText;

        await Assert.That(isNull).IsFalse();
        await Assert.That(lazyAfterLooking).IsTrue();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(copyAfter.IsEvaluated).IsTrue();
        await Assert.That(ReferenceEquals(copyAfter.AsString, first)).IsTrue();
    }

    [Test]
    public async Task EvaluatingTheRepresentationThroughTheFieldReplacesTheDeferredObjectInTheSlot()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        RepresentationProtocol proto = new();
        ProtocolId id = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, id);
        using Stack stack = builder.Build();
        ParseOptions options = new(FieldTreeMode.Build, firstProtocol: id);
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[4], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        _ = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out _);
        Field field = _Find(packet!, proto.ValueId);
        bool lazyBefore = field.Value.CustomRepresentation.IsLazy;

        string first = field.Value.CustomRepresentation.AsString;
        // A copy taken after the read carries the string only when the read went through the reference to the slot.
        LazyString copyAfter = field.Value.CustomRepresentation;

        await Assert.That(lazyBefore).IsTrue();
        await Assert.That(first).IsEqualTo("rep 7");
        await Assert.That(copyAfter.IsEvaluated).IsTrue();
        await Assert.That(ReferenceEquals(copyAfter.AsString, first)).IsTrue();
    }

    [Test]
    public async Task EightThreadsReadingOneSealedFieldGetOneStringAndLeaveTheSlotEvaluated()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        using Stack stack = builder.Build();
        Packet packet = _ParseWithTree(stack);
        Field field = _Find(packet, stack.GetFieldId("udp")!.Value);
        const int readerCount = 8;
        string[] seen = new string[readerCount];
        bool lazyBefore = field.CustomText.IsLazy;

        // Dedicated threads, not pool tasks: all eight must be alive at the barrier at the same time, so the first reads race on
        // the one slot of the sealed packet.
        using Barrier barrier = new(readerCount);
        Thread[] readers = new Thread[readerCount];
        for (int i = 0; i < readerCount; i++)
        {
            int slot = i;
            readers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                seen[slot] = field.CustomText.AsString;
            })
            {
                Name = "field-text-reader",
            };
            readers[i].Start();
        }

        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        await Assert.That(lazyBefore).IsTrue();
        await Assert.That(seen.All(text => ReferenceEquals(text, seen[0]))).IsTrue();
        await Assert.That(field.CustomText.IsEvaluated).IsTrue();
        await Assert.That(ReferenceEquals(field.CustomText.AsString, seen[0])).IsTrue();
    }

    #endregion

    #region Test doubles

    /// <summary>Appends one field whose custom representation is a deferred text.</summary>
    private sealed class RepresentationProtocol : IProtocol
    {
        private int _Resets;

        public FieldId ValueId { get; private set; }

        public string Name => "reprtest";
        public string UiName => "Representation Test";

        public void ResetParseState() => _Resets++;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId) =>
            ValueId = builder.RegisterField(protocolId, "reprtest.value", "Value", FieldType.U64);

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldValue value = FieldValue.NewU64(7).WithCustomRepresentation(
                LazyString.FormatLazy(7, static v => string.Create(CultureInfo.InvariantCulture, $"rep {v}")));
            _ = parentField.Append(ValueId, value);
            return data.Length;
        }
    }

    #endregion
}
