// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Utility;

/// <summary>One word a user may write after the number of a limit value, and the number of base units it stands for.</summary>
/// <param name="Text">
/// The canonical spelling, for example <c>MiB</c>, <c>min</c>, <c>Mbit/s</c>. One to 16 characters: ASCII letters, <c>%</c>,
/// and <c>/</c>; it does not start with <c>e</c>. A value is parsed ignoring case; it is formatted with this spelling.
/// </param>
/// <param name="Factor">How many base units the word stands for; at least 1. The base word of a unit has the factor 1.</param>
/// <seealso cref="UnitWordSet"/>
public readonly record struct UnitWord(string Text, long Factor);
