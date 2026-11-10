// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Limits;

/// <summary>
/// The limit policy code of NetworkInspector: how a user changes a limit with <c>ni</c>. Static pure functions, thread-safe.
/// </summary>
/// <remarks>
/// The policy values (the reference machine every default is sized for, <c>off</c> for every limit) are decisions of a person,
/// not code: they live in <c>custom_instructions.md</c> and <c>docs/decisions/limits-policy.md</c>. This type holds only what
/// the limits library cannot know: the sentence that names the <c>ni</c> commands.
/// <para>Sync: <c>custom_instructions.md</c> § Trust-boundary limits (the change hint names the settings command and the
/// <c>--set</c> option).</para>
/// </remarks>
/// <seealso cref="LimitDefinition"/>
/// <seealso cref="LimitMessages"/>
public static class CoreLimitPolicy
{
    /// <summary>
    /// Builds the hit sentence of <paramref name="definition"/> with the change hint of <c>ni</c>: the settings command and the
    /// <c>--set</c> option.
    /// </summary>
    /// <param name="definition">The limit that was hit.</param>
    /// <param name="eventText">What happened, with the measured value; see <see cref="LimitMessages.HitMessage"/>.</param>
    /// <param name="effective">The limit that was in effect.</param>
    /// <returns><c>{event}. Limit: {effective} (setting {key}). Change it with ni settings set {key} &lt;value&gt; or --set {key}=&lt;value&gt;.</c></returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="eventText"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="eventText"/> has no words.</exception>
    /// <remarks>Sync: the sentence is the one every earlier release printed; <see cref="LimitMessages.HitMessage"/> builds the rest.</remarks>
    public static string HitMessage(this LimitDefinition definition, string eventText, LimitValue effective)
    {
        ArgumentNullException.ThrowIfNull(definition);

        string changeHint = string.Create(CultureInfo.InvariantCulture,
            $"Change it with ni settings set {definition.Key} <value> or --set {definition.Key}=<value>.");
        return definition.HitMessage(eventText, effective, changeHint);
    }
}
