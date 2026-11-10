// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Utility;

/// <summary>The sentence every hit of a limit reports. Stateless and thread-safe.</summary>
/// <seealso cref="LimitDefinition"/>
public static class LimitMessages
{
    /// <summary>Builds the one sentence every hit of a limit reports: what happened, the limit in effect, the key, and how to change it.</summary>
    /// <param name="definition">The limit that was hit.</param>
    /// <param name="eventText">
    /// What happened, with the measured value in the unit form of the limit (<see cref="LimitDefinition.Format"/>), for example
    /// <c>Entry is 300 MiB</c>; white space and every final period are dropped, and the sentence period is added.
    /// </param>
    /// <param name="effective">The limit that was in effect.</param>
    /// <param name="changeHint">The application's own sentence that says how the user changes the limit; ends with a period.</param>
    /// <returns><c>{event}. Limit: {effective} (setting {key}). {changeHint}</c></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="eventText"/> or <paramref name="changeHint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The event text has no words, or the hint is white space or does not end in a period.
    /// </exception>
    /// <remarks>
    /// The hint is required on purpose: how a limit is changed (a command, a settings page, a file) is the application's
    /// knowledge, and a library default would tell the user of another application the wrong way. Rejected: a hint set once in
    /// a static (global state that a second component in the process overwrites) and an optional parameter (the first caller
    /// would ship a sentence without a way to change the limit).
    /// </remarks>
    public static string HitMessage(this LimitDefinition definition, string eventText, LimitValue effective, string changeHint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventText);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeHint);

        // The hint is the application's text; the library only checks that the sentence ends cleanly, so a hit never ends mid-phrase.
        if (!changeHint.EndsWith('.'))
        {
            throw new ArgumentException("The change hint ends in a period.", nameof(changeHint));
        }

        // Final periods are dropped so the sentence neither ends with ".." nor with an ellipsis; a text of periods only has no words.
        string trimmed = eventText.TrimEnd().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("The event text has no words.", nameof(eventText));
        }

        string limit = definition.Format(effective);
        return string.Create(CultureInfo.InvariantCulture, $"{trimmed}. Limit: {limit} (setting {definition.Key}). {changeHint}");
    }
}
