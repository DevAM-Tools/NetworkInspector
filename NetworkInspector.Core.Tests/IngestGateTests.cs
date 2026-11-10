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
    /// <summary>Whether the gate treats <paramref name="id"/> as a replay when the stack gave no verdict (an empty context).</summary>
    private static bool _IsReplay(IngestGate gate, int id)
    {
        ParseContext context = default;
        return gate.IsReplay(in context, id);
    }

    /// <summary>Whether the gate treats <paramref name="id"/> as a replay when the stack classified the parse as <paramref name="stackVerdict"/>.</summary>
    private static bool _IsReplayWithStackVerdict(IngestGate gate, int id, bool stackVerdict)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        using Stack stack = builder.Build();
        ParseContext context = new(stack, isReplay: stackVerdict);
        return gate.IsReplay(in context, id);
    }

    /// <summary>Enters the gate under a stack verdict of "replay"; the gate must refuse.</summary>
    private static void _EnterUnderStackReplayVerdict(IngestGate gate, int id)
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        using Stack stack = builder.Build();
        ParseContext context = new(stack, isReplay: true);
        using IngestScope scope = gate.Enter(in context, id);
    }

    /// <summary>Opens a scope and disposes a copy of it twice; the second dispose must throw.</summary>
    private static void _DisposeScopeCopyTwice(IngestGate gate, int id)
    {
        ParseContext context = default;
        IngestScope scope = gate.Enter(in context, id);
        IngestScope copy = scope;
        scope.Dispose();
        copy.Dispose();
    }

    private static void _EnterAndLeave(IngestGate gate, int id)
    {
        ParseContext context = default;
        using IngestScope scope = gate.Enter(in context, id);
    }

    private static (bool DuringInner, bool AfterInner) _NestedReplayStates(IngestGate gate, int id)
    {
        ParseContext context = default;
        using IngestScope outer = gate.Enter(in context, id);
        bool duringInner;
        bool afterInner;
        using (IngestScope inner = gate.Enter(in context, id))
        {
            duringInner = gate.IsReplay(in context, id);
        }

        afterInner = gate.IsReplay(in context, id);
        return (duringInner, afterInner);
    }

    private static void _EnterAndThrow(IngestGate gate, int id)
    {
        ParseContext context = default;
        using IngestScope scope = gate.Enter(in context, id);
        throw new InvalidOperationException("parse failed");
    }

    [Test]
    public async Task ANewGateHasParsedNothing()
    {
        IngestGate gate = new();

        await Assert.That(gate.Watermark).IsEqualTo(-1);
        await Assert.That(_IsReplay(gate, 0)).IsFalse();
    }

    [Test]
    public async Task APacketBecomesAReplayWhenItsScopeEnds()
    {
        IngestGate gate = new();
        bool before = _IsReplay(gate, 0);

        _EnterAndLeave(gate, 0);

        await Assert.That(before).IsFalse();
        await Assert.That(_IsReplay(gate, 0)).IsTrue();
        await Assert.That(_IsReplay(gate, 1)).IsFalse();
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
        await Assert.That(_IsReplay(gate, 0)).IsTrue();
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
        await Assert.That(_IsReplay(gate, 0)).IsTrue();
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
    public async Task AStackReplayIsAReplayAlsoWhenTheGateNeverSawThePacket()
    {
        IngestGate gate = new();

        // The stack completed id 5 on its own (the packet never reached this gate): the gate must still answer "replay".
        bool replay = _IsReplayWithStackVerdict(gate, 5, stackVerdict: true);
        bool firstParse = _IsReplayWithStackVerdict(gate, 5, stackVerdict: false);

        await Assert.That(gate.Watermark).IsEqualTo(-1);
        await Assert.That(replay).IsTrue();
        await Assert.That(firstParse).IsFalse();
    }

    [Test]
    public async Task EnteringUnderAStackReplayVerdictThrowsAndOpensNoScope()
    {
        IngestGate gate = new();

        InvalidOperationException? thrown = null;
        try
        {
            _EnterUnderStackReplayVerdict(gate, 0);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        // The refused Enter opened no scope: the next first parse raises the watermark normally.
        _EnterAndLeave(gate, 0);

        await Assert.That(thrown).IsNotNull();
        await Assert.That(gate.Watermark).IsEqualTo(0);
    }

    [Test]
    public async Task ADoubleDisposeOfAScopeCopyThrowsAndKeepsTheGateUsable()
    {
        IngestGate gate = new();

        InvalidOperationException? thrown = null;
        try
        {
            _DisposeScopeCopyTwice(gate, 0);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        // The nesting counter stayed at zero, so the next packet still raises the watermark.
        _EnterAndLeave(gate, 1);

        await Assert.That(thrown).IsNotNull();
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
                ParseContext context = default;
                using IngestScope scope = gate.Enter(in context, id);
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
                    if (_IsReplay(gate, id))
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
