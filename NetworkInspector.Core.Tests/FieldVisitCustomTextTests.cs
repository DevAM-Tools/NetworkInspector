// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// <see cref="FieldVisit.Value"/> and <see cref="FieldVisit.CustomText"/> refer to the storage of the field in a parse with tree,
/// and a deferred text that an observer keeps as a copy and builds at the end of the packet is the string the tree reads later
/// (the deferred object builds it once for every copy).
/// </summary>
internal sealed class FieldVisitCustomTextTests
{
    #region Helpers

    /// <summary>Builds the standard protocol stack.</summary>
    /// <returns>A stack with every standard protocol registered; the caller disposes it.</returns>
    private static Stack _Standard()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        return builder.Build();
    }

    /// <summary>Parses a static UDP frame with one observer that watches <paramref name="fieldName"/>.</summary>
    /// <param name="stack">The stack to parse with.</param>
    /// <param name="mode">Whether the parse keeps a field tree.</param>
    /// <param name="fieldName">Name of the only field the observer sees.</param>
    /// <returns>The parsed packet (null when the parse failed) and what the observer recorded.</returns>
    private static (Packet? Packet, Recorder Recorder) _Parse(Stack stack, FieldTreeMode mode, string fieldName)
    {
        FieldId id = stack.GetFieldId(fieldName)!.Value;
        Recorder recorder = new();
        ParseObserver[] entries = [new() { Observer = recorder, Watch = FieldWatch.Only(stack, id), DisplayText = FieldWatch.Only(stack, id) }];
        ParseOptions options = new(mode, observers: entries);
        Frame frame = Frame.Create(
            0,
            Timestamp.FromSecs(0),
            FrameBuilders.GenerateStaticUdpIpv6Frame(),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        _ = Packet.TryParse(0, stack, frame, in options, out Packet? packet, out _);
        return (packet, recorder);
    }

    #endregion

    #region Tests

    [Test]
    public async Task ACopyKeptByTheObserverBuildsTheStringTheTreeReadsLater()
    {
        using Stack stack = _Standard();

        (Packet? packet, Recorder recorder) = _Parse(stack, FieldTreeMode.Build, "udp");
        FieldId id = stack.GetFieldId("udp")!.Value;
        string? fromTree = null;
        foreach (Field field in packet!.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id)
            {
                fromTree = field.CustomText.AsString;
                break;
            }
        }

        // A deferred text is the precondition: an already built text would pass without proving the shared build.
        await Assert.That(recorder.WasDeferred).IsTrue();
        await Assert.That(recorder.Evaluated).IsNotNull();
        await Assert.That(fromTree).IsNotNull();
        await Assert.That(ReferenceEquals(recorder.Evaluated, fromTree)).IsTrue();
    }

    [Test]
    public async Task AVisitOfAParseWithTreeRefersToTheStorageOfTheField()
    {
        using Stack stack = _Standard();

        (Packet? packet, Recorder recorder) = _Parse(stack, FieldTreeMode.Build, "udp");

        await Assert.That(packet).IsNotNull();
        await Assert.That(recorder.HasField).IsTrue();
        await Assert.That(recorder.TextIsFieldStorage).IsTrue();
        await Assert.That(recorder.ValueIsFieldStorage).IsTrue();
    }

    [Test]
    public async Task ATreeFreeParseDeliversTheSameTextWithoutACursor()
    {
        using Stack stack = _Standard();

        (Packet? withTree, Recorder treeRecorder) = _Parse(stack, FieldTreeMode.Build, "udp");
        (Packet? withoutTree, Recorder skipRecorder) = _Parse(stack, FieldTreeMode.Skip, "udp");

        await Assert.That(withTree).IsNotNull();
        await Assert.That(withoutTree).IsNotNull();
        await Assert.That(treeRecorder.HasField).IsTrue();
        await Assert.That(skipRecorder.HasField).IsFalse();
        // No slot exists without a tree: the visit refers to the append argument and the text is the same.
        await Assert.That(skipRecorder.Evaluated).IsEqualTo(treeRecorder.Evaluated);
    }

    #endregion

    #region Recorder

    /// <summary>Keeps the custom text in <see cref="OnField"/> and evaluates it in <see cref="EndPacket"/>, as the contract says.</summary>
    private sealed class Recorder : IFieldObserver
    {
        private LazyString _Kept;

        /// <summary>Whether the text was still deferred when the visit was delivered.</summary>
        public bool WasDeferred { get; private set; }

        /// <summary>The string built at the end of the packet from the kept copy; null when the field was never visited.</summary>
        public string? Evaluated { get; private set; }

        /// <summary>Whether the visit carried a tree cursor.</summary>
        public bool HasField { get; private set; }

        /// <summary>Whether the visit's text reference is the very location <see cref="Field.CustomText"/> returns.</summary>
        public bool TextIsFieldStorage { get; private set; }

        /// <summary>Whether the visit's value reference is the very location <see cref="Field.Value"/> returns.</summary>
        public bool ValueIsFieldStorage { get; private set; }

        /// <inheritdoc/>
        public void BeginPacket(PacketId id, Timestamp timestamp)
        {
            _Kept = default;
        }

        /// <inheritdoc/>
        public void OnField(in FieldVisit visit)
        {
            _Kept = visit.CustomText;
            WasDeferred = _Kept.IsLazy;
            HasField = visit.HasField;
            if (visit.HasField)
            {
                TextIsFieldStorage = Unsafe.AreSame(in visit.CustomText, in visit.Field.CustomText);
                ValueIsFieldStorage = Unsafe.AreSame(in visit.Value, in visit.Field.Value);
            }
        }

        /// <inheritdoc/>
        public void EndPacket(PacketId id, bool complete)
        {
            Evaluated = _Kept.IsNull ? null : _Kept.AsString;
        }
    }

    #endregion
}
