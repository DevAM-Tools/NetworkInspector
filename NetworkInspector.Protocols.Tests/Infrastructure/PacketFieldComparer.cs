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

        foreach ((FieldId Id, FieldValue Value) expectedField in expected.Fields)
        {
            FieldInfo? info = stack.GetField(expectedField.Id);
            string name = info?.Name ?? expectedField.Id.ToString();

            bool found = actual.TryGetFieldValue(expectedField.Id, out FieldValue actualValue, materialize: true);
            await Assert.That(found).IsTrue().Because($"Re-parsed packet missing field '{name}'");

            await Assert.That(_ValuesEqual(expectedField.Value, actualValue))
                .IsTrue()
                .Because($"Field '{name}' value mismatch");
        }
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

        List<(FieldId Id, FieldValue Value)> expectedFields = [];
        foreach (Field field in expected.IterFieldsDfs(materialize: true))
        {
            expectedFields.Add((field.FieldId, field.Value));
        }

        foreach ((FieldId Id, FieldValue Value) expectedField in expectedFields)
        {
            FieldInfo? info = stack.GetField(expectedField.Id);
            string name = info?.Name ?? expectedField.Id.ToString();

            bool found = actual.TryGetFieldValue(expectedField.Id, out FieldValue actualValue, materialize: true);
            await Assert.That(found).IsTrue().Because($"Redissect packet missing field '{name}'");

            await Assert.That(_ValuesEqual(expectedField.Value, actualValue))
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
