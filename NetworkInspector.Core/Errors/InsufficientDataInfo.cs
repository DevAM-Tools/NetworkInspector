// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Detail information for <see cref="ParseErrorKind.InsufficientData"/> errors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct InsufficientDataInfo(ulong Expected, ulong Actual);