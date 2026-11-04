// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SomeIp;

/// <summary>
/// The texts of SOME/IP arrays and unions are final when the node is appended: the decoder never replaces them later. These
/// tests read the texts from the field tree, including the edges (zero elements, the element prefix inside arrays, the empty
/// union). Together with the unchanged payload tests they prove that moving the decisions in front of the append changed no text
/// a user sees, except that the empty union inside an array now keeps its element prefix.
/// </summary>
internal sealed class SomeIpCustomTextTests : SomeIpPayloadKit
{
    private const string _Choice = "{\"category\":\"union\",\"name\":\"choice_t\",\"members\":{\"1\":{\"name\":\"a\",\"type_reference\":\"uint8\"}}}";

    [Test]
    [Arguments(0, "array[0]")]
    [Arguments(1, "array[1]")]
    [Arguments(3, "array[3]")]
    public async Task AVariableArrayShowsItsFinalCountFromTheAppend(int count, string expected)
    {
        string methods = Method(1, "arr", Sites(Site("values", "bytes_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, ArrayType("bytes_t", "uint8")));
        SomeIpBytes bytes = new SomeIpBytes().U32((uint)count);
        for (int i = 0; i < count; i++)
        {
            bytes.U8((byte)(10 + i));
        }

        Packet packet = s.Parse(UdpFrame(bytes.ToArray()));

        await Assert.That(Texts(s, packet, InName("arr", "values"))).IsEquivalentTo(One(expected));
    }

    [Test]
    public async Task ArraysInsideAnArrayKeepTheirElementPrefix()
    {
        string types = Join(ArrayType("bytes_t", "uint8"), ArrayType("rows_t", "bytes_t"));
        string methods = Method(1, "rows", Sites(Site("values", "rows_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        // Two inner arrays: [1, 2] and [7]. The outer length counts both inner lengths and their bytes: (4 + 2) + (4 + 1).
        byte[] payload = new SomeIpBytes().U32(11).U32(2).U8(1).U8(2).U32(1).U8(7).ToArray();
        Packet packet = s.Parse(UdpFrame(payload));

        await Assert.That(Texts(s, packet, InName("rows", "values"))).IsEquivalentTo(One("array[2]"));
        await Assert.That(Texts(s, packet, InName("rows", "values.item"))).IsEquivalentTo(new List<string> { "[0] array[2]", "[1] array[1]" });
    }

    [Test]
    public async Task EmptyAndNonEmptyUnionsInsideAnArrayKeepTheirElementPrefix()
    {
        string types = Join(_Choice, ArrayType("choices_t", "choice_t"));
        string methods = Method(1, "ch", Sites(Site("values", "choices_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        // Element 0 is the empty union (length 4 = selector only), element 1 selects alternative 1 with the value 9.
        byte[] payload = new SomeIpBytes().U32(8 + 9).U32(4).U32(0).U32(5).U32(1).U8(9).ToArray();
        Packet packet = s.Parse(UdpFrame(payload));

        await Assert.That(Texts(s, packet, InName("ch", "values.item")))
            .IsEquivalentTo(new List<string> { "[0] union choice_t (empty)", "[1] union choice_t" });
        await Assert.That(Errors(s, packet)).IsEmpty();
    }

    [Test]
    public async Task AUnionShorterThanItsSelectorKeepsTheNonEmptyTextAndReportsTheError()
    {
        string types = Join(_Choice);
        string methods = Method(1, "ch", Sites(Site("value", "choice_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));

        // Length 2 is smaller than the 4-byte selector: no alternative can be named, so the node is not the "empty" union.
        Packet packet = s.Parse(UdpFrame(new SomeIpBytes().U32(2).U8(0).U8(0).ToArray()));

        await Assert.That(Texts(s, packet, InName("ch", "value"))).IsEquivalentTo(One("union choice_t"));
        await Assert.That(Errors(s, packet).Count).IsEqualTo(1);
        await Assert.That(Errors(s, packet)[0]).Contains("fewer than its 4-byte type selector");
    }

    #region Observer side (tree-free parse)

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task AnObserverEvaluatesTheSameArrayTextInEndPacketAsTheTree(int count)
    {
        string methods = Method(1, "arr", Sites(Site("values", "bytes_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, ArrayType("bytes_t", "uint8")));
        SomeIpBytes bytes = new SomeIpBytes().U32((uint)count);
        for (int i = 0; i < count; i++)
        {
            bytes.U8((byte)(10 + i));
        }

        byte[] frame = UdpFrame(bytes.ToArray());
        Packet tree = s.Parse(frame);
        string name = InName("arr", "values");

        List<string> observed = _ObserveTexts(s, frame, packetId: 0, name);

        await Assert.That(observed).IsEquivalentTo(Texts(s, tree, name));
        await Assert.That(observed).IsEquivalentTo(One(string.Create(CultureInfo.InvariantCulture, $"array[{count}]")));
    }

    [Test]
    public async Task AnObserverEvaluatesTheSameUnionTextsInEndPacketAsTheTree()
    {
        string types = Join(_Choice, ArrayType("choices_t", "choice_t"));
        string methods = Method(1, "ch", Sites(Site("values", "choices_t")), "[]");
        using SomeIpTestStack s = SomeIpTestStack.Create(Json(methods, types));
        byte[] frame = UdpFrame(new SomeIpBytes().U32(8 + 9).U32(4).U32(0).U32(5).U32(1).U8(9).ToArray());
        Packet tree = s.Parse(frame);
        string name = InName("ch", "values.item");

        List<string> observed = _ObserveTexts(s, frame, packetId: 0, name);

        await Assert.That(observed).IsEquivalentTo(Texts(s, tree, name));
        await Assert.That(observed).IsEquivalentTo(new List<string> { "[0] union choice_t (empty)", "[1] union choice_t" });
    }

    /// <summary>
    /// Replays the packet <paramref name="packetId"/> without field tree and returns the texts of every field named
    /// <paramref name="fieldName"/>, evaluated in <c>EndPacket</c> as the observer contract says.
    /// </summary>
    private static List<string> _ObserveTexts(SomeIpTestStack s, byte[] frameBytes, int packetId, string fieldName)
    {
        FieldId id = s.Stack.GetFieldId(fieldName)!.Value;
        TextCollector collector = new(id);
        ParseObserver[] entries =
        [
            new() { Observer = collector, Watch = FieldWatch.ForExtraction(s.Stack, id), DisplayText = FieldWatch.Only(s.Stack, id) },
        ];
        ParseOptions options = new(FieldTreeMode.Skip, observers: entries);
        Frame frame = Frame.Create(
            packetId,
            Timestamp.FromMillis(packetId),
            frameBytes,
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            s.Stack.FrameInterfaceRegistry).Value;
        _ = Packet.TryParse(packetId, s.Stack, frame, in options, out _, out _);
        return collector.Texts;
    }

    /// <summary>Keeps the text structs of one field and evaluates them when the packet ends.</summary>
    private sealed class TextCollector(FieldId watched) : IFieldObserver
    {
        private readonly List<LazyString> _Kept = [];

        public List<string> Texts { get; } = [];

        public void BeginPacket(PacketId id, Timestamp timestamp) => _Kept.Clear();

        public void OnField(in FieldVisit visit)
        {
            if (visit.FieldId == watched)
            {
                _Kept.Add(visit.CustomText);
            }
        }

        public void EndPacket(PacketId id, bool complete)
        {
            foreach (LazyString kept in _Kept)
            {
                Texts.Add(kept.IsNull ? "<none>" : kept.AsString);
            }
        }
    }

    #endregion
}
