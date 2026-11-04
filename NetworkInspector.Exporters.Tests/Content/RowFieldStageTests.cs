// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// <see cref="RowFieldStage"/>: fields are staged during the parse and become part of the output only when the exporter
/// commits the same, completely received packet. Everything else — a packet the filter rejected, a muted observer, a replay, a
/// mismatched id — is dropped or refused with a message that says why.
/// </summary>
internal sealed class RowFieldStageTests
{
    #region Staging fixtures

    /// <summary>The unsigned value (53) that most tests stage as a field value.</summary>
    private static readonly FieldValueData _Number = FieldValue.NewU64(53).Data;

    /// <summary>A parsed Ethernet, IPv4, UDP packet with the given id.</summary>
    /// <param name="id">The packet id.</param>
    /// <returns>The packet.</returns>
    private static Packet _Packet(int id) =>
        PacketGenerators.CreateParsedPacket(id, FrameGenerators.BuildEthernetIpv4UdpFrame(4));

    #endregion

    #region Committing and refusing

    /// <summary>Proves that a closed staging is committed once for its own packet, exposes its entries, and refuses a second commit.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AClosedStagingIsCommittedOnceForItsOwnPacketAndExposesItsEntries()
    {
        using RowFieldStage stage = new();
        Packet packet = _Packet(0);

        stage.BeginPacket(packet.Id.Value, 5);
        stage.AddValue(7, 0, in _Number, new LazyString("repr"), new LazyString("text"));
        stage.EndPacket(complete: true);
        bool first = stage.TryCommit(packet, out _, out string? message);
        bool second = stage.TryCommit(packet, out _, out string? secondMessage);

        await Assert.That(first).IsTrue();
        await Assert.That(message).IsNull();
        await Assert.That(stage.Entries.Length).IsEqualTo(1);
        await Assert.That(stage.Entries[0].FieldId).IsEqualTo(7);
        await Assert.That(stage.Entries[0].Representation).IsEqualTo("repr");
        await Assert.That(stage.Entries[0].Text).IsEqualTo("text");
        await Assert.That(stage.TimestampNanos).IsEqualTo(5L);

        // A second commit of the same packet never succeeds: a replay or a doubled OnPacket must not write it twice.
        await Assert.That(second).IsFalse();
        await Assert.That(secondMessage).Contains("already committed");
    }

    /// <summary>Proves that a staging whose packet was never committed is dropped when the next packet begins.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AStagingThatIsNeverCommittedIsDroppedByTheNextPacket()
    {
        using RowFieldStage stage = new();
        Packet rejected = _Packet(1);
        Packet next = _Packet(2);

        stage.BeginPacket(rejected.Id.Value, 0);
        stage.AddValue(7, 0, in _Number, default, default);
        stage.EndPacket(complete: true);

        // No TryCommit for the rejected packet (the filter said no); the next BeginPacket drops its rows.
        stage.BeginPacket(next.Id.Value, 0);
        stage.AddValue(8, 0, in _Number, default, default);
        stage.EndPacket(complete: true);
        bool committed = stage.TryCommit(next, out _, out _);

        await Assert.That(committed).IsTrue();
        await Assert.That(stage.Entries.Length).IsEqualTo(1);
        await Assert.That(stage.Entries[0].FieldId).IsEqualTo(8);
    }

