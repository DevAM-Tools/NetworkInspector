// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Atomicity tests for <see cref="StandardBlockBuilder.AddPacket"/>: a packet that fails part-way must leave no trace in
/// the block. Each test builds the same block twice, once with a failing packet in between and once without, and requires
/// identical bytes.
/// </summary>
internal sealed class StandardBlockBuilderAtomicityTests
{
    /// <summary>Builds the block of the given packets; <paramref name="failing"/> packets are offered but must be rejected.</summary>
    private static byte[] _BuildBlock(int maxFieldId, IEnumerable<(Packet Packet, bool Failing)> sequence)
    {
        StandardBlockBuilder builder = new(maxFieldId);
        try
        {
            foreach ((Packet packet, bool failing) in sequence)
            {
                if (failing)
                {
                    try
                    {
                        builder.AddPacket(packet);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Expected: the packet holds a field id beyond the builder's range.
                    }
                }
                else
                {
                    builder.AddPacket(packet);
                }
            }

            return builder.Build().ToArray();
        }
        finally
        {
            builder.Dispose();
        }
    }

    [Test]
    public async Task FailedPacketLeavesNoBoundsFieldInfoOrPreviousValueStateBehind()
    {
        using CustomFieldPacketFactory factory = new();
        Packet failing = factory.Create(7, "text");
        Packet good = factory.Create(7, null);

        // The text field id is the first id outside the range, so the failing packet is serialized up to the text field
        // (container and number are marked as seen) and then rejected. Both packets carry the number 7: had the failed
        // packet's value stayed in the same-as-previous state, the good packet would omit it, and a reader would find no
        // value there.
        int maxFieldId = factory.TextFieldId.Value;

        byte[] withFailure = _BuildBlock(maxFieldId, [(failing, true), (good, false)]);
        byte[] reference = _BuildBlock(maxFieldId, [(good, false)]);

        await Assert.That(withFailure).IsEquivalentTo(reference);
    }

    [Test]
    public async Task BuilderReportsOnlyTheAcceptedPackets()
    {
        using CustomFieldPacketFactory factory = new();
        Packet good = factory.Create(7, null);
        Packet failing = factory.Create(7, "text");
        StandardBlockBuilder builder = new(factory.TextFieldId.Value);
        try
        {
            builder.AddPacket(good);
            Exception? failure = null;
            try
            {
                builder.AddPacket(failing);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                failure = ex;
            }

            // The failing packet has the larger id and timestamp; neither may widen the block bounds.
            await Assert.That(failure).IsNotNull();
            await Assert.That(builder.PacketCount).IsEqualTo(1);
            await Assert.That(builder.MinPacketId).IsEqualTo(good.Id.Value);
            await Assert.That(builder.MaxPacketId).IsEqualTo(good.Id.Value);
            await Assert.That(builder.MinTimestamp).IsEqualTo(good.Timestamp.AsNanos);
            await Assert.That(builder.MaxTimestamp).IsEqualTo(good.Timestamp.AsNanos);
        }
        finally
        {
            builder.Dispose();
        }
    }
}
