// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Profile folders under a settings base, on <see cref="ProfileNames"/> and <see cref="StorageNames"/>. Profile names compare
/// case-insensitively. Every member validates its arguments and leaves the base unchanged on failure. Thread safety: the
/// members are file-system operations; concurrent creates of one name end with exactly one success, because each creator
/// builds the profile in a hidden folder of its own and moves it into place, which only one mover can do. The host's profile
/// service adds the active-profile and in-use refusals on top.
/// </summary>
public static class SettingsProfiles
{
    #region Constants

    /// <summary>Name of the profile that is created on first use.</summary>
    public const string DefaultProfile = "default";

    /// <summary>Prefix of the hidden work folders of creates and renames; profile names cannot start with a dot.</summary>
    private const string _WorkFolderPrefix = ".work-";

    /// <summary>Name of the cross-process lock file, which a copy must not take along.</summary>
    private const string _LockFileName = SettingsManager.LockFileName;

    #endregion

    #region Public API

    /// <summary>
    /// Lists the profiles: folders under the base that are not reserved, not hidden, and whose name is valid (a mapped folder
    /// needs a readable sidecar). A base that does not exist has no profiles.
    /// </summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <returns>The profile names, sorted ignoring case.</returns>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static IReadOnlyList<string> List(string settingsBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        return [.. _Entries(settingsBase).Select(static entry => entry.Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static name => name, StringComparer.Ordinal)];
    }

    /// <summary>Whether a profile exists (names compare ignoring case; an invalid name does not exist).</summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="name">The profile name.</param>
    /// <returns><see langword="true"/> when the profile exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool Exists(string settingsBase, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        return name is not null && _Find(settingsBase, name) is not null;
    }

    /// <summary>Creates an empty profile or a copy of <paramref name="copyFrom"/> (every file, no limits).</summary>
    /// <param name="settingsBase">The settings base folder; it is created when it does not exist.</param>
    /// <param name="name">The new profile name; the rules of <see cref="ProfileNames.TryValidate"/> apply.</param>
    /// <param name="copyFrom">An existing profile whose files are copied, or <see langword="null"/> for an empty profile.</param>
    /// <param name="error">
    /// Why the profile was not created; <see langword="null"/> on success. Messages: <c>Profile 'X' already exists.</c>,
    /// <c>Profile 'X' does not exist.</c> (the source), <c>Profile name 'X': &lt;rule&gt;.</c>, or a file-system message.
    /// </param>
    /// <returns><see langword="true"/> when the profile was created.</returns>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryCreate(string settingsBase, string name, string? copyFrom, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        if (!_CheckName(name, out error))
        {
            return false;
        }

        // The source must exist and the target must not (case-insensitively); both before anything is written.
        Entry? source = null;
        if (copyFrom is not null)
        {
            source = _Find(settingsBase, copyFrom);
            if (source is null)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Profile '{copyFrom}' does not exist.");
                return false;
            }
        }

        if (_Find(settingsBase, name) is not null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' already exists.");
            return false;
        }

        string storageName = StorageNames.ToStorageName(name);
        string work = Path.Combine(settingsBase, _WorkFolderPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(settingsBase);
            Directory.CreateDirectory(work);
            if (source is not null)
            {
                _CopyDirectory(Path.Combine(settingsBase, source.StorageName), work);
            }

            // The sidecar goes first, so a listing never sees a mapped folder without its name.
            if (!StorageNames.TryWriteNameFile(settingsBase, name, out string? sidecarProblem))
            {
                error = sidecarProblem;
                return false;
            }

            // Only one mover can win; a loser sees the folder of the winner and reports the profile as existing.
            Directory.Move(work, Path.Combine(settingsBase, storageName));
            error = null;
            return true;
        }
        catch (IOException) when (Directory.Exists(Path.Combine(settingsBase, storageName)))
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' already exists.");
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing is left half-way: the sidecar belongs to a profile that does not exist, so it goes with the work folder.
            if (!Directory.Exists(Path.Combine(settingsBase, storageName)))
            {
                StorageNames.DeleteNameFile(settingsBase, storageName);
            }

            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' cannot be created: {exception.Message}");
            return false;
        }
        finally
        {
            _DeleteDirectoryQuietly(work);
        }
    }

    /// <summary>Renames a profile (also a case-only rename), moving folder and sidecar.</summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="oldName">The existing profile.</param>
    /// <param name="newName">The new name; the rules of <see cref="ProfileNames.TryValidate"/> apply.</param>
    /// <param name="error">Why the profile was not renamed; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the profile was renamed.</returns>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryRename(string settingsBase, string oldName, string newName, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        Entry? existing = oldName is null
            ? null
            : _Find(settingsBase, oldName);
        if (existing is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{oldName}' does not exist.");
            return false;
        }

        if (!_CheckName(newName, out error))
        {
            return false;
        }

        // A name that already exists is refused, except for the profile itself (a case-only rename): names are unique
        // ignoring case, so a match with another spelling of the old name is the same profile.
        Entry? clash = _Find(settingsBase, newName);
        if (clash is not null && !string.Equals(clash.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{newName}' already exists.");
            return false;
        }

        string newStorage = StorageNames.ToStorageName(newName);
        string oldPath = Path.Combine(settingsBase, existing.StorageName);
        string newPath = Path.Combine(settingsBase, newStorage);
        string? work = null;
        bool moved = false;
        try
        {
            if (!StorageNames.TryWriteNameFile(settingsBase, newName, out string? sidecarProblem))
            {
                error = sidecarProblem;
                return false;
            }

            // The folder moves in two steps through a hidden name, so that a case-only rename works on file systems that
            // ignore case. A different storage name needs the same: the old folder must be free of the new name first.
            if (!string.Equals(existing.StorageName, newStorage, StringComparison.Ordinal))
            {
                work = Path.Combine(settingsBase, _WorkFolderPrefix + Guid.NewGuid().ToString("N"));
                Directory.Move(oldPath, work);
                moved = true;
                Directory.Move(work, newPath);
                work = null;
                moved = false;
                StorageNames.DeleteNameFile(settingsBase, existing.StorageName);
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the folder back where it was and drop the new sidecar; a failed rename leaves the base as it was.
            if (moved && work is not null && Directory.Exists(work) && !Directory.Exists(oldPath))
            {
                Directory.Move(work, oldPath);
            }

            if (!string.Equals(existing.StorageName, newStorage, StringComparison.Ordinal) && !Directory.Exists(newPath))
            {
                StorageNames.DeleteNameFile(settingsBase, newStorage);
            }

            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{oldName}' cannot be renamed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Deletes a profile; refuses the last one.</summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="name">The profile to delete.</param>
    /// <param name="error">
    /// Why the profile was not deleted; <see langword="null"/> on success. Messages: <c>Profile 'X' does not exist.</c> and
    /// <c>Profile 'X' is the last profile and cannot be deleted.</c>.
    /// </param>
    /// <returns><see langword="true"/> when the profile was deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryDelete(string settingsBase, string name, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        Entry? existing = name is null
            ? null
            : _Find(settingsBase, name);
        if (existing is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' does not exist.");
            return false;
        }

        if (_Entries(settingsBase).Count <= 1)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' is the last profile and cannot be deleted.");
            return false;
        }

        try
        {
            Directory.Delete(Path.Combine(settingsBase, existing.StorageName), recursive: true);
            StorageNames.DeleteNameFile(settingsBase, existing.StorageName);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' cannot be deleted: {exception.Message}");
            return false;
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>A profile found on disk: its full name and the name of its folder.</summary>
    private sealed record Entry(string Name, string StorageName);

    /// <summary>Checks a name for creating or renaming; the error carries the rule.</summary>
    private static bool _CheckName(string? name, [NotNullWhen(false)] out string? error)
    {
        if (ProfileNames.TryValidate(name, out string? rule))
        {
            error = null;
            return true;
        }

        error = string.Create(CultureInfo.InvariantCulture, $"Profile name '{name}': {rule}.");
        return false;
    }

    /// <summary>Finds a profile by name ignoring case.</summary>
    private static Entry? _Find(string settingsBase, string name) =>
        _Entries(settingsBase).FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads the profile folders of the base.</summary>
    private static List<Entry> _Entries(string settingsBase)
    {
        if (!Directory.Exists(settingsBase))
        {
            return [];
        }

        List<Entry> entries = [];
        try
        {
            foreach (string path in Directory.EnumerateDirectories(settingsBase))
            {
                string folder = Path.GetFileName(path);

                // Hidden folders (work folders, ".git" and the like) and the reserved names are not profiles.
                if (folder.Length == 0 || folder[0] == '.' || _IsReserved(folder))
                {
                    continue;
                }

                if (StorageNames.IsMapped(folder))
                {
                    if (StorageNames.TryReadName(settingsBase, folder, out string? full))
                    {
                        entries.Add(new Entry(full, folder));
                    }
                }
                else if (ProfileNames.TryValidateSyntax(folder, out _))
                {
                    entries.Add(new Entry(folder, folder));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable base has no listable profiles; the caller's create or delete reports the real problem.
            return entries;
        }

        return entries;
    }

    /// <summary>Whether a folder name is one of the reserved names (ignoring case).</summary>
    private static bool _IsReserved(string folder)
    {
        foreach (string reserved in ProfileNames.Reserved)
        {
            if (string.Equals(folder, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Copies every file and folder of a profile; the lock file is not taken along.</summary>
    private static void _CopyDirectory(string source, string target)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), _LockFileName, StringComparison.Ordinal))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>Deletes a work folder if it is still there; failures are ignored (cleanup).</summary>
    private static void _DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A work folder is hidden and never listed; the next create under another name is not affected.
        }
    }

    #endregion
}
