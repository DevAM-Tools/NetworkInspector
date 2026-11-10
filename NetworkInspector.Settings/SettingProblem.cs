// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Why a value is refused for a setting. <see cref="Message"/> is English and names neither the setting nor the scope, so
/// callers prefix it: a command line or a remote client shows <see cref="ToString"/> (<c>name: message</c>). Examples of messages:
/// <c>'abc' is not a whole number; example: 40001</c>, <c>'x' is not a number; example: 1.5</c>,
/// <c>'maybe' is not true or false</c>, <c>'0xZZ' is not hex bytes; example: 0A1B</c>,
/// <c>'[1,' is not a valid JSON array</c>, <c>element 2: …</c>, <c>allowed 1 – 65535</c> (a missing bound shows the range of
/// the type), <c>'NaN' is not a finite number</c>, <c>allowed: off, verify, verify and mark</c>, and
/// <c>expected I64, got String</c>. Immutable; thread-safe.
/// </summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Setting">Name of the setting the value was meant for.</param>
/// <param name="Message">One line that says what is wrong and, where useful, what is allowed or an example.</param>
public sealed record SettingProblem(SettingProblemKind Kind, string Setting, string Message)
{
    /// <summary>Formats the problem as <c>setting: message</c>, the form a command line or a remote client shows.</summary>
    /// <returns>The prefixed message.</returns>
    public override string ToString() => Setting + ": " + Message;
}
