// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// ZeroAlloc entry point for <c>NetworkInspector.Core</c>.
/// The Roslyn source generator creates optimized, zero-allocation overloads
/// for every <c>ZA.String(…)</c> / <c>ZA.Utf8(…)</c> call site in this assembly.
/// </summary>
internal sealed partial class ZA : ZeroAllocBase
{
}