    /// <summary>Proves that a staging of a packet that was not received completely is refused with a serialization error that names the cause.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AMutedStagingIsRefusedWithAMessageThatNamesTheCause()
    {
        using RowFieldStage stage = new();
        Packet packet = _Packet(3);

        stage.BeginPacket(packet.Id.Value, 0);
        stage.AddValue(7, 0, in _Number, default, default);
        stage.EndPacket(complete: false);
        bool committed = stage.TryCommit(packet, out ExportErrorKind kind, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(kind).IsEqualTo(ExportErrorKind.SerializationError);
        await Assert.That(message).Contains("not received completely");
    }

    /// <summary>Proves that a commit for a packet other than the staged one is refused with a message and commits nothing.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ACommitForAnotherPacketIdIsRefusedAndNothingIsCommitted()
    {
        using RowFieldStage stage = new();
        Packet staged = _Packet(4);
        Packet other = _Packet(5);

        stage.BeginPacket(staged.Id.Value, 0);
        stage.AddValue(7, 0, in _Number, default, default);
        stage.EndPacket(complete: true);
        bool committed = stage.TryCommit(other, out _, out string? message);

        await Assert.That(committed).IsFalse();
        await Assert.That(message).Contains("while fields of packet " + staged.Id.Value.ToString(CultureInfo.InvariantCulture) + " are staged");
    }

    /// <summary>Proves that committing an empty or discarded stage is refused with the neither-tree-nor-staged-fields message.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AnEmptyStageRefusesWithTheNeitherTreeNorStagedFieldsMessageAndDiscardEmptiesIt()
    {
        using RowFieldStage stage = new();
        Packet packet = _Packet(6);

        bool noStaging = stage.TryCommit(packet, out _, out string? message);
        stage.BeginPacket(packet.Id.Value, 0);
        stage.AddValue(7, 0, in _Number, default, default);
        stage.Discard();
        bool afterDiscard = stage.TryCommit(packet, out _, out string? afterDiscardMessage);

        await Assert.That(noStaging).IsFalse();
        await Assert.That(message).Contains("neither a field tree nor staged fields");
        await Assert.That(afterDiscard).IsFalse();
        await Assert.That(afterDiscardMessage).Contains("neither a field tree nor staged fields");
        await Assert.That(stage.StagedPacketId).IsEqualTo(-1);
    }

    #endregion

    #region Staged values

    /// <summary>Proves that replacing the last value changes the staged entry in place, and that the stage copies bytes when they are added.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task LastReplacesTheStagedValueInPlaceAndBytesAreCopiedAtOnce()
    {
        using RowFieldStage stage = new();
        Packet packet = _Packet(7);
        byte[] source = [1, 2, 3];
        FieldValueData bytes = FieldValue.NewBytes(source).Data;

        stage.BeginPacket(packet.Id.Value, 0);
        stage.AddValue(9, 0, in bytes, default, default);
        FieldValueData second = FieldValue.NewBytes(new byte[] { 9, 9 }).Data;
        stage.ReplaceLastValue(9, in second, default, default);

        // The source array is recycled by the caller after the parse: the stage must already hold its own copy.
        source[0] = 99;
        stage.EndPacket(complete: true);
        await Assert.That(stage.TryCommit(packet, out _, out _)).IsTrue();

        await Assert.That(stage.Entries.Length).IsEqualTo(1);
        await Assert.That(stage.GetBytes(in stage.Entries[0]).ToArray()).IsEquivalentTo(new byte[] { 9, 9 }, CollectionOrdering.Matching);
    }

    /// <summary>Proves that a deferred text completed after it was appended but before the end of the parse is evaluated at the end.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task ADeferredTextCompletedBeforeTheEndIsEvaluatedAtTheEnd()
    {
        using RowFieldStage stage = new();
        Packet packet = _Packet(8);
        int count = 0;
        LazyString deferred = LazyString.FormatLazy(
            new Func<int>(() => count), static read => string.Create(CultureInfo.InvariantCulture, $"items={read()}"));

        stage.BeginPacket(packet.Id.Value, 0);
        stage.AddValue(7, 0, in _Number, default, deferred);
        count = 3; // the dissector completes the text after the append, before the end of the parse
        stage.EndPacket(complete: true);
        await Assert.That(stage.TryCommit(packet, out _, out _)).IsTrue();

        await Assert.That(stage.Entries[0].Text).IsEqualTo("items=3");
    }

    /// <summary>Proves that after the staging sequence wraps, replacing the last value of an empty staging adds it, not touching a stale entry.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task AWrappedSequenceDoesNotPointALastReplacementAtAStaleEntry()
    {
        using RowFieldStage stage = new();
        const int fieldId = 7;

        // A packet at sequence 3 stages the field as entry 0. The sequence then wraps, and three packets later it would be back
        // at 3; a stale stamp would send the replacement of an empty staging to entry 0 of an empty list.
        stage.RestartSequenceAt(2);
        stage.BeginPacket(0, 0);
        stage.AddValue(fieldId, 0, in _Number, default, default);
        stage.EndPacket(complete: true);
        stage.RestartSequenceAt(-1);
        for (int i = 0; i < 3; i++)
        {
            stage.BeginPacket(0, 0);
            stage.EndPacket(complete: true);
        }

        FieldValueData replacement = FieldValue.NewU64(80).Data;
        stage.BeginPacket(0, 0);
        stage.ReplaceLastValue(fieldId, in replacement, default, default);
        stage.EndPacket(complete: true);

        await Assert.That(stage.Entries.Length).IsEqualTo(1);
        await Assert.That(stage.Entries[0].Value.TryGetAsU64(out ulong stored)).IsTrue();
        await Assert.That(stored).IsEqualTo(80UL);
    }

    #endregion
}
