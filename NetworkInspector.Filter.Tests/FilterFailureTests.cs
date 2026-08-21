// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests;

/// <summary>Covers runtime failures: regex timeouts, throwing backends and failed rebinding.</summary>
internal sealed class FilterFailureTests
{
    #region Fakes

    /// <summary>A backend whose compiled program throws on every packet.</summary>
    private sealed class ThrowingCodegen(bool stateful) : IFilterCodegen
    {
        /// <inheritdoc />
        public FilterResult<CompiledFilterProgram> Compile(
            FilterProgram program,
            SymbolResolver resolver,
            FilterCompileOptions? options)
        {
            FlankRuntime[] flanks = stateful
                ?
                [
                    new FlankRuntime(
                        ValueAccessor.Direct([new FieldId(1)]),
                        from: null,
                        to: null,
                        by: null,
                        isAnyChange: true,
                        FlankWindow.FromNanoseconds(1_000_000_000L)),
                ]
                : [];
            return new CompiledFilterProgram(
                static _ => throw new InvalidOperationException("boom"),
                flanks);
        }
    }

    #endregion

    #region Regex timeout

    [Test]
    public async Task Match_RegexTimeout_PoisonsWithRuntimeError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { RegexTimeout = TimeSpan.FromTicks(1) };
        FilterResult<Filter> compiled = Filter.Compile("dns.qry.name matches \"^(a+)+$\"", stack, options);
        await Assert.That(compiled.IsSuccess).IsTrue();

        Packet packet = FilterTestHelper.Parse(
            stack,
            FilterTestHelper.BuildDnsQueryFrame(label: new string('a', 60)));

        bool produced = compiled.Value.TryIsMatch(packet, out bool matched, out FilterError? failure);

        await Assert.That(produced).IsFalse();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.RuntimeError);
        await Assert.That(compiled.Value.IsPoisoned).IsTrue();
    }

    #endregion

    #region Throwing backend

    [Test]
    public async Task Match_EvaluationThrows_Stateful_PoisonsWithRuntimeError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { Codegen = new ThrowingCodegen(stateful: true) };
        Filter filter = Filter.Compile("udp", stack, options).Value;
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(filter.IsStateful).IsTrue();

        bool produced = filter.TryIsMatch(packet, out bool matched, out FilterError? failure);

        await Assert.That(produced).IsFalse();
        await Assert.That(matched).IsFalse();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.RuntimeError);
        await Assert.That(failure.Message).Contains("InvalidOperationException");
        await Assert.That(filter.IsPoisoned).IsTrue();
    }

    [Test]
    public async Task Match_EvaluationThrows_Stateless_Propagates()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { Codegen = new ThrowingCodegen(stateful: false) };
        Filter filter = Filter.Compile("udp", stack, options).Value;
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(filter.IsStateful).IsFalse();
        await Assert.That(() => filter.TryIsMatch(packet, out _, out _))
            .Throws<InvalidOperationException>()
            .WithMessage("boom");
        await Assert.That(filter.IsPoisoned).IsFalse();

        // Unbind must have run: a second eval rebinds cleanly and still surfaces the bug.
        await Assert.That(() => filter.TryIsMatch(packet, out _, out _))
            .Throws<InvalidOperationException>()
            .WithMessage("boom");
        await Assert.That(filter.IsPoisoned).IsFalse();
    }

    #endregion

    #region Derive failure

    [Test]
    public async Task TryDerive_StackWithoutTheField_ReportsError()
    {
        using Stack full = FilterTestHelper.BuildStack();
        using Stack ethernetOnly = FilterTestHelper.BuildEthernetOnlyStack();
        Filter filter = FilterTestHelper.CompileOrThrow("udp.srcport == 53", full);

        bool derived = filter.TryDerive(ethernetOnly, out Filter? clone, out FilterError? failure);

        await Assert.That(derived).IsFalse();
        await Assert.That(clone).IsNull();
        await Assert.That(failure!.Kind).IsEqualTo(FilterErrorKind.UnknownField);
    }

    #endregion
}
