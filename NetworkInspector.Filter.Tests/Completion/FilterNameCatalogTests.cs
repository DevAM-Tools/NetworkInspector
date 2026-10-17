// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Completion;

/// <summary>Covers the per-stack name catalog: caching, prefix lookup and closest-name suggestions.</summary>
internal sealed class FilterNameCatalogTests
{
    #region Caching

    [Test]
    public async Task For_SameStack_ReturnsTheSameCatalog()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        using Stack other = FilterTestHelper.BuildStack();

        FilterNameCatalog first = FilterNameCatalog.For(stack);
        FilterNameCatalog second = FilterNameCatalog.For(stack);
        FilterNameCatalog third = FilterNameCatalog.For(other);

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(third).IsNotSameReferenceAs(first);
    }

    [Test]
    public async Task For_FromManyThreads_ReturnsOneCatalog()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterNameCatalog[] seen = new FilterNameCatalog[4];
        Thread[] threads = new Thread[seen.Length];
        using Barrier start = new(threads.Length);
        for (int i = 0; i < threads.Length; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                seen[slot] = FilterNameCatalog.For(stack);
            })
            {
                Name = Invariant($"Catalog-{slot}"),
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        await Assert.That(seen.All(catalog => ReferenceEquals(catalog, seen[0]))).IsTrue();
    }

    #endregion

    #region Prefix lookup

    [Test]
    public async Task AddMatches_ProtocolAndContainerFieldShareOneEntry_KeepsTheProtocol()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        List<FilterCompletionItem> items = [];

        FilterNameCatalog.For(stack).AddMatches("udp", valueSourcesOnly: false, items);

        await Assert.That(items.Count(item => item.Text == "udp")).IsEqualTo(1);
        await Assert.That(items.First(item => item.Text == "udp").Kind).IsEqualTo(FilterCompletionKind.Protocol);
    }

    [Test]
    public async Task AddMatches_ValueSourcesOnly_SkipsProtocols()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        List<FilterCompletionItem> items = [];

        FilterNameCatalog.For(stack).AddMatches("udp", valueSourcesOnly: true, items);

        await Assert.That(items.Count).IsGreaterThan(0);
        await Assert.That(items.Any(item => item.Kind == FilterCompletionKind.Protocol)).IsFalse();
    }

    #endregion

    #region Suggestions

    [Test]
    [Arguments("udp.srcprot", "udp.srcport")]
    [Arguments("udp.srport", "udp.srcport")]
    [Arguments("udp.srcportt", "udp.srcport")]
    [Arguments("tpc", "tcp")]
    [Arguments("UDP", "udp")]
    public async Task Suggest_NearMiss_ReturnsTheClosestName(string written, string expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        string? suggestion = FilterNameCatalog.For(stack).Suggest(written);

        await Assert.That(suggestion).IsEqualTo(expected);
    }

    [Test]
    [Arguments("zzzzzzzzzz")]
    public async Task Suggest_FarName_ReturnsNull(string written)
    {
        using Stack stack = FilterTestHelper.BuildStack();

        string? suggestion = FilterNameCatalog.For(stack).Suggest(written);

        await Assert.That(suggestion).IsNull();
    }

    [Test]
    public async Task Suggest_VeryLongName_ReturnsNull()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        string written = new('x', 300);

        string? suggestion = FilterNameCatalog.For(stack).Suggest(written);

        await Assert.That(suggestion).IsNull();
    }

    #endregion
}
