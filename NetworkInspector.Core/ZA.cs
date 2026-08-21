// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// ZeroAlloc entry point for <c>NetworkInspector.Core</c>.
/// The Roslyn source generator creates optimized, zero-allocation overloads
/// for every <c>ZA.String(…)</c> / <c>ZA.Utf8(…)</c> call site in this assembly.
/// </summary>
internal sealed partial class ZA : ZeroAllocBase;
