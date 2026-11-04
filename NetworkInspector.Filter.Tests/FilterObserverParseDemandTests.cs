// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>
/// The parse need of a tree-free filter: <see cref="FilterObserver.ParseDemand"/> and <see cref="FilterObserver.ExtractionWatch"/>.
/// A pipeline combines the demand of the filter with the demand of its exporter into one parse plan, so a filter that can run
/// without a field tree adds no tree to the run.
/// </summary>
internal sealed class FilterObserverParseDemandTests
{
    private static readonly FilterObserverOptions _Options = FilterObserverOptions.Default;

    [Test]
    public async Task ARealFilterDemandsAPacketParseWithItsOwnObserverAndTheExtractionWatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;

        ParseDemand demand = observer.ParseDemand;

        await Assert.That(demand.Depth).IsEqualTo(ParseDepth.Packet);
        await Assert.That(demand.Observer.Observer).IsSameReferenceAs(observer);
        await Assert.That(demand.Observer.Watch == observer.ExtractionWatch).IsTrue();
        await Assert.That(demand.Observer.Watch.IsNone).IsFalse();
    }

    [Test]
    public async Task AnAlwaysMatchFilterDemandsNothing()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile(string.Empty, stack, in _Options).Value;

        ParseDemand demand = observer.ParseDemand;

        await Assert.That(demand.Depth).IsEqualTo(ParseDepth.None);
        await Assert.That(demand.Observer.Observer).IsNull();
        await Assert.That(observer.ExtractionWatch.IsNone).IsTrue();
    }

    [Test]
    public async Task TheDemandIsBuiltOnceAndStable()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53", stack, in _Options).Value;

        await Assert.That(observer.ExtractionWatch == observer.ExtractionWatch).IsTrue();
        await Assert.That(observer.ParseDemand.Observer.Watch == observer.ParseDemand.Observer.Watch).IsTrue();
    }

    [Test]
    public async Task TheExtractionWatchAcceptsExactlyTheFieldsOfTheOwningProtocolsOfTheObservedFields()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterObserver observer = Filter.Compile("udp.srcport == 53 && ip.ttl > 3", stack, in _Options).Value;
        ReadOnlySpan<FieldInfo> fields = stack.Fields.Span;
        HashSet<ProtocolId> owners =
        [
            fields[stack.GetFieldId("udp.srcport")!.Value.Value].ProtocolId,
            fields[stack.GetFieldId("ip.ttl")!.Value.Value].ProtocolId,
        ];

        // The expected set is computed independently of the implementation: every field of an owning protocol, nothing else.
        FieldWatch watch = observer.ExtractionWatch;
        List<int> wrong = [];
        for (int raw = 0; raw < fields.Length; raw++)
        {
            bool expected = owners.Contains(fields[raw].ProtocolId);
            if (watch.Accepts(new FieldId(raw)) != expected)
            {
                wrong.Add(raw);
            }
        }

        await Assert.That(wrong).IsEmpty();
    }
}
