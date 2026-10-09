// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Why an operational <c>Try*</c> call on <see cref="Session"/> returned false.
/// Absent (null) means the call succeeded. Immutable. Thread-safety is not applicable.
/// </summary>
public readonly record struct SessionFailure(SessionErrorCode Code, string Message);
