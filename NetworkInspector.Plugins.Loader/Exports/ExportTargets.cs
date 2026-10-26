// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Export target rules of the application side, shared by every export runner (the command line loops and the host export
/// job). Overwrite is the caller's decision (<c>--overwrite</c> of the command line, the <c>overwrite</c> parameter of the
/// host), not an exporter option, and the check happens before the exporter is created so that a refused export never
/// touches an existing output. Stateless; thread-safe (the file system arbitrates concurrent callers).
/// </summary>
public static class ExportTargets
{
    #region Constants

    /// <summary>Option name used in the errors about the target.</summary>
    private const string _TargetOption = "target";

    /// <summary>
    /// Name of the empty marker file that <see cref="MarkDataset"/> writes into a dataset folder. A non-empty folder is replaced
    /// by <c>--overwrite</c> only when it carries the marker, so a mistyped target cannot delete data that no export wrote.
    /// </summary>
    /// <remarks>
    /// Security decision. Threat: an overwrite request with a mistyped or hostile target path removes a folder of user data.
    /// Mitigation: only an empty folder or a folder that an earlier export marked is replaced, and the old content is moved
    /// aside first (see <see cref="TargetReplacement"/>), never deleted before the new output exists. Rejected: refusing every
    /// existing folder, which would make a repeated dataset export impossible. Accepted residual risk: the marker is a plain
    /// file name, so a user (or a tool) that creates it by hand declares the folder replaceable.
    /// </remarks>
    public const string DatasetMarkerFileName = ".ni-export";

    #endregion

    #region Public API

