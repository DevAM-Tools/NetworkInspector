// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Why an operational <c>Try*</c> call on <see cref="Session"/> returned false.
/// Absent (null) means the call succeeded. Immutable. Thread-safety is not applicable.
/// This is the return-path counterpart of <see cref="SessionException"/>: expected failures travel as a value (an
/// <see langword="out"/> parameter), so a refused read or registration costs no exception on a path callers may poll.
/// </summary>
/// <param name="Code">The machine-readable cause; branch on this, not on <paramref name="Message"/>.</param>
/// <param name="Message">Human-readable description in English, for logs and diagnostics.</param>
public readonly record struct SessionFailure(SessionErrorCode Code, string Message);
