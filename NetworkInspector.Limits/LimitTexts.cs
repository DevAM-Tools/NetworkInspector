// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The seven English texts that explain a limit to the person who may change it. Every <see cref="LimitDefinition"/> requires
/// all of them; a front end (command line, settings page, remote client) shows them and may translate them later by the key of
/// the definition. The definition checks the form (non-empty, no control character, sentences end with a period).
/// </summary>
/// <param name="Title">A short name of at most 60 characters without a final period, for example "Largest archive entry".</param>
/// <param name="Description">What the limit limits, in which situation it matters, and what the user influences.</param>
/// <param name="Reason">Why the limit exists, in the words of the user and not of the implementation.</param>
/// <param name="Benefit">Why the limit is useful for the user.</param>
/// <param name="SmallerValues">What smaller values mean, with an example.</param>
/// <param name="LargerValues">What larger values mean, with an example.</param>
/// <param name="OffEffect">What can happen when the limit is switched off.</param>
/// <seealso cref="LimitDefinition"/>
public sealed record LimitTexts(
    string Title,
    string Description,
    string Reason,
    string Benefit,
    string SmallerValues,
    string LargerValues,
    string OffEffect);
