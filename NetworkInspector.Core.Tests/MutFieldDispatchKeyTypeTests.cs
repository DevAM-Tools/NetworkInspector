// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for the key-type check of the keyed dispatch methods of <see cref="MutField"/>: a call whose key type differs from the
/// table's key type returns an error that names the table and both key types (it used to read as "no protocol for this key"),
/// a call of the matching key type still hits and misses as before, and the check adds no allocation on the miss path.
/// A probe protocol calls every method on every table and records what came back.
/// </summary>
internal sealed class MutFieldDispatchKeyTypeTests
{
    /// <summary>One wrong-key-type call and what came back.</summary>
    private readonly record struct Outcome(string Method, string TableName, bool IsKeyTypeError, string Message);

    private sealed class ChildProtocol : IProtocol
    {
        public string Name => "kt.child";

        public string UiName => "Key Type Child";

        public int Calls { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            Calls++;
            return 0;
        }
    }

    private sealed class ProbeProtocol(
        ProtocolTableId u64Table,
        ProtocolTableId stringTable,
        ProtocolTableId bytesTable,
        ProtocolTableId boolTable,
        ProtocolTableId anyTable) : IProtocol
    {
        public string Name => "kt.probe";

        public string UiName => "Key Type Probe";

        public List<Outcome> Wrong { get; } = [];

        public long AllocatedOnMissPath { get; private set; } = -1;

        public long AllocatedOnHitPath { get; private set; } = -1;

        public bool MatchingHits { get; private set; }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            BytesKey key = new([0xAB]);

            // Every method against a table of another key type.
            _Record("U64 on String", parentField.TryCallNextProtocolU64(stringTable, 7, data, in context), "kt.string");
            _Record("U64 on Bytes", parentField.TryCallNextProtocolU64(bytesTable, 7, data, in context), "kt.bytes");
            _Record("U64 on Bool", parentField.TryCallNextProtocolU64(boolTable, 7, data, in context), "kt.bool");
            _Record("U64 on Any", parentField.TryCallNextProtocolU64(anyTable, 7, data, in context), "kt.any");
            _Record("String on U64", parentField.TryCallNextProtocolString(u64Table, "hit", data, in context), "kt.u64");
            _Record("Bytes on U64", parentField.TryCallNextProtocolBytes(u64Table, key, data, in context), "kt.u64");
            _Record("Bool on U64", parentField.TryCallNextProtocolBool(u64Table, true, data, in context), "kt.u64");
            _Record("Any on U64", parentField.TryCallNextProtocolAny(u64Table, data, in context), "kt.u64");
            _Record("String on Any", parentField.TryCallNextProtocolString(anyTable, "hit", data, in context), "kt.any");
            _Record("Any on String", parentField.TryCallNextProtocolAny(stringTable, data, in context), "kt.string");

            // The matching key type: a registered key hits, an unknown key is a miss.
            MatchingHits = parentField.TryCallNextProtocolU64(u64Table, 7, data, in context).TryGetConsumed(out _)
                && parentField.TryCallNextProtocolU64(u64Table, 99, data, in context).IsNotDispatched
                && parentField.TryCallNextProtocolString(stringTable, "hit", data, in context).TryGetConsumed(out _)
                && parentField.TryCallNextProtocolString(stringTable, "miss", data, in context).IsNotDispatched
                && parentField.TryCallNextProtocolBytes(bytesTable, key, data, in context).TryGetConsumed(out _)
                && parentField.TryCallNextProtocolBool(boolTable, true, data, in context).TryGetConsumed(out _)
                && parentField.TryCallNextProtocolBool(boolTable, false, data, in context).IsNotDispatched
                && parentField.TryCallNextProtocolAny(anyTable, data, in context).TryGetConsumed(out _);

            // Allocation of the correct-key miss path (the new comparison runs here, no callee runs): warm up, then count.
            _ = parentField.TryCallNextProtocolU64(u64Table, 99, data, in context);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
            {
                _ = parentField.TryCallNextProtocolU64(u64Table, 99, data, in context);
                _ = parentField.TryCallNextProtocolString(stringTable, "miss", data, in context);
            }

            AllocatedOnMissPath = GC.GetAllocatedBytesForCurrentThread() - before;

            // The hit path of the matching key type (the callee allocates nothing either): warm up, then count.
            _ = parentField.TryCallNextProtocolU64(u64Table, 7, data, in context);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
            {
                _ = parentField.TryCallNextProtocolU64(u64Table, 7, data, in context);
                _ = parentField.TryCallNextProtocolString(stringTable, "hit", data, in context);
            }

            AllocatedOnHitPath = GC.GetAllocatedBytesForCurrentThread() - before;
            return 0;
        }

