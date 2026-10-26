// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// ZeroAlloc entry point for <c>NetworkInspector.Core</c>.
/// The Roslyn source generator creates optimized, zero-allocation overloads
/// for every <c>ZA.String(…)</c> / <c>ZA.Utf8(…)</c> call site in this assembly.
/// </summary>
/// <remarks>
/// Motivation: formatting interpolated or concatenated text through <c>ToString()</c> and <c>string.Concat</c> allocates
/// one intermediate string per argument; the generated overloads format each argument straight into a reusable buffer
/// instead (see the same class in the Protocols project for the call-site contract). Each assembly that uses
/// <c>ZA</c> declares its own internal partial class, so the generated overloads stay private to the assembly and are
/// not part of the public API of <c>NetworkInspector.Core</c>. The class has no members of its own; the generator fills it.
/// </remarks>
internal sealed partial class ZA : ZeroAllocBase;
