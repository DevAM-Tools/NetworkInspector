// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// An existing output that <see cref="ExportTargets.TryPrepare"/> moved aside so that the new export can use its path. The old
/// output stays recoverable until <see cref="Settle"/> decides: a new output that exists after the run replaced the old one for
/// good (the backup is deleted); a missing one (cancelled, failed, never opened) gets the old output back.
/// <para>
/// The decision is made from the existence of the new output alone. A run that ended without finishing its output must
/// therefore remove the unfinished output (<see cref="ExportTargets.DeletePartial"/>) before it settles, or a partial file
/// would count as the new output and the old one would be deleted.
/// </para>
/// <para>Thread safety: <see cref="Settle"/> may be called from any thread; only the first call acts.</para>
/// </summary>
public sealed class TargetReplacement
{
    #region Fields

    // The path the new output is written to (the path the old output had before it was moved aside).
    private readonly string _Target;

    // Where the old output waits: a dot-prefixed sibling of the target, so on the same volume.
    private readonly string _Backup;

    private readonly bool _IsDirectory;

    // 0 = open, 1 = settled. Only touched through Interlocked, so it needs no lock and no volatile.
    private int _Settled;

    #endregion

    #region Lifecycle

    /// <summary>Records a replacement that <see cref="ExportTargets.TryPrepare"/> has already performed (the old output is moved).</summary>
    /// <param name="target">Full path of the target.</param>
    /// <param name="backup">Full path the old output was moved to.</param>
    /// <param name="isDirectory"><see langword="true"/> when the output is a folder.</param>
    internal TargetReplacement(string target, string backup, bool isDirectory)
    {
        _Target = target;
        _Backup = backup;
        _IsDirectory = isDirectory;
    }

    #endregion

    #region Public API

    /// <summary>Deletes the backup when the new output exists, otherwise moves the backup back to the target path.</summary>
    /// <returns><see langword="null"/> on success; otherwise a one-line problem text that names where the old output is kept.</returns>
    public string? Settle()
    {
        // Exactly-once guard: the action moves or deletes the backup, so a second call (a runner that settles on two paths) must
        // not act on a backup that was already deleted or moved back. A failure of the first call is therefore not retried; the
        // returned text names where the old output is kept.
        if (Interlocked.Exchange(ref _Settled, 1) != 0)
        {
            return null;
        }

        // Existence decides: a new output means the run produced a result (the runner removes unfinished outputs before this
        // call), no output means the old one is restored.
        bool newOutputExists = _IsDirectory
            ? Directory.Exists(_Target)
            : File.Exists(_Target);
        try
        {
            if (newOutputExists)
            {
                ExportTargets.DeleteOrThrow(_Backup, _IsDirectory);
                return null;
            }

            ExportTargets.MoveOrThrow(_Backup, _Target, _IsDirectory);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An expected failure of the file system; the backup is left where it is so that no data is lost, and the caller
            // reports the text as a warning.
            return Invariant($"The replaced output is kept at '{_Backup}': {exception.Message}");
        }
    }

    #endregion
}
