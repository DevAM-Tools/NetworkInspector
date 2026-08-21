// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Jit;

/// <summary>
/// Turns a bound <see cref="FilterProgram"/> into an executable
/// <see cref="CompiledFilterProgram"/>.
/// <para>
/// The seam exists so the back end can be swapped — for example for an interpreting fallback on
/// platforms where expression-tree compilation is unavailable, or for a future emitter — without
/// touching the front end, the analyzer or the public API.
/// <see cref="ExpressionTreeCodegen"/> is the default and only implementation shipped in v1.
/// </para>
/// <para>
/// Implementations perform name binding and therefore report
/// <see cref="FilterErrorKind.UnknownField"/>, <see cref="FilterErrorKind.UnknownProtocol"/> and
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
