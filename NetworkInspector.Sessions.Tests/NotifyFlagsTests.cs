// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Tests for <see cref="NotifyFlags"/> — atomic flag-based notification semantics.
/// Verifies flag OR, Exchange-clearing, and coalescing behaviour.
/// </summary>
internal sealed class NotifyFlagsTests
{
    [Test]
    public async Task Flags_IndividualBitsAreDistinct()
    {
        // Ensure all flags are powers of two (no overlap).
        NotifyFlags[] allFlags =
        [
            NotifyFlags.NewPackets,
            NotifyFlags.SourceAdded,
            NotifyFlags.SourceCompleted,
            NotifyFlags.AllSourcesCompleted,
            NotifyFlags.JobAdded,
            NotifyFlags.JobStatusChanged,
            NotifyFlags.PhaseChanged,
            NotifyFlags.ShuttingDown,
        ];

        for (int i = 0; i < allFlags.Length; i++)
        {
            for (int j = i + 1; j < allFlags.Length; j++)
            {
                NotifyFlags combined = allFlags[i] & allFlags[j];
                await Assert.That(combined).IsEqualTo(NotifyFlags.None);
            }
        }
    }

    [Test]
    public async Task InterlockedOr_CoalesceMultipleFlags()
    {
        int field = 0;
        Interlocked.Or(ref field, (int)NotifyFlags.NewPackets);
        Interlocked.Or(ref field, (int)NotifyFlags.SourceCompleted);
        Interlocked.Or(ref field, (int)NotifyFlags.NewPackets); // Duplicate — idempotent

        NotifyFlags result = (NotifyFlags)Interlocked.Exchange(ref field, 0);

        // Both flags should be set, duplicate was idempotent.
        await Assert.That(result.HasFlag(NotifyFlags.NewPackets)).IsTrue();
        await Assert.That(result.HasFlag(NotifyFlags.SourceCompleted)).IsTrue();

        // Field should be cleared after Exchange.
        await Assert.That(field).IsEqualTo(0);
    }

    [Test]
    public async Task Exchange_ClearsFieldAtomically()
    {
        int field = (int)(NotifyFlags.NewPackets | NotifyFlags.PhaseChanged);

        int read = Interlocked.Exchange(ref field, 0);
        NotifyFlags flags = (NotifyFlags)read;

        await Assert.That(flags.HasFlag(NotifyFlags.NewPackets)).IsTrue();
        await Assert.That(flags.HasFlag(NotifyFlags.PhaseChanged)).IsTrue();

        // Second exchange reads 0 — nothing pending.
        int second = Interlocked.Exchange(ref field, 0);
        await Assert.That(second).IsEqualTo(0);
    }

    [Test]
    public async Task None_HasNoFlagsSet()
    {
        NotifyFlags none = NotifyFlags.None;
        await Assert.That((int)none).IsEqualTo(0);
    }
}
