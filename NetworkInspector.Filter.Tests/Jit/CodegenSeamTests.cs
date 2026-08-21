// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Jit;

/// <summary>Covers the <see cref="IFilterCodegen"/> seam and the default expression-tree backend.</summary>
internal sealed class CodegenSeamTests
{
    #region Fakes

    /// <summary>A backend that ignores the program and always answers with a fixed verdict.</summary>
    private sealed class ConstantCodegen(bool verdict) : IFilterCodegen
    {
        private readonly bool _Verdict = verdict;

        /// <summary>Number of programs this backend was asked to compile.</summary>
        public int CompileCount
        {
            get; private set;
        }

        /// <inheritdoc />
        public FilterResult<CompiledFilterProgram> Compile(
            FilterProgram program,
            SymbolResolver resolver,
            FilterCompileOptions? options)
        {
            CompileCount++;
            bool verdict = _Verdict;
            return new CompiledFilterProgram(_ => verdict, []);
        }
    }

    /// <summary>A node type the emitter does not know about.</summary>
    private sealed class UnknownNode() : FilterNode(0, 1);

    /// <summary>A backend that always reports a compile error.</summary>
    private sealed class FailingCodegen : IFilterCodegen
    {
        /// <inheritdoc />
        public FilterResult<CompiledFilterProgram> Compile(
            FilterProgram program,
            SymbolResolver resolver,
            FilterCompileOptions? options)
        {
            return FilterResult.Fail<CompiledFilterProgram>(FilterError.Compiler("backend refused"));
        }
    }

    #endregion

    #region Seam

    [Test]
    public async Task Compile_UsesCodegenFromOptions()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        ConstantCodegen codegen = new(true);
        FilterCompileOptions options = new() { Codegen = codegen };

        FilterResult<Filter> result = Filter.Compile("udp.srcport == 9999", stack, options);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(codegen.CompileCount).IsEqualTo(1);
        await Assert.That(FilterTestHelper.MatchOrThrow(result.Value, packet)).IsTrue();
        await Assert.That(result.Value.IsStateful).IsFalse();
    }

    [Test]
    public async Task Compile_CodegenFailure_IsReported()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { Codegen = new FailingCodegen() };

        FilterResult<Filter> result = Filter.Compile("udp", stack, options);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.CompilerError);
        await Assert.That(result.Error.Message).Contains("backend refused");
    }

    [Test]
    public async Task Derive_FallsBackToDefaultCodegen()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        using Stack second = FilterTestHelper.BuildStack();
        FilterCompileOptions options = new() { Codegen = new ConstantCodegen(true) };
        Filter filter = Filter.Compile("udp.srcport == 9999", stack, options).Value;
        Packet packet = FilterTestHelper.Parse(second, FilterTestHelper.BuildUdpFrame(53, 1024));

        bool derived = filter.TryDerive(second, out Filter? clone, out _);

        await Assert.That(derived).IsTrue();
        await Assert.That(FilterTestHelper.MatchOrThrow(clone!, packet)).IsFalse();
    }

    #endregion

    #region Default backend

    [Test]
    public async Task ExpressionTreeCodegen_UnknownNode_ReportsCompilerError()
    {
        using Stack stack = FilterTestHelper.BuildStack();
        FilterProgram program = new("<synthetic>", new UnknownNode(), FilterFeature.Classic);
        ExpressionTreeCodegen codegen = new();

        FilterResult<CompiledFilterProgram> result = codegen.Compile(program, new SymbolResolver(stack), null);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error.Kind).IsEqualTo(FilterErrorKind.CompilerError);
        await Assert.That(result.Error.Message).Contains("UnknownNode");
    }

    [Test]
    public async Task ExpressionTreeCodegen_ProtocolScopeWithoutContainer_UsesOwnerMatching()
    {
        using Stack stack = FilterTestHelper.BuildStackWithContainerlessProtocol();
        Filter filter = FilterTestHelper.CompileOrThrow("$noctr { noctr.value == 1 }", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    [Test]
    public async Task ExpressionTreeCodegen_ProtocolWithoutContainer_FallsBackToOwnerScan()
    {
        using Stack stack = FilterTestHelper.BuildStackWithContainerlessProtocol();
        Filter filter = FilterTestHelper.CompileOrThrow("noctr", stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsFalse();
    }

    #endregion

    #region Compiled program

    [Test]
    public async Task CompiledProgram_WithoutFlanks_IsStateless()
    {
        CompiledFilterProgram program = new(_ => true, []);

        await Assert.That(program.IsStateful).IsFalse();
        program.ResetState();
        await Assert.That(program.Root(null!)).IsTrue();
    }

    #endregion
}
