// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="PacketIndex"/>: direct API (Get/Try bitmap, cardinality) and
/// the <see cref="PresenceQuery"/> builder.
/// Uses a minimal Stack with mock protocols and fields to exercise the index API.
/// </summary>
internal sealed class PacketIndexQueryTests
{
    // === Helper to build a small stack with known groups and protocols ===

    private static (Stack Stack, ProtocolId Proto1, ProtocolId Proto2,
        IndexGroupId Group1, IndexGroupId Group2,
        FieldId Field1, FieldId Field2)
        _BuildIndexTestStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());

        StubProto p1 = new("proto1", "Protocol 1");
        StubProto p2 = new("proto2", "Protocol 2");
        ProtocolId id1 = builder.RegisterProtocol(p1);
        ProtocolId id2 = builder.RegisterProtocol(p2);

        // Register fields with index groups
        FieldId f1 = builder.RegisterFieldInGroup(id1, "proto1.field", "Field 1", FieldType.U64, "group1");
        FieldId f2 = builder.RegisterFieldInGroup(id2, "proto2.field", "Field 2", FieldType.U64, "group2");

        IndexGroupId g1 = builder.GetFieldIndexGroup(f1);
        IndexGroupId g2 = builder.GetFieldIndexGroup(f2);

        Stack stack = builder.Build();
        return (stack, id1, id2, g1, g2, f1, f2);
    }

    private static Stack _BuildLargeGroupStack(out FieldId fieldId, out IndexGroupId groupId)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProto proto = new("large.proto", "Large Protocol");
        ProtocolId protocolId = builder.RegisterProtocol(proto);

        for (int i = 0; i < 130; i++)
        {
            builder.RegisterFieldInGroup(
                protocolId,
                $"large.proto.field{i}",
                $"Field {i}",
                FieldType.U64,
                $"group{i}");
        }

        fieldId = builder.RegisterFieldInGroup(
            protocolId,
            "large.proto.target",
            "Target Field",
            FieldType.U64,
            "target.group");
        groupId = builder.GetFieldIndexGroup(fieldId);
        return builder.Build();
    }

    private static void _ShrinkGroupBitmaps(PacketIndex index)
    {
        System.Reflection.FieldInfo field = typeof(PacketIndex).GetField(
            "_GroupBitmaps",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(index, Array.Empty<RoaringBitmap>());
    }

    // === Construction ===

    [Test]
    public async Task Constructor_AllocatesBitmaps()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(index.GroupCount).IsGreaterThan(0);
            await Assert.That(index.ProtocolCount).IsGreaterThan(0);
            await Assert.That(index.Stack).IsEqualTo(stack);
        }
    }

    // === Direct recording and retrieval ===

    [Test]
    public async Task RecordGroupPresence_AndGetGroupBitmap()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            index.BeginPacket(1);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            ReadOnlyRoaringBitmap bm = index.GetGroupBitmap(g1);
            await Assert.That(bm.Cardinality).IsEqualTo(2L);
            await Assert.That(bm.Contains(0)).IsTrue();
            await Assert.That(bm.Contains(1)).IsTrue();
        }
    }

    [Test]
    public async Task RecordProtocolPresence_AndGetProtocolBitmap()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            ReadOnlyRoaringBitmap bm = index.GetProtocolBitmap(p1);
            await Assert.That(bm.Cardinality).IsEqualTo(1L);
            await Assert.That(bm.Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task GetFieldBitmap_ResolvesViaIndexGroup()
    {
        (Stack stack, _, _, IndexGroupId g1, _, FieldId f1, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(5);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            ReadOnlyRoaringBitmap bm = index.GetFieldBitmap(f1);
            await Assert.That(bm.Contains(5)).IsTrue();
        }
    }

    [Test]
    public async Task GetFieldBitmap_NoGroup_ReturnsEmpty()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        StubProto proto = new("nogroup.proto", "No Group Protocol");
        ProtocolId protocolId = builder.RegisterProtocol(proto);
        FieldId fieldWithoutGroup = builder.RegisterField(
            protocolId, "nogroup.proto.field", "Field Without Group", FieldType.U64);
        using Stack stack = builder.Build();
        PacketIndex index = new(stack);

        ReadOnlyRoaringBitmap bm = index.GetFieldBitmap(fieldWithoutGroup);
        await Assert.That(bm.IsEmpty).IsTrue();
    }

    // === DeDup within same packet ===

    [Test]
    public async Task RecordGroupPresence_Dedup_WithinSamePacket()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.RecordGroupPresence(g1); // duplicate
            index.RecordGroupPresence(g1); // duplicate
            index.EndPacket();

            // Should still be cardinality 1 (packet 0 recorded once)
            await Assert.That(index.GroupCardinality(g1)).IsEqualTo(1L);
        }
    }

    // === Cardinality ===

    [Test]
    public async Task GroupCardinality()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            for (int i = 0; i < 10; i++)
            {
                index.BeginPacket(i);
                index.RecordGroupPresence(g1);
                index.EndPacket();
            }

            await Assert.That(index.GroupCardinality(g1)).IsEqualTo(10L);
        }
    }

    [Test]
    public async Task ProtocolCardinality()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            for (int i = 0; i < 5; i++)
            {
                index.BeginPacket(i);
                index.RecordProtocolPresence(p1);
                index.EndPacket();
            }

            await Assert.That(index.ProtocolCardinality(p1)).IsEqualTo(5L);
        }
    }

    // === Try variants ===

    [Test]
    public async Task TryGetGroupBitmap_ValidId_ReturnsTrue()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            bool found = index.TryGetGroupBitmap(g1, out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsTrue();
            await Assert.That(bm.Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task TryGetGroupBitmap_InvalidId_ReturnsFalse()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            bool found = index.TryGetGroupBitmap(new IndexGroupId(9999), out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsFalse();
            await Assert.That(bm.IsEmpty).IsTrue();
        }
    }

    [Test]
    public async Task TryGetProtocolBitmap_ValidId_ReturnsTrue()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            bool found = index.TryGetProtocolBitmap(p1, out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsTrue();
            await Assert.That(bm.Contains(0)).IsTrue();
        }
    }

    [Test]
    public async Task TryGetProtocolBitmap_InvalidId_ReturnsFalse()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            bool found = index.TryGetProtocolBitmap(new ProtocolId(9999), out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsFalse();
            await Assert.That(bm.IsEmpty).IsTrue();
        }
    }

    [Test]
    public async Task TryGetFieldBitmap_ValidField()
    {
        (Stack stack, _, _, IndexGroupId g1, _, FieldId f1, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(3);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            bool found = index.TryGetFieldBitmap(f1, out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsTrue();
            await Assert.That(bm.Contains(3)).IsTrue();
        }
    }

    [Test]
    public async Task TryGetFieldBitmap_InvalidField_ReturnsFalse()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            bool found = index.TryGetFieldBitmap(FieldId.Invalid, out ReadOnlyRoaringBitmap bm);
            await Assert.That(found).IsFalse();
        }
    }

    [Test]
    public async Task TryGroupCardinality_ValidId()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            bool found = index.TryGroupCardinality(g1, out long cardinality);
            await Assert.That(found).IsTrue();
            await Assert.That(cardinality).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task TryGroupCardinality_InvalidId_ReturnsFalse()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            bool found = index.TryGroupCardinality(new IndexGroupId(9999), out long cardinality);
            await Assert.That(found).IsFalse();
            await Assert.That(cardinality).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task TryProtocolCardinality_ValidId()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            bool found = index.TryProtocolCardinality(p1, out long cardinality);
            await Assert.That(found).IsTrue();
            await Assert.That(cardinality).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task TryProtocolCardinality_InvalidId_ReturnsFalse()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            bool found = index.TryProtocolCardinality(new ProtocolId(9999), out long cardinality);
            await Assert.That(found).IsFalse();
            await Assert.That(cardinality).IsEqualTo(0L);
        }
    }

    // === PresenceQuery ===

    [Test]
    public async Task Query_SelectProtocol()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            // Packets 0-2 have p1, packets 1-3 have p2
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();
            index.BeginPacket(2);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();
            index.BeginPacket(3);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            long countP1 = index.Query().SelectProtocol(p1).Count();
            long countP2 = index.Query().SelectProtocol(p2).Count();
            await Assert.That(countP1).IsEqualTo(3L);
            await Assert.That(countP2).IsEqualTo(3L);
        }
    }

    [Test]
    public async Task Query_SelectGroup()
    {
        (Stack stack, _, _, IndexGroupId g1, IndexGroupId g2, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordGroupPresence(g2);
            index.EndPacket();

            long count = index.Query().SelectGroup(g1).Count();
            await Assert.That(count).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task Query_SelectField()
    {
        (Stack stack, _, _, IndexGroupId g1, _, FieldId f1, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            long count = index.Query().SelectField(f1).Count();
            await Assert.That(count).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task Query_And_IntersectsProtocols()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();
            index.BeginPacket(2);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            long count = index.Query().SelectProtocol(p1).AndProtocol(p2).Count();
            await Assert.That(count).IsEqualTo(1L); // Only packet 1 has both
        }
    }

    [Test]
    public async Task Query_Or_UnionsProtocols()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            long count = index.Query().SelectProtocol(p1).OrProtocol(p2).Count();
            await Assert.That(count).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task PresenceQuery_OrProtocol_DoesNotMutateIndexOwnedDenseBitmap()
    {
        (Stack stack, ProtocolId sparse, ProtocolId dense, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            const int denseCount = 4100;

            for (int packetId = 0; packetId < denseCount; packetId++)
            {
                index.BeginPacket(packetId);
                index.RecordProtocolPresence(dense);
                index.EndPacket();
            }

            index.BeginPacket(100);
            index.RecordProtocolPresence(sparse);
            index.EndPacket();
            index.BeginPacket(200);
            index.RecordProtocolPresence(sparse);
            index.EndPacket();

            long denseCardinalityBefore = index.GetProtocolBitmap(dense).Cardinality;
            long sparseCardinalityBefore = index.GetProtocolBitmap(sparse).Cardinality;

            long unionCount = index.Query().SelectProtocol(sparse).OrProtocol(dense).Count();

            await Assert.That(index.GetProtocolBitmap(dense).Cardinality).IsEqualTo(denseCardinalityBefore);
            await Assert.That(index.GetProtocolBitmap(sparse).Cardinality).IsEqualTo(sparseCardinalityBefore);
            await Assert.That(denseCardinalityBefore).IsEqualTo(denseCount);
            await Assert.That(unionCount).IsEqualTo(denseCount);
        }
    }

    [Test]
    public async Task Query_AndNot_SubtractsProtocol()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();
            index.BeginPacket(2);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            // p1 packets minus those with p2 = packets 0, 2
            long count = index.Query().SelectProtocol(p1).AndNotProtocol(p2).Count();
            await Assert.That(count).IsEqualTo(2L);

            bool has0 = index.Query().SelectProtocol(p1).AndNotProtocol(p2).Contains(0);
            bool has2 = index.Query().SelectProtocol(p1).AndNotProtocol(p2).Contains(2);
            await Assert.That(has0).IsTrue();
            await Assert.That(has2).IsTrue();
        }
    }

    [Test]
    public async Task Query_Xor_SymmetricDifference()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();
            index.BeginPacket(2);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            // p1 xor p2 = {0, 2} (packet 1 is in both, so excluded)
            long count = index.Query().SelectProtocol(p1).XorProtocol(p2).Count();
            await Assert.That(count).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task Query_Contains()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(5);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            bool found = index.Query().SelectProtocol(p1).Contains(5);
            bool notFound = index.Query().SelectProtocol(p1).Contains(0);
            await Assert.That(found).IsTrue();
            await Assert.That(notFound).IsFalse();
        }
    }

    [Test]
    public async Task Query_ToReadOnlyBitmap()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            ReadOnlyRoaringBitmap bm = index.Query().SelectProtocol(p1).ToReadOnlyBitmap();
            await Assert.That(bm.Cardinality).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task Query_ToBitmap_Detached()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            RoaringBitmap bm = index.Query().SelectProtocol(p1).ToBitmap();
            bm.Add(999);

            // Original index should not be affected
            await Assert.That(index.GetProtocolBitmap(p1).Contains(999)).IsFalse();
        }
    }

    [Test]
    public async Task Query_Empty_CountIsZero()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            // No selection made
            long count = index.Query().Count();
            await Assert.That(count).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task Query_AndNot_NoResult_Yields_Empty()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            // AndNot without prior selection → empty
            long count = index.Query().AndNotProtocol(p1).Count();
            await Assert.That(count).IsEqualTo(0L);
        }
    }

    // === PacketIndex off-lifecycle guard (regression for HIGH-3) ===

    [Test]
    public async Task RecordGroupPresence_BeforeFirstBeginPacket_ThrowsInvalidOperationException()
    {
        // Regression for HIGH-3 + initialization fix: _CurrentPacketId now starts at -1,
        // so calling RecordGroupPresence before any BeginPacket must throw.
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            InvalidOperationException? ex = null;
            try
            {
                index.RecordGroupPresence(g1);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            await Assert.That(ex).IsNotNull();
        }
    }

    [Test]
    public async Task RecordGroupPresence_AfterEndPacket_ThrowsInvalidOperationException()
    {
        // Regression for HIGH-3: RecordGroupPresence must fail fast after EndPacket.
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.EndPacket();

            InvalidOperationException? ex = null;
            try
            {
                index.RecordGroupPresence(g1);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            await Assert.That(ex).IsNotNull();
        }
    }

    [Test]
    public async Task RecordProtocolPresence_BeforeFirstBeginPacket_ThrowsInvalidOperationException()
    {
        // Regression for HIGH-3 + initialization fix.
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            InvalidOperationException? ex = null;
            try
            {
                index.RecordProtocolPresence(p1);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            await Assert.That(ex).IsNotNull();
        }
    }

    [Test]
    public async Task RecordProtocolPresence_AfterEndPacket_ThrowsInvalidOperationException()
    {
        // Regression for HIGH-3.
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.EndPacket();

            InvalidOperationException? ex = null;
            try
            {
                index.RecordProtocolPresence(p1);
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }
            await Assert.That(ex).IsNotNull();
        }
    }

    [Test]
    public async Task RecordGroupPresence_AfterThrow_DoesNotMutateBitmap()
    {
        // Verify that an off-lifecycle call does not silently insert anything into bitmaps.
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            // One valid record during a proper lifecycle.
            index.BeginPacket(7);
            index.RecordGroupPresence(g1);
            index.EndPacket();

            // Off-lifecycle call (throws but must not add anything).
            try
            {
                index.RecordGroupPresence(g1);
            }
            catch (InvalidOperationException ex)
            {
                // Expected: RecordGroupPresence after Freeze must throw. Confirm the exception is non-null.
                await Assert.That(ex).IsNotNull();
            }

            // Cardinality must still reflect exactly one packet (id=7), not a spurious id.
            await Assert.That(index.GroupCardinality(g1)).IsEqualTo(1L);
        }
    }

    // === Throw paths and extended PresenceQuery (exit-point coverage) ===

    [Test]
    public async Task GetGroupBitmap_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.GetGroupBitmap(new IndexGroupId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task GetProtocolBitmap_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.GetProtocolBitmap(new ProtocolId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task GetFieldBitmap_OutOfRangeField_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.GetFieldBitmap(new FieldId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task GetFieldBitmap_ResolvedGroupOutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, FieldId f1, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            _ShrinkGroupBitmaps(index);
            await Assert.That(() => index.GetFieldBitmap(f1))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task GroupCardinality_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.GroupCardinality(new IndexGroupId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task ProtocolCardinality_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.ProtocolCardinality(new ProtocolId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task RecordGroupPresence_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            await Assert.That(() => index.RecordGroupPresence(new IndexGroupId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task RecordProtocolPresence_DedupWithinPacket_ReturnsEarly()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            await Assert.That(index.ProtocolCardinality(p1)).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task RecordProtocolPresence_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            await Assert.That(() => index.RecordProtocolPresence(new ProtocolId(9999)))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task PresenceQuery_GroupAndFieldCombinators()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, IndexGroupId g1, IndexGroupId g2, FieldId f1, FieldId f2) =
            _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordGroupPresence(g2);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            long andGroup = index.Query().SelectProtocol(p1).AndGroup(g1).Count();
            long orField = index.Query().SelectProtocol(p1).OrField(f2).Count();
            long andNotGroup = index.Query().OrProtocol(p1).OrProtocol(p2).AndNotGroup(g2).Count();
            long xorField = index.Query().SelectProtocol(p1).XorField(f1).Count();

            await Assert.That(andGroup).IsEqualTo(1L);
            await Assert.That(orField).IsEqualTo(2L);
            await Assert.That(andNotGroup).IsEqualTo(1L);
            await Assert.That(xorField).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task PresenceQuery_ToBitmapAndContains()
    {
        (Stack stack, ProtocolId p1, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(3);
            index.RecordGroupPresence(g1);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            PresenceQuery query = index.Query().SelectProtocol(p1);
            bool contains3 = query.Contains(3);
            bool contains99 = query.Contains(99);
            RoaringBitmap mutable = query.ToBitmap();
            ReadOnlyRoaringBitmap readOnly = query.ToReadOnlyBitmap();
            bool mutableContains = mutable.Contains(3);
            bool readOnlyContains = readOnly.Contains(3);

            await Assert.That(contains3).IsTrue();
            await Assert.That(contains99).IsFalse();
            await Assert.That(mutableContains).IsTrue();
            await Assert.That(readOnlyContains).IsTrue();
        }
    }

    [Test]
    public async Task PresenceQuery_XorAndAndNotFieldCombinators()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, IndexGroupId g1, IndexGroupId g2, FieldId f1, FieldId f2) =
            _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordGroupPresence(g1);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordGroupPresence(g2);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            long xorGroup = index.Query().SelectProtocol(p1).XorGroup(g2).Count();
            long andNotField = index.Query().OrProtocol(p1).OrProtocol(p2).AndNotField(f2).Count();
            long orGroup = index.Query().SelectProtocol(p1).OrGroup(g2).Count();
            long andField = index.Query().SelectProtocol(p1).AndField(f1).Count();

            await Assert.That(xorGroup).IsEqualTo(2L);
            await Assert.That(andNotField).IsEqualTo(1L);
            await Assert.That(orGroup).IsEqualTo(2L);
            await Assert.That(andField).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task PresenceQuery_EmptyQuery_ContainsFalseAndEmptyBitmap()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            PresenceQuery query = index.Query();
            bool contains0 = query.Contains(0);
            bool bitmapEmpty = query.ToBitmap().IsEmpty;
            bool readOnlyEmpty = query.ToReadOnlyBitmap().IsEmpty;
            await Assert.That(contains0).IsFalse();
            await Assert.That(bitmapEmpty).IsTrue();
            await Assert.That(readOnlyEmpty).IsTrue();
        }
    }

    [Test]
    public async Task Query_XorProtocol_AsFirstOperation()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            long count = index.Query().XorProtocol(p1).Count();
            await Assert.That(count).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task Query_ToBitmap_AfterChainedOps_UsesMutableClonePath()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            RoaringBitmap bm = index.Query().SelectProtocol(p1).AndProtocol(p2).ToBitmap();
            await Assert.That(bm.Cardinality).IsEqualTo(1L);
            await Assert.That(bm.Contains(1)).IsTrue();
        }
    }

    [Test]
    public async Task Query_ToReadOnlyBitmap_AfterChainedOps_ReturnsDetachedClone()
    {
        (Stack stack, ProtocolId p1, ProtocolId p2, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            ReadOnlyRoaringBitmap snapshot = index.Query().SelectProtocol(p1).AndProtocol(p2).ToReadOnlyBitmap();
            index.BeginPacket(2);
            index.RecordProtocolPresence(p1);
            index.RecordProtocolPresence(p2);
            index.EndPacket();

            await Assert.That(snapshot.Cardinality).IsEqualTo(1L);
            await Assert.That(snapshot.Contains(1)).IsTrue();
            await Assert.That(snapshot.Contains(2)).IsFalse();
        }
    }

    [Test]
    public async Task Query_ToReadOnlyBitmap_SingleSelect_AliasesIndexBitmap()
    {
        (Stack stack, ProtocolId p1, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            ReadOnlyRoaringBitmap view = index.Query().SelectProtocol(p1).ToReadOnlyBitmap();
            index.BeginPacket(1);
            index.RecordProtocolPresence(p1);
            index.EndPacket();

            await Assert.That(view.Cardinality).IsEqualTo(2L);
            await Assert.That(view.Contains(1)).IsTrue();
        }
    }

    [Test]
    public async Task BeginPacket_NestedWithoutEndPacket_ThrowsInvalidOperationException()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            await Assert.That(() => index.BeginPacket(1))
                .Throws<InvalidOperationException>();
            index.EndPacket();
        }
    }

    [Test]
    public async Task RollbackCurrentPacket_WhenNoActivePacket_IsNoOp()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.RollbackCurrentPacket();
            await Assert.That(index.GroupCardinality(g1)).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task RollbackCurrentPacket_WhenActive_ClearsDedupWithoutCommit()
    {
        (Stack stack, _, _, IndexGroupId g1, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);

            index.BeginPacket(7);
            index.RecordGroupPresence(g1);
            index.RollbackCurrentPacket();
            index.EndPacket();

            await Assert.That(index.GroupCardinality(g1)).IsEqualTo(0L);
        }
    }

    [Test]
    public async Task EndPacket_WithoutBeginPacket_ThrowsInvalidOperationException()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.EndPacket())
                .Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task BeginPacket_NegativePacketId_ThrowsArgumentOutOfRangeException()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.BeginPacket(-1))
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task BeginPacket_LargeStack_ClearsDedupViaArrayClear()
    {
        Stack stack = _BuildLargeGroupStack(out FieldId fieldId, out IndexGroupId groupId);
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(index.GroupCount).IsGreaterThanOrEqualTo(130);

            index.BeginPacket(0);
            index.RecordGroupPresence(groupId);
            index.EndPacket();

            index.BeginPacket(1);
            index.RecordGroupPresence(groupId);
            index.EndPacket();

            await Assert.That(index.GetFieldBitmap(fieldId).Cardinality).IsEqualTo(2L);
        }
    }

    [Test]
    public async Task RecordGroupPresence_OutOfRange_UsesGroupIdParamName()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            index.BeginPacket(0);
            ArgumentOutOfRangeException? ex = null;
            try
            {
                index.RecordGroupPresence(new IndexGroupId(9999));
            }
            catch (ArgumentOutOfRangeException e)
            {
                ex = e;
            }
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex!.ParamName).IsEqualTo("groupId");
        }
    }

    [Test]
    public async Task PresenceQuery_SelectProtocol_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.Query().SelectProtocol(new ProtocolId(9999)).Count())
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task PresenceQuery_SelectGroup_OutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.Query().SelectGroup(new IndexGroupId(9999)).Count())
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task PresenceQuery_SelectField_OutOfRangeField_Throws()
    {
        (Stack stack, _, _, _, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            await Assert.That(() => index.Query().SelectField(FieldId.Invalid).Count())
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task PresenceQuery_SelectField_ResolvedGroupOutOfRange_Throws()
    {
        (Stack stack, _, _, _, _, FieldId f1, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            _ShrinkGroupBitmaps(index);
            await Assert.That(() => index.Query().SelectField(f1).Count())
                .Throws<ArgumentOutOfRangeException>();
        }
    }

    [Test]
    public async Task EndPacket_ConcurrentReaders_HeldViewSeesAppends()
    {
        (Stack stack, ProtocolId proto, _, IndexGroupId group, _, _, _) = _BuildIndexTestStack();
        using (stack)
        {
            PacketIndex index = new(stack);
            const int packetCount = 2000;
            ReadOnlyRoaringBitmap groupView = index.GetGroupBitmap(group);
            ReadOnlyRoaringBitmap protocolView = index.GetProtocolBitmap(proto);
            Exception? readerFailure = null;
            int vanished = 0;

            Task writer = Task.Run(() =>
            {
                for (int packetId = 0; packetId < packetCount; packetId++)
                {
                    index.BeginPacket(packetId);
                    index.RecordGroupPresence(group);
                    index.RecordProtocolPresence(proto);
                    index.EndPacket();
                }
            });

            Task reader = Task.Run(() =>
            {
                try
                {
                    bool[] seen = new bool[packetCount];
                    while (!writer.IsCompleted)
                    {
                        for (int id = 0; id < packetCount; id++)
                        {
                            if (groupView.Contains((uint)id))
                            {
                                seen[id] = true;
                            }
                            else if (seen[id])
                            {
                                Interlocked.Increment(ref vanished);
                            }
                        }
                    }

                    for (int id = 0; id < packetCount; id++)
                    {
                        if (!groupView.Contains((uint)id) && seen[id])
                        {
                            Interlocked.Increment(ref vanished);
                        }
                    }
                }
                catch (Exception ex)
                {
                    readerFailure = ex;
                }
            });

            await Task.WhenAll(writer, reader);
            await Assert.That(readerFailure).IsNull();
            await Assert.That(vanished).IsEqualTo(0);
            await Assert.That(groupView.Cardinality).IsEqualTo(packetCount);
            await Assert.That(protocolView.Contains((uint)(packetCount - 1))).IsTrue();
        }
    }

    // === Stub protocol ===

    private sealed class StubProto(string name, string uiName) : IProtocol
    {
        public string Name => name;
        public string UiName => uiName;
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
    }
}
