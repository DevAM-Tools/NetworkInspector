// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="IngestGate"/>: the first-parse versus replay decision, the outermost-only raise of the watermark,
/// the publication of effects recorded inside a scope, and the refusal to mutate on a replay.
/// </summary>
internal sealed class IngestGateTests
{
    private readonly record struct TestEffect(int Value);

    // The scope is a ref struct, so it cannot live across an await: the scoped work sits in synchronous helpers.
    private static void _EnterAndLeave(IngestGate gate, int id)
    {
        using IngestScope scope = gate.Enter(new PacketId(id));
    }

    private static (bool DuringInner, bool AfterInner) _NestedReplayStates(IngestGate gate, int id)
    {
        using IngestScope outer = gate.Enter(new PacketId(id));
        bool duringInner;
        bool afterInner;
        using (IngestScope inner = gate.Enter(new PacketId(id)))
        {
            duringInner = gate.IsReplay(new PacketId(id));
        }

        afterInner = gate.IsReplay(new PacketId(id));
        return (duringInner, afterInner);
    }

    private static void _EnterAndThrow(IngestGate gate, int id)
    {
        using IngestScope scope = gate.Enter(new PacketId(id));
        throw new InvalidOperationException("parse failed");
    }

    [Test]
    public async Task ANewGateHasParsedNothing()
    {
        IngestGate gate = new();

        await Assert.That(gate.Watermark).IsEqualTo(-1);
        await Assert.That(gate.IsReplay(new PacketId(0))).IsFalse();
    }

    [Test]
    public async Task APacketBecomesAReplayWhenItsScopeEnds()
    {
        IngestGate gate = new();
        bool before = gate.IsReplay(new PacketId(0));

        _EnterAndLeave(gate, 0);

        await Assert.That(before).IsFalse();
        await Assert.That(gate.IsReplay(new PacketId(0))).IsTrue();
        await Assert.That(gate.IsReplay(new PacketId(1))).IsFalse();
        await Assert.That(gate.Watermark).IsEqualTo(0);
    }

    [Test]
    public async Task OnlyTheOutermostScopeRaisesTheWatermark()
    {
        IngestGate gate = new();

        (bool duringInner, bool afterInner) = _NestedReplayStates(gate, 0);

        // An inner scope of the same packet must not turn the packet's remaining layers into replays.
        await Assert.That(duringInner).IsFalse();
        await Assert.That(afterInner).IsFalse();
        await Assert.That(gate.IsReplay(new PacketId(0))).IsTrue();
    }

    [Test]
    public async Task AnExceptionInsideTheScopeStillRaisesTheWatermark()
    {
        IngestGate gate = new();

        InvalidOperationException? thrown = null;
        try
        {
            _EnterAndThrow(gate, 0);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(gate.IsReplay(new PacketId(0))).IsTrue();
    }

    [Test]
    public async Task EnteringAReplayedPacketThrows()
    {
        IngestGate gate = new();
        _EnterAndLeave(gate, 0);

        InvalidOperationException? thrown = null;
        try
        {
            _EnterAndLeave(gate, 0);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();

        // The refused Enter opened no scope: the next packet is still a first parse and the watermark is unchanged.
        await Assert.That(gate.Watermark).IsEqualTo(0);
        _EnterAndLeave(gate, 1);
        await Assert.That(gate.Watermark).IsEqualTo(1);
    }

    [Test]
    public async Task TheDefaultScopeIsInert()
    {
        IngestGate gate = new();
        IngestScope inert = default;

        inert.Dispose();

        await Assert.That(gate.Watermark).IsEqualTo(-1);
    }

    [Test]
    public async Task AReaderThatSeesAReplayAlsoSeesItsEffect()
    {
        // One writer records an effect for every id inside its scope while four readers poll random ids. Whenever a reader
        // sees "replay", the effect must already be published; the assertion holds for every interleaving, so it cannot flake.
        const int Count = 10_000;
        IngestGate gate = new();
        EffectStore<TestEffect> effects = new();
        int violations = 0;
        int observedReplays = 0;
        bool writerDone = false;

        Task writer = Task.Run(() =>
        {
            for (int id = 0; id < Count; id++)
            {
                using IngestScope scope = gate.Enter(new PacketId(id));
                effects.Record(id, 0, new TestEffect(id));
            }

            Volatile.Write(ref writerDone, true);
        });

        Task[] readers = new Task[4];
        for (int reader = 0; reader < readers.Length; reader++)
        {
            int seed = reader;
            readers[reader] = Task.Run(() =>
            {
                Random random = new(seed);
                while (!Volatile.Read(ref writerDone))
                {
                    int id = random.Next(Count);
                    if (gate.IsReplay(new PacketId(id)))
                    {
                        Interlocked.Increment(ref observedReplays);
                        if (!effects.TryGet(id, 0, out TestEffect effect) || effect.Value != id)
                        {
                            Interlocked.Increment(ref violations);
                        }
                    }
                }
            });
        }

        await writer;
        await Task.WhenAll(readers);

        await Assert.That(violations).IsEqualTo(0);
        await Assert.That(gate.Watermark).IsEqualTo(Count - 1);
        await Assert.That(effects.TryGet(Count - 1, 0, out _)).IsTrue();
    }
}
