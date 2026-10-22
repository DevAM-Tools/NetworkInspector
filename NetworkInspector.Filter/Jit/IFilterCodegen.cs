// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// Turns a bound <see cref="FilterProgram"/> into an executable
/// <see cref="CompiledFilterProgram"/>.
/// <para>
/// The seam exists so the back end can be swapped — for example for a future emitter or a test
/// double — without touching the front end, the analyzer or the public API.
/// <see cref="EvalTreeCodegen"/> (a tree of typed evaluation nodes, no runtime code generation) is
/// the default and only implementation shipped.
/// </para>
/// <para>
/// Implementations perform name binding and therefore report
/// <see cref="FilterErrorKind.UnknownField"/> and
/// <see cref="FilterErrorKind.TypeMismatch"/>.
/// </para>
/// </summary>
internal interface IFilterCodegen
{
    /// <summary>Compiles a parsed program against a resolver.</summary>
    FilterResult<CompiledFilterProgram> Compile(
        FilterProgram program,
        SymbolResolver resolver,
        FilterCompileOptions? options);
}
