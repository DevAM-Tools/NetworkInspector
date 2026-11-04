// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the text-part ladders of <see cref="MutField"/> that were completed so that append, prepend, and insert-after offer
/// the same variants: the nine-part plain overloads, and the lazy overloads of prepend and insert-after with one, two, and nine
/// parts. Each test checks where the field lands, what custom text it carries, that the populator runs on first child access,
/// and that nothing is formatted when nobody reads the text.
/// </summary>
internal sealed class MutFieldCustomTextLadderTests
{
    /// <summary>The nine parts joined without separators: strings, integers, a double (invariant culture), a char, and a bool.</summary>
    private const string _NineParts = "a12.5cTrue7z89";

    private sealed class ThrowingPart
    {
        public override string ToString() => throw new InvalidOperationException("The text parts must not be formatted when nobody reads the text.");
    }

    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    private static Packet _NewPacket(Stack stack, FieldTreeMode mode)
    {
        Frame frame = Frame.Create(
            1,
            Timestamp.FromSecs(1),
            FrameBuilders.GenerateStaticUdpFrame(64),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        return new Packet(0, stack, frame, mode);
    }

    /// <summary>The custom texts of the children of the root, in order; a child without text shows as an empty string.</summary>
    private static List<string> _ChildTexts(Packet packet)
    {
        List<string> texts = [];
        foreach (Field child in packet.RootField().Children(materialize: false))
        {
            texts.Add(child.TryGetCustomText(out LazyString text)
                ? text.AsString
                : string.Empty);
        }

        return texts;
    }

    private static ParseResult _Populate(in MutField container)
    {
        _ = container.Append(container.Packet.Stack.GetFieldId("packet.info")!.Value, FieldValue.NewString("populated"));
        return 0;
    }

    /// <summary>Plain nine-part overloads: append lands last, prepend lands first, insert-after lands right after its anchor; all carry the joined text.</summary>
    [Test]
    public async Task TheNinePartPlainOverloadsPlaceTheFieldAndJoinTheText()
    {
        using Stack stack = _BuildStack();
        FieldId info = stack.GetFieldId("packet.info")!.Value;
        Packet packet = _NewPacket(stack, FieldTreeMode.Build);
        MutField root = packet.RootFieldMut();
        MutField anchor = root.AppendWithCustomText(info, FieldValue.NewString("anchor"), new LazyString("anchor"));
        _ = root.AppendWithCustomText(info, FieldValue.NewString("tail"), new LazyString("tail"));

        _ = root.AppendWithCustomText(info, FieldValue.NewString("x"), "a", 1, 2.5, 'c', true, 7L, "z", 8UL, 9);
        _ = root.PrependWithCustomText(info, FieldValue.NewString("x"), "a", 1, 2.5, 'c', true, 7L, "z", 8UL, 9);
        _ = anchor.InsertAfterWithCustomText(info, FieldValue.NewString("x"), "a", 1, 2.5, 'c', true, 7L, "z", 8UL, 9);
        List<string> texts = _ChildTexts(packet);

        await Assert.That(texts.Count).IsEqualTo(5);
        await Assert.That(texts[0]).IsEqualTo(_NineParts);
        await Assert.That(texts[1]).IsEqualTo("anchor");
        await Assert.That(texts[2]).IsEqualTo(_NineParts);
        await Assert.That(texts[3]).IsEqualTo("tail");
        await Assert.That(texts[4]).IsEqualTo(_NineParts);
    }

    /// <summary>
    /// Lazy overloads of prepend and insert-after with 1, 2, and 9 parts: the container lands first (prepend) or right after its
    /// anchor (insert-after), carries the joined text, and its populator adds a child on first access.
    /// </summary>
    [Test]
    public async Task TheLazyOverloadsOfPrependAndInsertAfterPlaceTheContainerAndJoinTheText()
    {
        using Stack stack = _BuildStack();
        FieldId info = stack.GetFieldId("packet.info")!.Value;
        FieldId container = stack.GetFieldId("packet")!.Value;
        Packet packet = _NewPacket(stack, FieldTreeMode.Build);
        MutField root = packet.RootFieldMut();
        MutField anchor = root.AppendWithCustomText(info, FieldValue.NewString("anchor"), new LazyString("anchor"));
        _ = root.AppendWithCustomText(info, FieldValue.NewString("tail"), new LazyString("tail"));

        MutField prepended1 = root.PrependLazyWithCustomText(container, FieldValue.None, "p", _Populate);
        MutField prepended2 = root.PrependLazyWithCustomText(container, FieldValue.None, "p", 2, _Populate);
        _ = root.PrependLazyWithCustomText(container, FieldValue.None, "a", 1, 2.5, 'c', true, 7L, "z", 8UL, 9, _Populate);
        _ = anchor.InsertAfterLazyWithCustomText(container, FieldValue.None, "i", _Populate);
        _ = anchor.InsertAfterLazyWithCustomText(container, FieldValue.None, "i", 2, _Populate);
        _ = anchor.InsertAfterLazyWithCustomText(container, FieldValue.None, "a", 1, 2.5, 'c', true, 7L, "z", 8UL, 9, _Populate);
        bool materialized = prepended1.MaterializeIfLazy();
        bool childAdded = prepended1.HasChildren(materialize: false) && prepended2.HasChildren(materialize: false) == false;
        List<string> texts = _ChildTexts(packet);

        // Order: each prepend goes in front of the earlier ones; each insert-after goes right after the anchor, in front of the earlier ones.
        await Assert.That(texts.Count).IsEqualTo(8);
        await Assert.That(texts[0]).IsEqualTo(_NineParts);
        await Assert.That(texts[1]).IsEqualTo("p2");
        await Assert.That(texts[2]).IsEqualTo("p");
        await Assert.That(texts[3]).IsEqualTo("anchor");
        await Assert.That(texts[4]).IsEqualTo(_NineParts);
        await Assert.That(texts[5]).IsEqualTo("i2");
        await Assert.That(texts[6]).IsEqualTo("i");
        await Assert.That(texts[7]).IsEqualTo("tail");
        await Assert.That(materialized).IsTrue();
        await Assert.That(childAdded).IsTrue();
    }

    /// <summary>
    /// A parse without field tree and without a display-text cache reads no custom text: the new overloads append the field without
    /// text and never format a part (a part whose <c>ToString</c> throws is never called).
    /// </summary>
    [Test]
    public async Task NoPartIsFormattedWhenNobodyReadsTheText()
    {
        using Stack stack = _BuildStack();
        FieldId info = stack.GetFieldId("packet.info")!.Value;
        FieldId container = stack.GetFieldId("packet")!.Value;
        Packet packet = _NewPacket(stack, FieldTreeMode.Skip);
        MutField root = packet.RootFieldMut();
        ThrowingPart part = new();
        MutField anchor = root.Append(info, FieldValue.None);
        bool wanted = root.WantsCustomText(info);

        _ = root.AppendWithCustomText(info, FieldValue.None, part, part, part, part, part, part, part, part, part);
        _ = root.PrependWithCustomText(info, FieldValue.None, part, part, part, part, part, part, part, part, part);
        _ = anchor.InsertAfterWithCustomText(info, FieldValue.None, part, part, part, part, part, part, part, part, part);
        _ = root.PrependLazyWithCustomText(container, FieldValue.None, part, part, _Populate);
        _ = anchor.InsertAfterLazyWithCustomText(container, FieldValue.None, part, part, part, part, part, part, part, part, part, _Populate);

        await Assert.That(wanted).IsFalse();
    }
}
