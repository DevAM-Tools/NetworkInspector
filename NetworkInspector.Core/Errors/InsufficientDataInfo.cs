// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Detail information for <see cref="ParseErrorKind.InsufficientData"/> errors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct InsufficientDataInfo(ulong Expected, ulong Actual);
