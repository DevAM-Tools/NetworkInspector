// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Analysis;

/// <summary>Covers name binding order, caching and the value/presence distinction.</summary>
internal sealed class SymbolResolverTests
{
    #region Resolution

    [Test]
    public async Task Resolve_ProtocolName_BindsToProtocolWithContainerField()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterSymbol? symbol = resolver.Resolve("udp");

        await Assert.That(symbol!.Kind).IsEqualTo(FilterSymbolKind.Protocol);
        await Assert.That(symbol.IsValueSource).IsFalse();
        await Assert.That(symbol.ContainerField).IsEqualTo(FilterTestHelper.FieldIdOf(stack, "udp"));
        await Assert.That(symbol.Name).IsEqualTo("udp");
    }

    [Test]
    public async Task Resolve_ProtocolWithoutContainerField_ReportsInvalidContainer()
    {
        using Stack stack = FilterTestHelper.BuildStackWithContainerlessProtocol();
        SymbolResolver resolver = new(stack);

        FilterSymbol? symbol = resolver.Resolve("noctr");

        await Assert.That(symbol!.Kind).IsEqualTo(FilterSymbolKind.Protocol);
        await Assert.That(symbol.ContainerField.IsValid).IsFalse();
    }

    [Test]
    public async Task Resolve_FieldName_BindsToSingleField()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterSymbol? symbol = resolver.Resolve("udp.srcport");

        await Assert.That(symbol!.Kind).IsEqualTo(FilterSymbolKind.Field);
        await Assert.That(symbol.IsValueSource).IsTrue();
        await Assert.That(symbol.Fields.Length).IsEqualTo(1);
        await Assert.That(symbol.IndexGroup.IsValid).IsTrue();
    }

    [Test]
    public async Task Resolve_AliasName_BindsToEveryMember()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterSymbol? symbol = resolver.Resolve("ip.addr");

        await Assert.That(symbol!.Kind).IsEqualTo(FilterSymbolKind.Alias);
        await Assert.That(symbol.Fields.Length).IsEqualTo(2);
        await Assert.That(symbol.IndexGroup.IsValid).IsFalse();
    }

    [Test]
    public async Task Resolve_UnknownName_ReturnsNull()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        await Assert.That(resolver.Resolve("nope.nope")).IsNull();
    }

    [Test]
    public async Task Resolve_IsCached()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterSymbol? first = resolver.Resolve("udp.srcport");
        FilterSymbol? second = resolver.Resolve("udp.srcport");

        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task Resolve_UnknownNameIsCachedToo()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        await Assert.That(resolver.Resolve("nope")).IsNull();
        await Assert.That(resolver.Resolve("nope")).IsNull();
    }

    #endregion

    #region Diagnostics

    [Test]
    public async Task ResolveValue_ProtocolName_ReportsTypeMismatch()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterResult<FilterSymbol> result = resolver.ResolveValue("udp", 0, 3);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.TypeMismatch);
    }

    [Test]
    public async Task ResolveValue_UnknownName_ReportsUnknownField()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        FilterResult<FilterSymbol> result = resolver.ResolveValue("nope", 0, 4);

        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    [Test]
    public async Task ResolveAny_AcceptsProtocolAndField()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        await Assert.That(resolver.ResolveAny("udp", 0, 3).IsSuccess).IsTrue();
        await Assert.That(resolver.ResolveAny("udp.srcport", 0, 11).IsSuccess).IsTrue();
        await Assert.That(resolver.ResolveAny("nope", 0, 4).IsSuccess).IsFalse();
    }

    [Test]
    public async Task FieldOwners_MapsFieldsToTheirProtocol()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        SymbolResolver resolver = new(stack);

        ProtocolId[] owners = resolver.FieldOwners;
        FieldId srcPort = FilterTestHelper.FieldIdOf(stack, "udp.srcport");

        await Assert.That(owners[srcPort.Value]).IsEqualTo(FilterTestHelper.ProtocolIdOf(stack, "udp"));
    }

    #endregion
}
