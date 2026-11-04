// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>Which occurrences of a field in one packet are exported (a field repeats, for example IP in IP or several answers).</summary>
/// <remarks>
/// The policy is applied while the fields arrive, with one counter per field id: <see cref="First"/> drops later values,
/// <see cref="Last"/> overwrites the staged value in place, <see cref="All"/> appends. No packet is buffered twice and no
/// evaluation step runs afterwards, which keeps the rule simple for users and allocation-free on the hot path.
/// </remarks>
public enum Occurrences
{
    /// <summary>Every occurrence, numbered 0, 1, 2 … in the order the parse produced them. Default: nothing is lost silently.</summary>
    All = 0,

    /// <summary>Only the first occurrence (occurrence 0).</summary>
    First = 1,

    /// <summary>Only the last occurrence, exported as occurrence 0.</summary>
    Last = 2,
}
