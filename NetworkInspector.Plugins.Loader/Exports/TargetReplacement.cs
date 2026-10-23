// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// An existing output that <see cref="ExportTargets.TryPrepare"/> moved aside so that the new export can use its path. The old
/// output stays recoverable until <see cref="Settle"/> decides: a new output that exists after the run replaced the old one for
/// good (the backup is deleted); a missing one (cancelled, failed, never opened) gets the old output back.
/// <para>Thread safety: <see cref="Settle"/> may be called from any thread; only the first call acts.</para>
/// </summary>
public sealed class TargetReplacement
{
    #region Fields

    private readonly string _Target;

    private readonly string _Backup;

    private readonly bool _IsDirectory;

    private int _Settled;

    #endregion

    #region Lifecycle

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
        if (Interlocked.Exchange(ref _Settled, 1) != 0)
        {
            return null;
        }

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
            return Invariant($"The replaced output is kept at '{_Backup}': {exception.Message}");
        }
    }

    #endregion
}
