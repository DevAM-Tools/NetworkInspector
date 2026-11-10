// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// A deferred custom text whose formatter throws while the value cache records it at the end of the parse: the fault is
/// contained per text, like a throw at append.
/// </summary>
internal sealed class ValueCacheDeferredTextFaultTests
{
    #region Tests

    [Test]
    public async Task AThrowingDeferredTextIsContainedAndTheOtherTextsTheIndexAndTheObserversSurvive()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        FaultyTextProtocol proto = new();
        ProtocolId protoId = builder.RegisterProtocol(proto);
        proto.RegisterFields(builder, protoId);
        using Stack stack = builder.Build();
        ValueCache cache = new(
            stack,
            [
                new ValueCacheFieldConfig(proto.FaultyId, RecordValue: false, RecordCustomText: true),
                new ValueCacheFieldConfig(proto.GoodId, RecordValue: false, RecordCustomText: true),
            ]);
        PacketIndex index = new(stack);
        CompleteRecorder recorder = new();
        ParseObserver[] observers = [new() { Observer = recorder, Watch = FieldWatch.All }];
        ParseOptions options = new(FieldTreeMode.Build, cache, index: index, observers: observers, firstProtocol: protoId);
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[16], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

        bool parsed = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out _);

        bool hasError = packet!.TryGetFieldValue(stack.PacketErrorFieldId, out FieldValue error, materialize: false);
        ValueCacheSeries<string> good = cache.GetCustomTextSeries(proto.GoodId);
        await Assert.That(parsed).IsTrue();
        await Assert.That(index.GetProtocolBitmap(protoId).Contains(0)).IsTrue();
        await Assert.That(recorder.Complete).IsTrue();
        await Assert.That(good.Count).IsEqualTo(1);
        await Assert.That(good[0].Value).IsEqualTo("b");
        await Assert.That(hasError).IsTrue();
        await Assert.That(error.ToString(null, CultureInfo.InvariantCulture)).Contains("boom");
    }

    #endregion

    #region Test doubles

    /// <summary>Appends one field whose deferred text throws and one whose deferred text builds "b".</summary>
    private sealed class FaultyTextProtocol : IProtocol
    {
        public FieldId FaultyId;
        public FieldId GoodId;
        private ProtocolId _ProtocolId;
        private int _Resets;

        public string Name => "vcxfault";
        public string UiName => "Faulty Text Exercise";

        public void ResetParseState() => _Resets++;

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ProtocolId = protocolId;
            FaultyId = builder.RegisterField(protocolId, "vcxfault.faulty", "Faulty", FieldType.U64);
            GoodId = builder.RegisterField(protocolId, "vcxfault.good", "Good", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            // A first protocol is not recorded by a dispatcher, so it records its own presence; the test checks that the
            // index entry survives the faulty text.
            context.RecordProtocolPresence(_ProtocolId);
            _ = parentField.AppendWithCustomText(
                FaultyId, FieldValue.NewU64(1), LazyString.FormatLazy(0, static _ => throw new InvalidOperationException("boom")));
            _ = parentField.AppendWithCustomText(GoodId, FieldValue.NewU64(2), LazyString.FormatLazy(0, static _ => "b"));
            return data.Length;
        }
    }

    /// <summary>Records the <c>complete</c> flag of the last <see cref="IFieldObserver.EndPacket"/>.</summary>
    private sealed class CompleteRecorder : IFieldObserver
    {
        public bool Complete;

        public void BeginPacket(PacketId id, Timestamp timestamp)
        {
        }

        public void OnField(in FieldVisit visit)
        {
        }

        public void EndPacket(PacketId id, bool complete) => Complete = complete;
    }

    #endregion
}
