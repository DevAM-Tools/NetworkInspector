// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// <see cref="FieldWatch.ForExtraction"/>: a watch for consumers that must not lose a listed field. A watch made with
/// <see cref="FieldWatch.Only(IStack, FieldId)"/> expands a deferred container only when the container or a field of its index
/// group is watched, so a field inside a container outside that group is silently missing in a parse without field tree.
/// </summary>
internal sealed class FieldWatchExtractionTests
{
    [Test]
    public async Task OnlyMissesAFieldInALazyContainerOutsideAnyIndexGroupAndForExtractionFindsIt()
    {
        (Stack stack, LazyOwnerProtocol protocol, ProtocolId id) = _Build();
        using (stack)
        {
            List<ulong> viaOnly = _Observe(stack, id, FieldWatch.Only(stack, protocol.Inner));
            List<ulong> viaExtraction = _Observe(stack, id, FieldWatch.ForExtraction(stack, protocol.Inner));

            // Documents today's trap for Only, and that the extraction watch closes it.
            await Assert.That(viaOnly).IsEmpty();
            await Assert.That(viaExtraction).IsEquivalentTo(new List<ulong> { 77UL });
        }
    }

    [Test]
    public async Task ForExtractionCountsTheRequestedFieldsNotTheWidenedOnes()
    {
        (Stack stack, LazyOwnerProtocol protocol, ProtocolId _) = _Build();
        using (stack)
        {
            FieldWatch extraction = FieldWatch.ForExtraction(stack, protocol.Inner);

            HashSet<int> requested = [];
            extraction.CollectRequested(requested);
            HashSet<int> none = [];
            FieldWatch.None.CollectRequested(none);
            HashSet<int> all = [];
            FieldWatch.All.CollectRequested(all);

            await Assert.That(requested.Count).IsEqualTo(1);
            await Assert.That(none.Count).IsEqualTo(0);
            await Assert.That(all.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ForExtractionRejectsAnEmptyListAndAnIdOfAnotherStack()
    {
        (Stack stack, LazyOwnerProtocol _, ProtocolId _) = _Build();
        using (stack)
        {
            FieldId foreign = new(stack.FieldCount + 5);

            await Assert.That(() => FieldWatch.ForExtraction(stack, ReadOnlySpan<FieldId>.Empty)).Throws<ArgumentException>();
            await Assert.That(() => FieldWatch.ForExtraction(stack, foreign)).Throws<ArgumentException>();
        }
    }

    private static (Stack Stack, LazyOwnerProtocol Protocol, ProtocolId Id) _Build()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        LazyOwnerProtocol protocol = new();
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, id);
        return (builder.Build(), protocol, id);
    }

    private static List<ulong> _Observe(Stack stack, ProtocolId first, FieldWatch watch)
    {
        ListWatch recorder = new();
        ParseObserver[] entries = [new() { Observer = recorder, Watch = watch }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: entries, firstProtocol: first);
        Frame frame = Frame.Create(
            0, Timestamp.FromSecs(0), new byte[16], LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        _ = Packet.TryParse(0, stack, frame, in options, out _, out _);
        return [.. recorder.U64.Where(static value => value != 0)]; // the container reports 0 (no U64 value)
    }

    /// <summary>Owns a lazy container (no index group) with one child; the child is what the watches ask for.</summary>
    private sealed class LazyOwnerProtocol : IProtocol
    {
        private FieldId _Container;

        public FieldId Inner { get; private set; }

        public string Name => "lazyowner";

        public string UiName => "Lazy owner";

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _Container = builder.RegisterField(protocolId, "lazyowner", "Lazy owner", FieldType.None);
            Inner = builder.RegisterField(protocolId, "lazyowner.inner", "Inner", FieldType.U64);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldId inner = Inner;
            _ = parentField.AppendLazy(_Container, FieldValue.None, (in MutField container) =>
            {
                _ = container.Append(inner, FieldValue.NewU64(77));
                return 0;
            });
            return data.Length;
        }
    }
}
