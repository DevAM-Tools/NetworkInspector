// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Compares two packets field-by-field for ingest/redissect identity tests.
/// </summary>
internal static class PacketFieldComparer
{
    /// <summary>
    /// Immutable copy of a fully materialized packet's fields.
    /// <para>
    /// Needed for concurrency tests: materialization mutates a packet in place, so several threads
    /// must never materialize the same reference packet. One thread captures the snapshot, all
    /// readers then compare against the snapshot instead of against the shared packet.
    /// </para>
    /// </summary>
    internal sealed record PacketFieldSnapshot(int FieldCount, (FieldId Id, FieldValue Value)[] Fields);

    /// <summary>
    /// Materializes <paramref name="packet"/> and captures its fields. Must be called from the single
    /// thread that owns the packet, before any other thread compares against the result.
    /// </summary>
    internal static PacketFieldSnapshot CaptureFields(Packet packet)
    {
        packet.MaterializeAll();

        int count = packet.FieldCount(materialize: true);
        List<(FieldId Id, FieldValue Value)> fields = new(count);
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            // Lazy texts stay unevaluated: the readers race on their first evaluation, which the in-repository LazyString makes safe (compare-exchange publication).
            fields.Add((field.FieldId, field.Value));
        }

        return new PacketFieldSnapshot(count, [.. fields]);
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> matches a snapshot captured via
    /// <see cref="CaptureFields"/>. Touches only <paramref name="actual"/>, so it is safe to run on
    /// many threads in parallel as long as each thread owns its own <paramref name="actual"/>.
    /// </summary>
    internal static async Task AssertMatchesSnapshot(Stack stack, PacketFieldSnapshot expected, Packet actual)
    {
        actual.MaterializeAll();

        await Assert.That(actual.FieldCount(materialize: true)).IsEqualTo(expected.FieldCount);

        // Compared position by position, not by looking the field id up: some fields occur several times in one packet
        // (one tcp.segment per contributing packet, one SACK edge per block), and a lookup by id would always return the
        // first occurrence and report a false mismatch for the others. Two dissects of the same bytes must also produce
        // the fields in the same order, which this comparison checks as well.
        List<(FieldId Id, FieldValue Value)> actualFields = _CollectFields(actual);
        for (int index = 0; index < actualFields.Count; index++)
        {
            (FieldId Id, FieldValue Value) expectedField = expected.Fields[index];
            FieldInfo? info = stack.GetField(expectedField.Id);
            string name = info?.Name ?? expectedField.Id.ToString();

            await Assert.That(actualFields[index].Id).IsEqualTo(expectedField.Id).Because($"Re-parsed packet field #{index} is not '{name}'");
            await Assert.That(_ValuesEqual(expectedField.Value, actualFields[index].Value))
                .IsTrue()
                .Because($"Field '{name}' value mismatch");
        }
    }

    /// <summary>
    /// Collects the fields of a packet in depth-first order. A separate synchronous method because the field enumerator is a
    /// ref struct, which cannot be held across the awaits of the assertions.
    /// </summary>
    private static List<(FieldId Id, FieldValue Value)> _CollectFields(Packet packet)
    {
        List<(FieldId Id, FieldValue Value)> fields = [];
        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            fields.Add((field.FieldId, field.Value));
        }

        return fields;
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> has the same materialized field values as
    /// <paramref name="expected"/> for every field name registered on <paramref name="stack"/>.
    /// </summary>
    internal static async Task AssertFieldIdentical(Stack stack, Packet expected, Packet actual)
    {
        actual.MaterializeAll();
        expected.MaterializeAll();

        await Assert.That(actual.FieldCount(materialize: true))
            .IsEqualTo(expected.FieldCount(materialize: true));

        // Positional comparison, see AssertMatchesSnapshot: repeated fields must be compared occurrence by occurrence.
        List<(FieldId Id, FieldValue Value)> expectedFields = _CollectFields(expected);
        List<(FieldId Id, FieldValue Value)> actualFields = _CollectFields(actual);
        for (int index = 0; index < actualFields.Count; index++)
        {
            (FieldId Id, FieldValue Value) expectedField = expectedFields[index];
            FieldInfo? info = stack.GetField(expectedField.Id);
            string name = info?.Name ?? expectedField.Id.ToString();

            await Assert.That(actualFields[index].Id).IsEqualTo(expectedField.Id).Because($"Redissect packet field #{index} is not '{name}'");
            await Assert.That(_ValuesEqual(expectedField.Value, actualFields[index].Value))
                .IsTrue()
                .Because($"Field '{name}' value mismatch");
        }
    }

    private static bool _ValuesEqual(FieldValue left, FieldValue right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        return left.Type switch
        {
            FieldType.None => true,
            FieldType.Bool => left.Data.TryGetAsBool(out bool lb) && right.Data.TryGetAsBool(out bool rb) && lb == rb,
            FieldType.U64 => left.Data.TryGetAsU64(out ulong lu) && right.Data.TryGetAsU64(out ulong ru) && lu == ru,
            FieldType.I64 => left.Data.TryGetAsI64(out long li) && right.Data.TryGetAsI64(out long ri) && li == ri,
            FieldType.F64 => left.Data.TryGetAsF64(out double lf) && right.Data.TryGetAsF64(out double rf) && lf == rf,
            FieldType.String => left.Data.TryGetAsString(out string? ls) && right.Data.TryGetAsString(out string? rs)
                && string.Equals(ls, rs, StringComparison.Ordinal),
            FieldType.Bytes => left.Data.TryGetAsBytes(out ReadOnlyMemory<byte> lb)
                && right.Data.TryGetAsBytes(out ReadOnlyMemory<byte> rb)
                && lb.Span.SequenceEqual(rb.Span),
            _ => left.ToString() == right.ToString(),
        };
    }
}
