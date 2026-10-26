// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// The one lookup of <c>ni settings get</c>, <c>set</c>, and <c>reset</c>: finds a setting (or a stored value of a plugin that is
/// not loaded) by name in the application settings, the profile settings, or both, applies the <c>--group</c> check, and builds
/// the error messages. Every check happens before anything is written. Stateless; thread-safe.
/// </summary>
internal static class SettingsLookup
{
    #region Types

    /// <summary>What a name resolved to.</summary>
    /// <param name="Scope">The scope the setting lives in.</param>
    /// <param name="Manager">The manager of that scope.</param>
    /// <param name="Setting">The registered setting; <see langword="null"/> for an orphan.</param>
    /// <param name="Orphan">The stored value of an unregistered setting; <see langword="null"/> for a registered setting.</param>
    internal sealed record Match(SettingScope Scope, SettingsManager Manager, Setting? Setting, OrphanedSettingValue? Orphan);

    #endregion

    #region Internal API

    /// <summary>Resolves a name.</summary>
    /// <param name="session">The plugin session that owns both managers.</param>
    /// <param name="name">The setting name.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> searches both.</param>
    /// <param name="group">The group the setting must be in; <see langword="null"/> accepts any.</param>
    /// <param name="match">The match; <see langword="null"/> on failure.</param>
    /// <param name="error">One line that says what is wrong; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="match"/> is set.</returns>
    internal static bool TryFind(CliPluginSession session, string name, SettingScope? scope, string? group,
        [NotNullWhen(true)] out Match? match, [NotNullWhen(false)] out string? error)
    {
        match = null;

        // Step 1, the group filter: a group that no searched scope has is a typo; list the groups so the user can see the
        // choices. Orphans count here, so the group of a plugin that is not loaded is a valid choice.
        if (group is not null && !_Groups(session, scope).Contains(group, StringComparer.Ordinal))
        {
            error = Invariant($"Unknown group '{group}'; groups: {string.Join(", ", _Groups(session, scope).Order(StringComparer.Ordinal))}.");
            return false;
        }

        // Step 2, the name: collect the hits of every searched scope (a registered setting first, else a stored value of an
        // unregistered one) so that a name present in both scopes is detected instead of silently resolving to one of them.
        List<Match> candidates = [];
        foreach ((SettingScope candidateScope, SettingsManager manager) in _Scopes(session, scope))
        {
            if (manager.TryGetSetting(name, out Setting? setting))
            {
                candidates.Add(new Match(candidateScope, manager, setting, null));
            }
            else
            {
                OrphanedSettingValue? orphan = manager.Orphans.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
                if (orphan is not null)
                {
                    candidates.Add(new Match(candidateScope, manager, null, orphan));
                }
            }
        }

        if (candidates.Count == 0)
        {
            error = _UnknownMessage(session, name);
            return false;
        }

        if (candidates.Count > 1)
        {
            error = Invariant($"Setting '{name}' exists in the application and the profile scope; add --scope.");
            return false;
        }

        // Step 3, the group check against the one hit: --group narrows a lookup, it never redirects it to another setting.
        Match found = candidates[0];
        string actualGroup = found.Setting?.GroupName ?? found.Orphan?.GroupName ?? string.Empty;
        if (group is not null && !string.Equals(actualGroup, group, StringComparison.Ordinal))
        {
            error = Invariant($"Setting '{name}' is in group '{actualGroup}', not '{group}'.");
            return false;
        }

        match = found;
        error = null;
        return true;
    }

    /// <summary>The scopes to search with their managers, application first.</summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> for both.</param>
    /// <returns>One pair per searched scope.</returns>
    internal static IReadOnlyList<(SettingScope Scope, SettingsManager Manager)> Scopes(CliPluginSession session, SettingScope? scope) =>
        _Scopes(session, scope);

    /// <summary>The names of the groups (registered groups and groups of stored values) of the searched scopes.</summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> for both.</param>
    /// <returns>The distinct non-empty group names.</returns>
    internal static IReadOnlyList<string> Groups(CliPluginSession session, SettingScope? scope) => _Groups(session, scope);

    #endregion

    #region Private Helpers

    /// <summary>The searched scopes with their managers: application first, then profile.</summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> for both.</param>
    /// <returns>One pair per searched scope.</returns>
    private static List<(SettingScope Scope, SettingsManager Manager)> _Scopes(CliPluginSession session, SettingScope? scope)
    {
        List<(SettingScope, SettingsManager)> scopes = [];
        if (scope is null or SettingScope.Application)
        {
            scopes.Add((SettingScope.Application, session.ApplicationSettings));
        }

        if (scope is null or SettingScope.Profile)
        {
            scopes.Add((SettingScope.Profile, session.ProfileSettings));
        }

        return scopes;
    }

    /// <summary>The distinct non-empty group names (registered and stored) of the searched scopes, ordinal comparison.</summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> for both.</param>
    /// <returns>The group names, in no particular order.</returns>
    private static List<string> _Groups(CliPluginSession session, SettingScope? scope)
    {
        HashSet<string> groups = new(StringComparer.Ordinal);
        foreach ((SettingScope _, SettingsManager manager) in _Scopes(session, scope))
        {
            foreach (string registered in manager.AllGroups)
            {
                groups.Add(registered);
            }

            foreach (OrphanedSettingValue orphan in manager.Orphans)
            {
                groups.Add(orphan.GroupName);
            }
        }

        groups.Remove(string.Empty);
        return [.. groups];
    }

    /// <summary>
    /// The message for a name nobody knows: up to three close names (from both scopes, duplicates removed), or a pointer to
    /// the list.
    /// </summary>
    /// <param name="session">The plugin session.</param>
    /// <param name="name">The name as typed.</param>
    /// <returns>One line of text.</returns>
    private static string _UnknownMessage(CliPluginSession session, string name)
    {
        List<string> similar = [];
        foreach ((SettingScope _, SettingsManager manager) in _Scopes(session, null))
        {
            foreach (string candidate in manager.FindSimilar(name, 3))
            {
                if (!similar.Contains(candidate, StringComparer.Ordinal))
                {
                    similar.Add(candidate);
                }
            }
        }

        return similar.Count > 0
            ? Invariant($"Unknown setting '{name}'; did you mean: {string.Join(", ", similar.Take(3))}?")
            : Invariant($"Unknown setting '{name}'; see ni settings list.");
    }

    #endregion
}
