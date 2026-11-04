// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Custom text for observers in a parse without field tree. The field tree stores the text of a field; a tree-free consumer
/// gets it through <see cref="FieldVisit.CustomText"/> (and <c>Value.CustomRepresentation</c>) when its
/// <see cref="ParseObserver.DisplayText"/> watch accepts the field. Without that watch nothing is built: formatting text is work
/// that most observers (filters) do not want to pay.
/// </summary>
internal sealed class FieldObserverDisplayTextTests
{
    #region Helpers

    private static Stack _Standard()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Frame _Frame(Stack stack, byte[] data) =>
        Frame.Create(
            0, Timestamp.FromSecs(0), data, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

    private static byte[] _Bytes(string kind) => kind switch
    {
        "udp" => FrameBuilders.GenerateStaticUdpIpv6Frame(),
        "tcp" => FrameBuilders.Generate_TcpSynFrame(),
        _ => FrameBuilders.GenerateDnsQueryFrame(),
    };

    /// <summary>Text and representation of the first field with the id in a parse that keeps the field tree.</summary>
    private static (string? Text, string? Representation) _FromTree(Stack stack, byte[] data, FieldId id)
    {
        ParseOptions options = new(FieldTreeMode.Build);
        _ = Packet.TryParse(0, stack, _Frame(stack, data), in options, out Packet? packet, out _);
        foreach (Field field in packet!.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id)
            {
                string? text = field.TryGetCustomText(out LazyString custom) && !custom.IsNull
                    ? custom.AsString
                    : null;
                LazyString representation = field.Value.CustomRepresentation;
                return (text, representation.IsNull ? null : representation.AsString);
            }
        }

        return (null, null);
    }

    /// <summary>Text and representation as the observer evaluates them in EndPacket of a parse without field tree.</summary>
    private static (string? Text, string? Representation) _FromObserver(Stack stack, byte[] data, FieldId id, FieldWatch displayText)
    {
        TextRecorder recorder = new(id);
        ParseObserver[] entries = [new() { Observer = recorder, Watch = FieldWatch.ForExtraction(stack, id), DisplayText = displayText }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: entries);
        _ = Packet.TryParse(0, stack, _Frame(stack, data), in options, out _, out _);
        return (recorder.Text, recorder.Representation);
    }

    #endregion

    #region Tests

    [Test]
    [Arguments("udp", "udp")]
    [Arguments("tcp", "tcp")]
    [Arguments("dns", "dns.qry.name")]
    public async Task TreeFreeTextEqualsTreeTextWhenTheDisplayWatchAcceptsTheField(string kind, string fieldName)
    {
        using Stack stack = _Standard();
        FieldId id = stack.GetFieldId(fieldName)!.Value;
        byte[] data = _Bytes(kind);

        (string? treeText, string? treeRepresentation) = _FromTree(stack, data, id);
        (string? text, string? representation) = _FromObserver(stack, data, id, FieldWatch.Only(stack, id));

        // The tree must have something to compare, otherwise the test proves nothing.
        await Assert.That(treeText is not null || treeRepresentation is not null).IsTrue();
        await Assert.That(text).IsEqualTo(treeText);
        await Assert.That(representation).IsEqualTo(treeRepresentation);
    }

    [Test]
    [Arguments("udp", "udp")]
    [Arguments("tcp", "tcp")]
    [Arguments("dns", "dns.qry.name")]
    public async Task WithoutADisplayWatchNothingIsBuiltForTheObserver(string kind, string fieldName)
    {
        using Stack stack = _Standard();
        FieldId id = stack.GetFieldId(fieldName)!.Value;

        (string? text, string? representation) = _FromObserver(stack, _Bytes(kind), id, FieldWatch.None);

        await Assert.That(text).IsNull();
        // The UDP header size "8 bytes" is a constant string, built for nobody to pay for; only gated representations are absent.
        if (kind != "udp")
        {
            await Assert.That(representation).IsNull();
        }
    }

    [Test]
    public async Task ADisplayWatchOfAnotherFieldDoesNotBuildTheTextOfThisField()
    {
        using Stack stack = _Standard();
        FieldId container = stack.GetFieldId("udp")!.Value;
        FieldId other = stack.GetFieldId("udp.srcport")!.Value;

        (string? text, string? _) = _FromObserver(stack, _Bytes("udp"), container, FieldWatch.Only(stack, other));

        await Assert.That(text).IsNull();
    }

    #endregion

    #region Recorder

    /// <summary>Keeps the text structs of one field and evaluates them in <see cref="EndPacket"/>, as the contract says.</summary>
    private sealed class TextRecorder(FieldId watched) : IFieldObserver
    {
        private LazyString _Text;
        private LazyString _Representation;

        public string? Text { get; private set; }

        public string? Representation { get; private set; }

        public void BeginPacket(PacketId id, Timestamp timestamp)
        {
            _Text = default;
            _Representation = default;
        }

        public void OnField(in FieldVisit visit)
        {
            if (visit.FieldId == watched)
            {
                // Keep the structs; do not evaluate: a dissector may complete a deferred text after the append.
                _Text = visit.CustomText;
                _Representation = visit.Value.CustomRepresentation;
            }
        }

        public void EndPacket(PacketId id, bool complete)
        {
            Text = _Text.IsNull ? null : _Text.AsString;
            Representation = _Representation.IsNull ? null : _Representation.AsString;
        }
    }

    #endregion
}