    /// <summary>
    /// Normalizes <paramref name="targetPath"/> to a full path (a relative path is resolved against the current directory),
    /// checks the parent folder, and applies the overwrite rule: an existing target is refused unless
    /// <paramref name="overwrite"/> is set (then it is moved aside, see <paramref name="replacement"/>); a folder where a file
    /// target is wanted, or a file where a directory target is wanted, is always refused. Overwriting never replaces a folder
    /// that contains the current directory, and a folder is replaced only when it is empty or an earlier export marked it
    /// (<see cref="DatasetMarkerFileName"/>), so a mistyped target cannot delete the place the process works in or data that
    /// no export wrote.
    /// </summary>
    /// <param name="targetPath">Output path as given by the user; <see langword="null"/> or blank gives <see cref="PluginErrorCode.InvalidOption"/>.</param>
    /// <param name="isDirectory"><see langword="true"/> for directory formats such as Parquet.</param>
    /// <param name="overwrite">Whether an existing target of the right kind may be replaced.</param>
    /// <param name="fullPath">The normalized path to hand to the exporter; <see langword="null"/> on failure.</param>
    /// <param name="replacement">
    /// The old output that was moved aside so that the path is free; <see langword="null"/> when nothing was replaced. The
    /// caller must call <see cref="TargetReplacement.Settle"/> when the run has ended: the old output is deleted when a new one
    /// exists and comes back when it does not.
    /// </param>
    /// <param name="error">
    /// Why the target cannot be used; <see langword="null"/> on success. <see cref="PluginErrorCode.TargetExists"/>
    /// ("Output '&lt;full path&gt;' exists." or a folder that no export wrote), <see cref="PluginErrorCode.FileNotFound"/> (the
    /// parent folder is missing), <see cref="PluginErrorCode.FileUnreadable"/> (the old target could not be moved aside), or
    /// <see cref="PluginErrorCode.InvalidOption"/> (an invalid path).
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="fullPath"/> is ready for the exporter.</returns>
    public static bool TryPrepare(string targetPath, bool isDirectory, bool overwrite, [NotNullWhen(true)] out string? fullPath,
        out TargetReplacement? replacement, [NotNullWhen(false)] out PluginError? error)
    {
        // The checks run first and change nothing; only a passed check with an existing target reaches the move below.
        replacement = null;
        if (!_TryCheck(targetPath, isDirectory, overwrite, out fullPath, out bool replace, out error))
        {
            return false;
        }

        if (replace)
        {
            // Move, never delete: the old output must survive until the new one is complete. The backup lies next to the target,
            // so the move stays on one volume and is atomic. The unique suffix keeps two replacements of the same target (or a
            // leftover of a crashed run) from colliding.
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("_TryCheck guarantees a parent folder.");
            string backup = Path.Combine(parent, "." + Path.GetFileName(fullPath) + ".ni-replaced-" + Guid.NewGuid().ToString("N"));
            try
            {
                MoveOrThrow(fullPath, backup, isDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A locked or protected old output: the old output is untouched, the caller gets no path and nothing to settle.
                error = new PluginError(PluginErrorCode.FileUnreadable, Invariant($"Cannot replace output '{fullPath}': {exception.Message}"),
                    _TargetOption);
                fullPath = null;
                return false;
            }

            // From here the target path is free and the old output is recoverable until the caller settles the replacement.
            replacement = new TargetReplacement(fullPath, backup, isDirectory);
        }

        return true;
    }

    /// <summary>
    /// Marks the folder of a directory export as written by an export (creates the folder when it is missing). Call it right
    /// after the listener was created.
    /// </summary>
    /// <param name="fullPath">The full path returned by <see cref="TryPrepare"/>.</param>
    /// <returns><see langword="null"/> on success; otherwise a one-line problem text.</returns>
    /// <exception cref="ArgumentException"><paramref name="fullPath"/> is empty or blank.</exception>
    public static string? MarkDataset(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        try
        {
            // The marker is written right after the listener exists, before any data: an export that is cancelled or fails
            // later still leaves a folder that a following overwrite recognizes as its own. Idempotent for a re-marked folder.
            Directory.CreateDirectory(fullPath);
            string marker = Path.Combine(fullPath, DatasetMarkerFileName);
            if (!File.Exists(marker))
            {
                File.WriteAllBytes(marker, []);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Expected failure of the file system (read-only volume, permissions): reported as text, not thrown.
            return exception.Message;
        }
    }

    /// <summary>
    /// Applies the rules of <see cref="TryPrepare"/> without changing anything: use it to refuse a bad output before a long run
    /// starts. With <paramref name="overwrite"/> an existing target of the right kind passes (it is moved aside later, by
    /// <see cref="TryPrepare"/>, when the output is really opened).
    /// </summary>
    /// <param name="targetPath">Output path as given by the user.</param>
    /// <param name="isDirectory"><see langword="true"/> for directory formats such as Parquet.</param>
    /// <param name="overwrite">Whether an existing target of the right kind is acceptable.</param>
    /// <param name="fullPath">The normalized path; <see langword="null"/> on failure.</param>
    /// <param name="error">Why the target cannot be used; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the target passes.</returns>
    public static bool TryCheck(string targetPath, bool isDirectory, bool overwrite, [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out PluginError? error) =>
        _TryCheck(targetPath, isDirectory, overwrite, out fullPath, out _, out error);

    /// <summary>
    /// The shared rules of <see cref="TryPrepare"/> and <see cref="TryCheck"/>; it changes nothing on disk.
    /// <paramref name="replace"/> tells whether an existing target has to be moved aside before the exporter may use the path.
    /// </summary>
    private static bool _TryCheck(string targetPath, bool isDirectory, bool overwrite, [NotNullWhen(true)] out string? fullPath,
        out bool replace, [NotNullWhen(false)] out PluginError? error)
    {
        // Normalize first; every later check works on the full path.
        replace = false;
        fullPath = null;
        if (!_TryNormalize(targetPath, out string? normalized, out error))
        {
            return false;
        }

        // The parent folder must exist: exporters create files, not folder trees. A root has no parent and no use as target.
        string? parent = Path.GetDirectoryName(normalized);
        if (parent is null)
        {
            error = new PluginError(PluginErrorCode.InvalidOption, Invariant($"'{normalized}' has no parent folder."), _TargetOption);
            return false;
        }

        if (!Directory.Exists(parent))
        {
            error = new PluginError(PluginErrorCode.FileNotFound, Invariant($"Folder '{parent}' does not exist."), _TargetOption);
            return false;
        }

        // A thing of the wrong kind at the target is refused whatever the overwrite flag says: replacing a folder by a file
        // (or the reverse) is never what "overwrite the output" means.
        bool fileExists = File.Exists(normalized);
        bool directoryExists = Directory.Exists(normalized);
        if (!isDirectory && directoryExists)
        {
            error = new PluginError(PluginErrorCode.TargetExists, Invariant($"Output '{normalized}' is a folder, not a file."),
                _TargetOption);
            return false;
        }

        if (isDirectory && fileExists)
        {
            error = new PluginError(PluginErrorCode.TargetExists, Invariant($"Output '{normalized}' is a file, not a folder."),
                _TargetOption);
            return false;
        }

        // Nothing there: the target is free.
        if (!fileExists && !directoryExists)
        {
            fullPath = normalized;
            error = null;
            return true;
        }

        // Something of the right kind is there: only the overwrite flag may replace it.
        if (!overwrite)
        {
            error = new PluginError(PluginErrorCode.TargetExists, Invariant($"Output '{normalized}' exists."), _TargetOption);
            return false;
        }

        // Replacing the folder the process works in would pull the ground away from every relative path of this process (and a
        // typo such as "." or ".." resolves to exactly that place). Refused whatever the content.
        if (directoryExists && _ContainsCurrentDirectory(normalized))
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"Output '{normalized}' contains the current directory and is not replaced."), _TargetOption);
            return false;
        }

        // A folder is replaced only when it is empty or an earlier export marked it; anything else is somebody's data.
        if (directoryExists && !_MayReplaceDirectory(normalized))
        {
            error = new PluginError(PluginErrorCode.TargetExists,
                Invariant($"Output folder '{normalized}' is not empty and was not written by an export; remove it yourself or choose another folder."),
                _TargetOption);
            return false;
        }

        // All checks passed. The check and the later move are separate file system calls, so a folder that gains files in
        // between is still moved, not deleted: the content stays recoverable at the backup path until the replacement settles.
        replace = true;
        fullPath = normalized;
        error = null;
        return true;
    }