        private void _Record(string method, ParseResult result, string tableName)
        {
            bool isError = result.IsError
                && result.TryGetError(out ParseError error)
                && error.Kind == ParseErrorKind.ProtocolTableMissing;
            string message = result.TryGetError(out ParseError text)
                ? text.ToString()
                : string.Empty;
            Wrong.Add(new Outcome(method, tableName, isError, message));
        }
    }

    private static ProbeProtocol _Run()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ChildProtocol child = new();
        ProtocolId childId = builder.RegisterProtocol(child);
        ProtocolTableId u64Table = builder.RegisterProtocolTable("kt.u64", "U64", ProtocolTableKeyType.U64);
        ProtocolTableId stringTable = builder.RegisterProtocolTable("kt.string", "String", ProtocolTableKeyType.String);
        ProtocolTableId bytesTable = builder.RegisterProtocolTable("kt.bytes", "Bytes", ProtocolTableKeyType.Bytes);
        ProtocolTableId boolTable = builder.RegisterProtocolTable("kt.bool", "Bool", ProtocolTableKeyType.Bool);
        ProtocolTableId anyTable = builder.RegisterProtocolTable("kt.any", "Any", ProtocolTableKeyType.Any);
        builder.RegisterParserInU64Table(u64Table, 7, childId);
        builder.RegisterParserInStringTable(stringTable, "hit", childId);
        builder.RegisterParserInBytesTable(bytesTable, new BytesKey([0xAB]), childId);
        builder.RegisterParserInBoolTable(boolTable, true, childId);
        builder.RegisterParserInAnyTable(anyTable, childId);
        ProbeProtocol probe = new(u64Table, stringTable, bytesTable, boolTable, anyTable);
        ProtocolId probeId = builder.RegisterProtocol(probe);

        using Stack stack = builder.Build();
        Frame frame = Frame.Create(
            1, Timestamp.FromSecs(0), new byte[] { 0x01 }, LinkType.Ethernet, FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(firstProtocol: probeId);
        if (!Packet.TryParse(0, stack, frame, in options, out Packet? _, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return probe;
    }

    /// <summary>
    /// Each keyed method on a table of another key type returns the table-missing error kind with a text that names the table, the
    /// key type the table has, and the key type of the method; none of them reads as <c>NotDispatched</c>.
    /// </summary>
    [Test]
    public async Task AWrongKeyTypeIsAnErrorThatNamesTheTableAndBothTypes()
    {
        ProbeProtocol probe = _Run();

        await Assert.That(probe.Wrong.Count).IsEqualTo(10);
        foreach (Outcome outcome in probe.Wrong)
        {
            await Assert.That(outcome.IsKeyTypeError).IsTrue().Because(outcome.Method);
            await Assert.That(outcome.Message).Contains(outcome.TableName).Because(outcome.Method);
            await Assert.That(outcome.Message).Contains("key type").Because(outcome.Method);
        }

        Outcome first = probe.Wrong[0];
        await Assert.That(first.Message).Contains("String").And.Contains("U64");
    }

    /// <summary>The matching key type keeps its behavior: a registered key hits the child, an unknown key is not dispatched.</summary>
    [Test]
    public async Task TheMatchingKeyTypeStillHitsAndMisses()
    {
        ProbeProtocol probe = _Run();

        await Assert.That(probe.MatchingHits).IsTrue();
    }

    /// <summary>
    /// The miss path and the hit path of the matching key type allocate nothing: the key-type check builds its error text only on
    /// a mismatch (the callee of the hit path allocates nothing either, so any allocation is the dispatch's own).
    /// </summary>
    [Test]
    public async Task TheKeyTypeCheckAddsNoAllocationOnTheMissAndHitPaths()
    {
        ProbeProtocol probe = _Run();

        await Assert.That(probe.AllocatedOnMissPath).IsEqualTo(0L);
        await Assert.That(probe.AllocatedOnHitPath).IsEqualTo(0L);
    }
}
