// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The three ways to read field values from a parse, each as the smallest complete example: the field tree, the value cache, and a
/// field observer. The bodies of these tests are the samples of <c>NetworkInspector.Core/README.md</c> ("Three Ways To Read Field
/// Values"); they are kept as tests so that a change of the API breaks the build before it breaks the documentation.
/// </summary>
internal sealed class CoreApiSampleTests
{
    #region Helpers

    /// <summary>Ethernet II / IPv4 192.168.1.1 → 192.168.1.2 / UDP 12345 → 53, no payload (42 bytes).</summary>
    private const string _UdpFrameHex =
        "001122334455" + "66778899AABB" + "0800" + "4500001C" + "12344000" + "4011A549" + "C0A80101" + "C0A80102" + "30390035" + "00080000";

    private static Stack _Stack()
    {
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        return builder.Build();
    }

    private static Frame _Frame(Stack stack, int id) =>
        Frame.Create(
            id,
            Timestamp.FromSecs(1_700_000_000 + id),
            Convert.FromHexString(_UdpFrameHex),
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;

    /// <summary>Collects the one field it wants; a real observer would stage it for a file, a table, or a metric.</summary>
    private sealed class PortRecorder(FieldId wanted) : IFieldObserver
    {
        internal List<string> Lines { get; } = [];

        public void BeginPacket(PacketId id, Timestamp timestamp) => Lines.Add($"packet {id.Value}");

        public void OnField(in FieldVisit visit)
        {
            // ForExtraction also delivers the other fields of the owning protocol; the observer keeps only its own.
            if (visit.FieldId == wanted)
            {
                Lines.Add($"{visit.FieldId.Value}={visit.Value}");
            }
        }

        public void EndPacket(PacketId id, bool complete) => Lines.Add($"end {id.Value} complete={complete}");
    }

    #endregion

    [Test]
    public async Task TheFieldTreeAnswersLookupsForOnePacket()
    {
        using Stack stack = _Stack();
        FieldId dstPort = stack.GetFieldId("udp.dstport")!.Value;

        // Parse with the default options: the packet keeps a field tree.
        ParseOptions options = new();
        if (!Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        // One value by id (the first occurrence). `materialize: true` expands a lazy container that may hold the field.
        bool found = packet.TryGetFieldValue(dstPort, out FieldValue value, materialize: true);

        // Every occurrence of a field that can repeat (tunnels, VLAN tags): step through them with the cookie.
        List<string> occurrences = [];
        FieldLookupCookie cookie = FieldLookupCookie.Start;
        while (packet.TryGetNextField(dstPort, ref cookie, out Field field, materialize: true))
        {
            // The default display line of the field (label and value, or its custom text alone); the parts stay readable
            // for an own line: field.FieldInfo.UiName, field.Value, field.CustomText.
            occurrences.Add(field.ToString());
        }

        // The whole tree, depth first.
        int count = packet.IterFieldsDfs(materialize: true).Count();

        await Assert.That(found).IsTrue();
        await Assert.That(value.ToString()).IsEqualTo("53");
        await Assert.That(string.Join(",", occurrences)).IsEqualTo("Destination Port: 53");
        await Assert.That(count).IsGreaterThan(5);
    }

    [Test]
    public async Task TheValueCacheKeepsOneFieldOfManyPacketsAsAColumn()
    {
        using Stack stack = _Stack();
        FieldId dstPort = stack.GetFieldId("udp.dstport")!.Value;

        // The cache records the listed fields while packets are parsed; the parse needs no field tree.
        ValueCache cache = new(stack, [new ValueCacheFieldConfig(dstPort, ValueCaptureMode.FirstOccurrence)]);
        ParseOptions options = new(FieldTreeMode.Skip, cache);
        for (int i = 0; i < 3; i++)
        {
            _ = Packet.TryParse(i, stack, _Frame(stack, i), in options, out _, out _);
        }

        // Read the column through a snapshot handle: its Count and its rows do not change while you read.
        ValueCacheSeriesHandle<ulong> handle = cache.GetSeries<ulong>(dstPort).Handle;
        List<string> rows = [];
        foreach (ValueCacheRow<ulong> row in handle)
        {
            rows.Add($"{handle.Keys.Count}:{row.Value}");
        }

        await Assert.That(handle.Count).IsEqualTo(3);
        await Assert.That(string.Join(",", rows)).IsEqualTo("3:53,3:53,3:53");
    }

    [Test]
    public async Task AFieldObserverSeesTheWatchedFieldsWhileTheParseRunsWithoutATree()
    {
        using Stack stack = _Stack();
        FieldId dstPort = stack.GetFieldId("udp.dstport")!.Value;

        // The observer says what to do with each field, the watch says which fields it gets. `ForExtraction` is the watch for a
        // consumer that must see its fields even when they sit in a lazily decoded container.
        PortRecorder recorder = new(dstPort);
        ParseObserver[] observers = [new() { Observer = recorder, Watch = FieldWatch.ForExtraction(stack, dstPort) }];
        ParseOptions options = new(FieldTreeMode.Skip, observers: observers);

        bool parsed = Packet.TryParse(0, stack, _Frame(stack, 0), in options, out Packet? packet, out _);

        await Assert.That(parsed).IsTrue();
        await Assert.That(packet!.HasFieldTree).IsFalse();
        await Assert.That(string.Join(";", recorder.Lines)).IsEqualTo($"packet 0;{dstPort.Value}=53;end 0 complete=True");
    }
}