    /// <summary>
    /// Removes the output of an export that ended without <c>OnFinish</c> (cancelled or failed). Call it after the listener
    /// was disposed: the Core exporters finish their file on <c>Dispose</c>, so the runner deletes the result to meet the
    /// contract rule that an unfinished export leaves no partial output. A plugin listener that already deleted its output
    /// makes this a no-op. The method reports a problem as text instead of throwing, because it runs on cleanup paths that
    /// must not hide the original failure.
    /// </summary>
    /// <param name="fullPath">The path returned by <see cref="TryPrepare"/>.</param>
    /// <param name="isDirectory"><see langword="true"/> for a directory output.</param>
    /// <returns><see langword="null"/> when nothing remains; otherwise a one-line problem text.</returns>
    /// <exception cref="ArgumentException"><paramref name="fullPath"/> is empty or blank.</exception>
    public static string? DeletePartial(string fullPath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // The same guard as for overwrite: the method only removes what an export created, never the working place. Only a
        // directory can contain the current directory. There is no marker check here on purpose: a path that reaches this
        // method was free, or its old content was moved aside, when TryPrepare approved it, so what lies there now was
        // written by this export attempt.
        if (isDirectory && _ContainsCurrentDirectory(fullPath))
        {
            return Invariant($"Output '{fullPath}' contains the current directory and is not removed.");
        }

        return _Delete(fullPath, isDirectory);
    }

    #endregion

    #region Private Helpers

    /// <summary>Turns the user's text into a full path or an <see cref="PluginErrorCode.InvalidOption"/> error.</summary>
    /// <param name="targetPath">Output path as given by the user.</param>
    /// <param name="normalized">The full path without a trailing separator; <see langword="null"/> on failure.</param>
    /// <param name="error">The error; <see langword="null"/> on success.</param>
    private static bool _TryNormalize(string targetPath, [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out PluginError? error)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            error = new PluginError(PluginErrorCode.InvalidOption, "An output path is required.", _TargetOption);
            return false;
        }

        try
        {
            // A trailing separator ("out/") would make the target its own parent folder; roots keep theirs.
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = new PluginError(PluginErrorCode.InvalidOption,
                Invariant($"'{targetPath}' is not a valid output path: {exception.Message}"), _TargetOption);
            return false;
        }
    }

    /// <summary>Whether the current directory lies inside <paramref name="directory"/> (or is it).</summary>
    private static bool _ContainsCurrentDirectory(string directory)
    {
        string current = Path.GetFullPath(Directory.GetCurrentDirectory());

        // Both sides end with a separator, so "/data/out" does not match the unrelated sibling "/data/out2"; the equality case
        // (the current directory is the folder itself) matches too. The comparison is textual: symbolic links are not
        // resolved, so a path that reaches the working directory only through a link is not detected.
        string prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        // Windows and macOS file systems ignore case by default; comparing case-sensitively there could miss the match.
        return (current + Path.DirectorySeparatorChar).StartsWith(prefix, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    /// <summary>Whether <c>--overwrite</c> may replace the folder: it is empty, or an earlier export marked it.</summary>
    private static bool _MayReplaceDirectory(string directory)
    {
        try
        {
            // The marker is tested first because it is one cheap lookup; the emptiness test only enumerates until the first
            // entry (lazy enumeration), so even a huge folder costs one read.
            return File.Exists(Path.Combine(directory, DatasetMarkerFileName))
                || !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed is not proven to be an export.
            return false;
        }
    }

    /// <summary>
    /// Moves a file or a folder; used for the backup of a replaced output and its restore. I/O errors reach the caller, which
    /// turns them into the text that names where the old output is kept.
    /// </summary>
    /// <param name="from">Existing path.</param>
    /// <param name="to">Free path on the same volume.</param>
    /// <param name="isDirectory"><see langword="true"/> when the path is a folder.</param>
    internal static void MoveOrThrow(string from, string to, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(from, to);
            return;
        }

        File.Move(from, to);
    }

    /// <summary>Deletes a file or a folder tree and lets I/O errors through to the caller.</summary>
    /// <param name="path">Existing path of the old output (the backup).</param>
    /// <param name="isDirectory"><see langword="true"/> when the path is a folder.</param>
    internal static void DeleteOrThrow(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }

    /// <summary>Deletes a file or a directory tree; returns a problem text instead of throwing, <see langword="null"/> on success.</summary>
    /// <param name="path">Output path to remove; a missing path is not a problem.</param>
    /// <param name="isDirectory"><see langword="true"/> when the output is a folder.</param>
    private static string? _Delete(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            else
            {
                // File.Delete does nothing for a missing file, which is the no-op the callers rely on.
                File.Delete(path);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    #endregion
}
