// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Profile folders under a settings base, on <see cref="ProfileNames"/> and <see cref="StorageNames"/>. Profile names compare
/// case-insensitively. Every member validates its arguments and leaves the base unchanged on failure, except that a delete
/// which fails midway can leave a partly emptied profile folder (see <see cref="TryDelete"/>). Thread safety: the
/// members are file-system operations; concurrent creates of one name end with exactly one success, because each creator
/// builds the profile in a hidden folder of its own and moves it into place, which only one mover can do. A profile service
/// of the application adds the active-profile and in-use refusals on top.
/// </summary>
public static class SettingsProfiles
{
    #region Profile naming constants

    /// <summary>Name of the profile that is created on first use.</summary>
    public const string DefaultProfile = "default";

    /// <summary>
    /// Prefix of the hidden work folders of creates and renames. Profile names cannot start with a dot
    /// (<see cref="ProfileNames.TryValidateSyntax"/>) and the listing skips folders that start with a dot, so a work folder can
    /// never clash with a profile and is never listed, not even while it exists.
    /// </summary>
    private const string _WorkFolderPrefix = ".work-";

    #endregion

    #region Profile operations

    /// <summary>
    /// Lists the profiles: folders under the base that are not reserved, not hidden, and whose name is valid (a mapped folder
    /// needs a readable sidecar). A base that does not exist has no profiles.
    /// </summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="layout">The layout of the application: its reserved folders are no profiles and its lock file is not copied.</param>
    /// <returns>The profile names, sorted ignoring case.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is null, empty, or white space.</exception>
    public static IReadOnlyList<string> List(string settingsBase, SettingsStorageLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        ArgumentNullException.ThrowIfNull(layout);
        return [.. _Entries(settingsBase, layout).Select(static entry => entry.Name)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static name => name, StringComparer.Ordinal)];
    }

    /// <summary>Whether a profile exists (names compare ignoring case; an invalid or <see langword="null"/> name does not exist).</summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="layout">The layout of the application: its reserved folders are no profiles and its lock file is not copied.</param>
    /// <param name="name">The profile name.</param>
    /// <returns><see langword="true"/> when the profile exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is null, empty, or white space.</exception>
    public static bool Exists(string settingsBase, SettingsStorageLayout layout, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        ArgumentNullException.ThrowIfNull(layout);
        return name is not null && _Find(settingsBase, layout, name) is not null;
    }

    /// <summary>Creates an empty profile or a copy of <paramref name="copyFrom"/> (every file, no limits).</summary>
    /// <param name="settingsBase">The settings base folder; it is created when it does not exist.</param>
    /// <param name="layout">The layout of the application: its reserved folders are no profiles and its lock file is not copied.</param>
    /// <param name="name">The new profile name; the rules of <see cref="ProfileNames.TryValidate"/> apply.</param>
    /// <param name="copyFrom">An existing profile whose files are copied, or <see langword="null"/> for an empty profile.</param>
    /// <param name="error">
    /// Why the profile was not created; <see langword="null"/> on success. Messages: <c>Profile 'X' already exists.</c>,
    /// <c>Profile 'X' does not exist.</c> (the source), <c>Profile name 'X': &lt;rule&gt;.</c>, or a file-system message.
    /// </param>
    /// <returns><see langword="true"/> when the profile was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryCreate(string settingsBase, SettingsStorageLayout layout, string name, string? copyFrom, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        ArgumentNullException.ThrowIfNull(layout);
        if (!_CheckName(name, layout, out error))
        {
            return false;
        }

        // The source must exist and the target must not (case-insensitively); both before anything is written.
        Entry? source = null;
        if (copyFrom is not null)
        {
            source = _Find(settingsBase, layout, copyFrom);
            if (source is null)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"Profile '{copyFrom}' does not exist.");
                return false;
            }
        }

        if (_Find(settingsBase, layout, name) is not null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' already exists.");
            return false;
        }

        // The profile is built in a hidden work folder with a unique name and only then moved into place. A reader or a
        // concurrent creator therefore never sees a half-copied profile, and the move is the single point at which the name is
        // claimed. The naive alternative, creating the final folder first and filling it, would let a crash or a competing
        // create leave or mix a partly filled profile under the real name.
        string storageName = StorageNames.ToStorageName(name);
        string work = Path.Combine(settingsBase, _WorkFolderPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            // The base must exist before the sidecar below can be written into it.
            Directory.CreateDirectory(settingsBase);
            Directory.CreateDirectory(work);
            if (source is not null)
            {
                _CopyDirectory(Path.Combine(settingsBase, source.StorageName), work, layout.LockFileName);
            }

            // The sidecar goes first, so a listing never sees a mapped folder without its name. A failed write (including a
            // hash collision with another name) returns here; the finally block removes the work folder. A creator never
            // rewrites a sidecar that exists (overwriteCaseOnlyDifference: false): the loser of a race for the same name must
            // not change the spelling that the winner wrote.
            if (!StorageNames.TryWriteNameFile(settingsBase, name, StorageNames.ToStorageName, overwriteCaseOnlyDifference: false, out string? sidecarProblem))
            {
                error = sidecarProblem;
                return false;
            }

            // Only one mover can win: Directory.Move refuses an existing target folder with an IOException. A loser sees the
            // folder of the winner and reports the profile as existing (first catch block); the winner's sidecar stays.
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
            // After a successful move the work folder no longer exists and this is a no-op; on every failure path it removes
            // the partly copied profile.
            _DeleteDirectoryQuietly(work);
        }
    }

    /// <summary>Renames a profile (also a case-only rename), moving folder and sidecar.</summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="layout">The layout of the application: its reserved folders are no profiles and its lock file is not copied.</param>
    /// <param name="oldName">The existing profile.</param>
    /// <param name="newName">The new name; the rules of <see cref="ProfileNames.TryValidate"/> apply.</param>
    /// <param name="error">Why the profile was not renamed; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the profile was renamed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryRename(string settingsBase, SettingsStorageLayout layout, string oldName, string newName, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        ArgumentNullException.ThrowIfNull(layout);
        Entry? existing = oldName is null
            ? null
            : _Find(settingsBase, layout, oldName);
        if (existing is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{oldName}' does not exist.");
            return false;
        }

        if (!_CheckName(newName, layout, out error))
        {
            return false;
        }

        // A name that already exists is refused, except for the profile itself (a case-only rename): names are unique
        // ignoring case, so a match with another spelling of the old name is the same profile.
        Entry? clash = _Find(settingsBase, layout, newName);
        if (clash is not null && !string.Equals(clash.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{newName}' already exists.");
            return false;
        }

        // 'work' and 'moved' track the intermediate state of the two-step folder move, so that the catch block can undo it.
        string newStorage = StorageNames.ToStorageName(newName);
        string oldPath = Path.Combine(settingsBase, existing.StorageName);
        string newPath = Path.Combine(settingsBase, newStorage);
        string? work = null;
        bool moved = false;
        try
        {
            // The new sidecar is written before the folder moves, so a listing never sees the new folder without its name. For
            // a mapped case-only rename the storage name does not change (it is built from the lower-case prefix and the hash
            // of the upper-case name), so this call only rewrites the sidecar with the new spelling and no folder moves.
            if (!StorageNames.TryWriteNameFile(settingsBase, newName, out string? sidecarProblem))
            {
                error = sidecarProblem;
                return false;
            }

            // The folder moves in two steps through a hidden name, so that a case-only rename of a direct name works on file
            // systems that ignore case (the old and the new path then name the same folder). The same path is taken for every
            // different storage name, not only for case-only renames.
            if (!string.Equals(existing.StorageName, newStorage, StringComparison.Ordinal))
            {
                work = Path.Combine(settingsBase, _WorkFolderPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.Move(oldPath, work);
                moved = true;
                Directory.Move(work, newPath);
                work = null;
                moved = false;

                // The old sidecar belongs to the old storage name; a leftover would be ignored but is removed to stay tidy.
                StorageNames.DeleteNameFile(settingsBase, existing.StorageName);
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the folder back where it was and drop the new sidecar; a failed rename leaves the base as it was. 'moved' is
            // true only between the two Directory.Move calls, i.e. while the profile sits in the hidden work folder. The
            // guards keep the undo from touching a folder that another process has put at the old path in the meantime.
            // The undo itself can fail (the folder is in use, the path was taken, access was lost); that failure must not escape
            // from this Try member and replace the error that caused the rename to fail, so it is folded into the message.
            string restoreNote = string.Empty;
            if (moved && work is not null && Directory.Exists(work) && !Directory.Exists(oldPath)
                && !TryRestoreFolder(work, oldPath))
            {
                restoreNote = string.Create(CultureInfo.InvariantCulture,
                    $" The profile folder could not be put back; it is kept, hidden, as '{Path.GetFileName(work)}'.");
            }

            // The new sidecar is dropped only when no folder carries the new storage name; otherwise it belongs to that folder.
            if (!string.Equals(existing.StorageName, newStorage, StringComparison.Ordinal) && !Directory.Exists(newPath))
            {
                StorageNames.DeleteNameFile(settingsBase, newStorage);
            }

            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{oldName}' cannot be renamed: {exception.Message}{restoreNote}");
            return false;
        }
    }

    /// <summary>
    /// Deletes a profile; refuses the last one, also when two callers delete the last two profiles at the same time (the
    /// folder is moved out of the listing before the remaining profiles are counted). A profile that is in use is refused
    /// without changes where the file system refuses to move its folder.
    /// </summary>
    /// <param name="settingsBase">The settings base folder.</param>
    /// <param name="layout">The layout of the application: its reserved folders are no profiles and its lock file is not copied.</param>
    /// <param name="name">The profile to delete.</param>
    /// <param name="error">
    /// Why the profile was not deleted; <see langword="null"/> on success. Messages: <c>Profile 'X' does not exist.</c> and
    /// <c>Profile 'X' is the last profile and cannot be deleted.</c>.
    /// </param>
    /// <returns><see langword="true"/> when the profile was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="settingsBase"/> is empty.</exception>
    public static bool TryDelete(string settingsBase, SettingsStorageLayout layout, string name, [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsBase);
        ArgumentNullException.ThrowIfNull(layout);
        Entry? existing = name is null
            ? null
            : _Find(settingsBase, layout, name);
        if (existing is null)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{name}' does not exist.");
            return false;
        }

        // At least one listable profile must remain. This first count only refuses early; it is not atomic with the delete,
        // because this class takes no lock across profiles: two concurrent deletes of the last two profiles could both pass
        // it. The count after the claim below is the one that decides.
        if (_Entries(settingsBase, layout).Count <= 1)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' is the last profile and cannot be deleted.");
            return false;
        }

        // Claim the profile first by moving its folder to a hidden work folder (one atomic rename: of two deleters of the same
        // profile only one succeeds, and the listing no longer shows the profile), then count what is still listed. Each of two
        // concurrent deleters counts only after its own folder has left the listing, so the later counter always misses the
        // other's folder and cannot take away the last profile; at worst both refuse. The undo is a move back.
        string profilePath = Path.Combine(settingsBase, existing.StorageName);
        string work = Path.Combine(settingsBase, _WorkFolderPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            Directory.Move(profilePath, work);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing was changed; a profile that is in use (a file inside is open) is refused here, before anything is deleted.
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' cannot be deleted: {exception.Message}");
            return false;
        }

        if (_Entries(settingsBase, layout).Count == 0)
        {
            string stuck = TryRestoreFolder(work, profilePath)
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $" The profile folder could not be put back; it is kept, hidden, as '{Path.GetFileName(work)}'.");
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' is the last profile and cannot be deleted.{stuck}");
            return false;
        }

        try
        {
            // The folder goes first and the sidecar second: a failure in between leaves a harmless sidecar, never a mapped
            // folder without its name.
            Directory.Delete(work, recursive: true);
            StorageNames.DeleteNameFile(settingsBase, existing.StorageName);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A recursive delete is not atomic: a failure midway (for example a file in use) can leave a partly emptied folder.
            // It is moved back so that it stays listed (its sidecar was not touched) and the delete can be retried; when even
            // that fails, it stays in the hidden work folder and the message names it.
            string stuck = TryRestoreFolder(work, profilePath)
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $" The remains are kept, hidden, as '{Path.GetFileName(work)}'.");
            error = string.Create(CultureInfo.InvariantCulture, $"Profile '{existing.Name}' cannot be deleted: {exception.Message}{stuck}");
            return false;
        }
    }

    #endregion

    #region Folder helpers

    /// <summary>A profile found on disk: its full name and the name of its folder.</summary>
    /// <param name="Name">The full profile name.</param>
    /// <param name="StorageName">The name of the profile folder, which differs from the name for a long or device-like name.</param>
    private sealed record Entry(string Name, string StorageName);

    /// <summary>Checks a name for creating or renaming; the error carries the rule.</summary>
    /// <param name="name">The name to check; may be <see langword="null"/>.</param>
    /// <param name="layout">The layout whose reserved folder names a profile must not use.</param>
    /// <param name="error">The reason, with the rule, when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the name may be used for a profile.</returns>
    private static bool _CheckName(string? name, SettingsStorageLayout layout, [NotNullWhen(false)] out string? error)
    {
        if (ProfileNames.TryValidate(name, layout, out string? rule))
        {
            error = null;
            return true;
        }

        error = string.Create(CultureInfo.InvariantCulture, $"Profile name '{name}': {rule}.");
        return false;
    }

    /// <summary>Finds a profile by name ignoring case.</summary>
    /// <param name="settingsBase">The folder that holds the profiles.</param>
    /// <param name="layout">The layout of the application.</param>
    /// <param name="name">The profile name.</param>
    /// <returns>The profile, or <see langword="null"/> when no profile has this name.</returns>
    private static Entry? _Find(string settingsBase, SettingsStorageLayout layout, string name) =>
        _Entries(settingsBase, layout).FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the profile folders of the base on every call (no cache): the folders are the only source of truth, so other
    /// processes and the other members of this class always see the current state. A mapped folder (long or device-like
    /// name) is listed only when its sidecar yields the full name; any other folder only when its name is valid syntax.
    /// </summary>
    /// <param name="settingsBase">The folder that holds the profiles.</param>
    /// <param name="layout">The layout of the application; its reserved folder names are not profiles.</param>
    /// <returns>The profiles found now, in no fixed order.</returns>
    private static List<Entry> _Entries(string settingsBase, SettingsStorageLayout layout)
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
                if (folder.Length == 0 || folder[0] == '.' || _IsReserved(folder, layout))
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
            // An unreadable base ends the listing; the entries read so far are returned. The caller's create or delete reports
            // the real problem.
            return entries;
        }

        return entries;
    }

    /// <summary>Whether a folder name is one of the reserved names (ignoring case).</summary>
    /// <param name="folder">The folder name, without path.</param>
    /// <param name="layout">The layout that lists the reserved names.</param>
    /// <returns><see langword="true"/> when the folder is reserved and so no profile.</returns>
    private static bool _IsReserved(string folder, SettingsStorageLayout layout)
    {
        foreach (string reserved in layout.ReservedFolderNames)
        {
            if (string.Equals(folder, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies every file and folder of a profile except the lock file. The owner of a profile holds its lock file open with
    /// <see cref="FileShare.None"/>, so copying it would fail for a profile in use, and a copied lock file would mean nothing
    /// in the new folder. The source is the user's own profile, so the copy has no size or count limit; an I/O failure surfaces
    /// as an exception that <see cref="TryCreate"/> turns into an error.
    /// </summary>
    /// <param name="source">The profile folder to copy.</param>
    /// <param name="target">The existing, empty folder to copy into.</param>
    /// <param name="lockFileName">The lock file name of the layout (<see cref="SettingsStorageLayout.LockFileName"/>), matched ordinally.</param>
    private static void _CopyDirectory(string source, string target, string lockFileName)
    {
        // Folders first, so that every file below finds its parent folder in the target.
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), lockFileName, StringComparison.Ordinal))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>
    /// Moves a folder back to the path it was taken from (the undo of a rename or delete). Reports failure instead of throwing,
    /// because it runs while another failure is being reported and must not replace it.
    /// </summary>
    /// <param name="work">The folder as it is now (a hidden work folder).</param>
    /// <param name="originalPath">The path to put it back to; it must not exist.</param>
    /// <returns><see langword="true"/> when the folder is back at <paramref name="originalPath"/>.</returns>
    internal static bool TryRestoreFolder(string work, string originalPath)
    {
        try
        {
            Directory.Move(work, originalPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes a work folder if it is still there; failures are ignored (cleanup).</summary>
    /// <param name="path">The folder to delete.</param>
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
