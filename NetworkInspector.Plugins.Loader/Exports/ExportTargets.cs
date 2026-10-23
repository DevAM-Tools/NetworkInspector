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
    /// <param name="targetPath">Output path as given by the user.</param>
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
        replacement = null;
        if (!_TryCheck(targetPath, isDirectory, overwrite, out fullPath, out bool replace, out error))
        {
            return false;
        }

        if (replace)
        {
            // Move, never delete: the old output must survive until the new one is complete. The backup lies next to the target,
            // so the move stays on one volume and is atomic.
            string parent = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("_TryCheck guarantees a parent folder.");
            string backup = Path.Combine(parent, "." + Path.GetFileName(fullPath) + ".ni-replaced-" + Guid.NewGuid().ToString("N"));
            try
            {
                MoveOrThrow(fullPath, backup, isDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = new PluginError(PluginErrorCode.FileUnreadable, Invariant($"Cannot replace output '{fullPath}': {exception.Message}"),
                    _TargetOption);
                fullPath = null;
                return false;
            }

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

    /// <summary>The shared rules; <paramref name="replace"/> tells whether an existing target has to be deleted.</summary>
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

        // The same guard as for overwrite: the method only removes what an export created, never the working place.
        if (isDirectory && _ContainsCurrentDirectory(fullPath))
        {
            return Invariant($"Output '{fullPath}' contains the current directory and is not removed.");
        }

        return _Delete(fullPath, isDirectory);
    }

    #endregion

    #region Private Helpers

    /// <summary>Turns the user's text into a full path or an <see cref="PluginErrorCode.InvalidOption"/> error.</summary>
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
            return File.Exists(Path.Combine(directory, DatasetMarkerFileName))
                || !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed is not proven to be an export.
            return false;
        }
    }

    /// <summary>Moves a file or a folder; used for the backup of a replaced output and its restore.</summary>
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
