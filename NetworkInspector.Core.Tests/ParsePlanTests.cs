// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// <see cref="ParsePlan"/>: the combination of what each consumer of a run needs into one parse per packet. The plan takes the
/// deepest demand (no parse, parse without field tree, parse with field tree), keeps observers in demand order, accepts one
/// value cache and one index, and describes itself in one line for verbose output.
/// </summary>
internal sealed class ParsePlanTests
{
    #region Helpers

    private static (Stack Stack, ProbeProtocol Probe) _Probe()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        ProbeProtocol probe = new();
        ProtocolId id = builder.RegisterProtocol(probe);
        probe.RegisterFields(builder, id);
        return (builder.Build(), probe);
    }

    private static ParseDemand _Fields(IFieldObserver observer, FieldWatch watch) =>
        ParseDemand.Fields(new ParseObserver { Observer = observer, Watch = watch });

    private static ParsePlan _Plan(Stack stack, params ParseDemand[] demands)
    {
        if (!ParsePlan.TryCreate(stack, demands, out ParsePlan? plan, out ParsePlanError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        return plan;
    }

    #endregion

    #region Depth

    [Test]
    public async Task NoDemandAndFramesOnlyNeedNoParse()
    {
        (Stack stack, ProbeProtocol _) = _Probe();
        using (stack)
        {
            ParsePlan none = _Plan(stack);
            ParsePlan framesOnly = _Plan(stack, ParseDemand.FramesOnly);

            await Assert.That(none.Depth).IsEqualTo(ParseDepth.None);
            await Assert.That(none.NeedsParse).IsFalse();
            await Assert.That(framesOnly.Depth).IsEqualTo(ParseDepth.None);
            await Assert.That(framesOnly.NeedsParse).IsFalse();
        }
    }

    [Test]
    public async Task FieldsDemandNeedsAPacketParseWithoutATree()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ParsePlan plan = _Plan(stack, ParseDemand.FramesOnly, _Fields(new ListWatch(), FieldWatch.Only(stack, probe.A)));

            await Assert.That(plan.Depth).IsEqualTo(ParseDepth.Packet);
            await Assert.That(plan.NeedsParse).IsTrue();
            await Assert.That(plan.Options.FieldTree).IsEqualTo(FieldTreeMode.Skip);
        }
    }

    [Test]
    public async Task FieldTreeDemandWinsAndKeepsTheObserver()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ParsePlan plan = _Plan(stack, _Fields(new ListWatch(), FieldWatch.Only(stack, probe.A)), ParseDemand.FieldTree);

            await Assert.That(plan.Depth).IsEqualTo(ParseDepth.FieldTree);
            await Assert.That(plan.Options.FieldTree).IsEqualTo(FieldTreeMode.Build);
            await Assert.That(plan.Options.Observers.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ACacheOnlyDemandIsRaisedToAPacketParse()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(probe.A)]);

            // Depth stays at its default (None), but a cache cannot be filled without a parse: the plan raises it.
            ParsePlan plan = _Plan(stack, new ParseDemand { Cache = cache });

            await Assert.That(plan.Depth).IsEqualTo(ParseDepth.Packet);
            await Assert.That(plan.Options.Cache).IsSameReferenceAs(cache);
            await Assert.That(plan.Options.FieldTree).IsEqualTo(FieldTreeMode.Skip);
        }
    }

    [Test]
    public async Task RecordOnReplayIsOredOverTheDemandsOfOneCache()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(probe.A)]);

            ParsePlan plan = _Plan(stack, new ParseDemand { Cache = cache }, new ParseDemand { Cache = cache, RecordOnReplay = true });

            await Assert.That(plan.Options.RecordOnReplay).IsTrue();
        }
    }

    #endregion

    #region Conflicts

    [Test]
    public async Task TwoDifferentCachesTwoDifferentIndexesAndAForeignCacheAreRefused()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        (Stack other, ProbeProtocol otherProbe) = _Probe();
        using (stack)
        using (other)
        {
            ValueCache first = new(stack, [new ValueCacheFieldConfig(probe.A)]);
            ValueCache second = new(stack, [new ValueCacheFieldConfig(probe.B)]);
            ValueCache foreign = new(other, [new ValueCacheFieldConfig(otherProbe.A)]);
            PacketIndex indexOne = new(stack);
            PacketIndex indexTwo = new(stack);

            bool twoCaches = ParsePlan.TryCreate(
                stack, [new() { Cache = first }, new() { Cache = second }], out ParsePlan? p1, out ParsePlanError e1);
            bool twoIndexes = ParsePlan.TryCreate(
                stack, [new() { Index = indexOne }, new() { Index = indexTwo }], out ParsePlan? p2, out ParsePlanError e2);
            bool foreignCache = ParsePlan.TryCreate(stack, [new() { Cache = foreign }], out ParsePlan? p3, out ParsePlanError e3);
            bool nullObserver = ParsePlan.TryCreate(
                stack,
                [new() { Depth = ParseDepth.Packet, Observer = new ParseObserver { Watch = FieldWatch.All } }],
                out ParsePlan? p4,
                out ParsePlanError e4);

            await Assert.That(twoCaches).IsFalse();
            await Assert.That(e1.Code).IsEqualTo(ParsePlanErrorCode.MultipleValueCaches);
            await Assert.That(e1.Message).Contains("demand 1");
            await Assert.That(twoIndexes).IsFalse();
            await Assert.That(e2.Code).IsEqualTo(ParsePlanErrorCode.MultipleIndexes);
            await Assert.That(foreignCache).IsFalse();
            await Assert.That(e3.Code).IsEqualTo(ParsePlanErrorCode.CacheStackMismatch);
            await Assert.That(nullObserver).IsFalse();
            await Assert.That(e4.Code).IsEqualTo(ParsePlanErrorCode.NullObserver);

            // No plan exists after a refusal.
            await Assert.That(p1).IsNull();
            await Assert.That(p2).IsNull();
            await Assert.That(p3).IsNull();
            await Assert.That(p4).IsNull();
        }
    }

    [Test]
    public async Task AnUndefinedDepthAndOneObserverInTwoDemandsAreRefusedAndAFieldsDemandNeedsAnObserver()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ListWatch shared = new();

            bool undefinedDepth = ParsePlan.TryCreate(
                stack, [ParseDemand.FramesOnly, new() { Depth = (ParseDepth)7 }], out ParsePlan? p1, out ParsePlanError e1);
            bool sharedObserver = ParsePlan.TryCreate(
                stack,
                [_Fields(shared, FieldWatch.Only(stack, probe.A)), _Fields(shared, FieldWatch.Only(stack, probe.B))],
                out ParsePlan? p2,
                out ParsePlanError e2);

            await Assert.That(undefinedDepth).IsFalse();
            await Assert.That(e1.Code).IsEqualTo(ParsePlanErrorCode.InvalidDepth);
            await Assert.That(e1.Message).Contains("Demand 1");
            await Assert.That(p1).IsNull();
            await Assert.That(sharedObserver).IsFalse();
            await Assert.That(e2.Code).IsEqualTo(ParsePlanErrorCode.DuplicateObserver);
            await Assert.That(e2.Message).Contains("Demands 0 and 1");
            await Assert.That(p2).IsNull();
            await Assert.That(() => ParseDemand.Fields(default)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task TheSameCacheInTwoDemandsIsAcceptedOnce()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(probe.A)]);

            ParsePlan plan = _Plan(stack, new ParseDemand { Cache = cache }, new ParseDemand { Cache = cache });

            await Assert.That(plan.Options.Cache).IsSameReferenceAs(cache);
        }
    }

    #endregion

    #region Observers

    [Test]
    public async Task ObserversKeepDemandOrderAndTheArrayIsCreatedOnce()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ListWatch filter = new();
            ListWatch exporter = new();

            ParsePlan plan = _Plan(
                stack,
                _Fields(filter, FieldWatch.Only(stack, probe.A)),
                ParseDemand.FramesOnly,
                _Fields(exporter, FieldWatch.Only(stack, probe.B)));

            ReadOnlyMemory<ParseObserver> first = plan.Options.Observers;
            ReadOnlyMemory<ParseObserver> second = plan.Options.Observers;

            // The filter is listed first, so it sees every packet before the exporter does.
            await Assert.That(first.Length).IsEqualTo(2);
            await Assert.That(first.Span[0].Observer).IsSameReferenceAs(filter);
            await Assert.That(first.Span[1].Observer).IsSameReferenceAs(exporter);

            // One array for the whole run: every read of Options returns the same memory.
            await Assert.That(first.Equals(second)).IsTrue();
        }
    }

    #endregion

    #region Description

    [Test]
    public async Task ToStringDescribesEachShapeInOneLine()
    {
        (Stack stack, ProbeProtocol probe) = _Probe();
        using (stack)
        {
            ValueCache cache = new(stack, [new ValueCacheFieldConfig(probe.A)]);
            PacketIndex index = new(stack);
            ListWatch observer = new();
            ListWatch second = new();

            await Assert.That(_Plan(stack).ToString()).IsEqualTo("none");
            await Assert.That(_Plan(stack, _Fields(observer, FieldWatch.ForExtraction(stack, probe.A, probe.B))).ToString())
                .IsEqualTo("fields only, no tree (2 fields, 1 observer)");
            await Assert.That(_Plan(stack, _Fields(observer, FieldWatch.Only(stack, probe.A)), _Fields(second, FieldWatch.Only(stack, probe.A))).ToString())
                .IsEqualTo("fields only, no tree (1 field, 2 observers)");
            await Assert.That(_Plan(stack, _Fields(observer, FieldWatch.All)).ToString())
                .IsEqualTo("fields only, no tree (all fields, 1 observer)");
            await Assert.That(_Plan(stack, new ParseDemand { Cache = cache }).ToString())
                .IsEqualTo("packet only, no tree, value cache");
            await Assert.That(_Plan(stack, new ParseDemand { Index = index }).ToString())
                .IsEqualTo("packet only, no tree, index");
            await Assert.That(_Plan(stack, ParseDemand.FieldTree).ToString()).IsEqualTo("field tree");
            await Assert.That(_Plan(stack, ParseDemand.FieldTree, _Fields(observer, FieldWatch.All)).ToString())
                .IsEqualTo("field tree (1 observer)");
        }
    }

    #endregion
}
