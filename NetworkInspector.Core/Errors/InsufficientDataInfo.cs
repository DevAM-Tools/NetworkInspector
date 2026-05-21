// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Detail information for <see cref="ParseErrorKind.InsufficientData"/> errors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct InsufficientDataInfo(ulong Expected, ulong Actual);
