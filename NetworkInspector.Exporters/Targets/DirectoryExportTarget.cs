// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// A target whose named entries are files below one folder: <c>fields/field_12.parquet</c> becomes
/// <c>{root}/fields/field_12.parquet</c>. Folders are created on the first open that needs them, so a target that opens nothing
/// leaves no folder. Each entry opens once; an existing file is truncated.
/// <para>
/// <b>Thread safety:</b> thread-safe. The set of opened names is guarded by a <see cref="Lock"/> whose section holds no I/O; the
/// file system arbitrates the opens themselves.
/// </para>
/// </summary>
/// <remarks>
/// Security: the entry grammar keeps every name relative and free of <c>..</c> (<see cref="ExportEntryName"/>); the target also
/// resolves the full path and confirms it stays below the root (defence in depth against a future grammar change). Rejected:
/// trusting the grammar alone. Residual risk: a symbolic link inside the root that points elsewhere is followed.
/// </remarks>
/// <seealso cref="IExportTarget"/>
/// <seealso cref="FileExportTarget"/>
public sealed class DirectoryExportTarget : IExportTarget
{
    #region Configuration and state

    /// <summary>Guards <see cref="_Opened"/>; the critical section is two set operations and no I/O.</summary>
    private readonly Lock _Gate = new();

    /// <summary>The entries opened so far, so a second open of one name is refused instead of truncating its file.</summary>
    private readonly HashSet<ExportEntryName> _Opened = [];

    /// <summary>The root with a trailing separator, for the prefix check of a resolved entry path.</summary>
    private readonly string _RootWithSeparator;

    /// <summary>Creates the target; no folder is touched.</summary>
    /// <param name="rootPath">The folder that receives the entries; resolved to a full path now.</param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is empty or not a valid path.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="rootPath"/> is <see langword="null"/>.</exception>
    public DirectoryExportTarget(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        // TrimEndingDirectorySeparator keeps the separator of a file-system root ("C:\" or "/"). Appending another would make
        // the prefix check refuse every entry of a root folder, so a separator is added only when the path has none.
        _RootWithSeparator = Path.EndsInDirectorySeparator(RootPath)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;
    }

    /// <summary>The full path of the root folder, without a trailing separator.</summary>
    public string RootPath { get; }

    #endregion

    #region Opening

    /// <summary>Creates the folders of the entry as needed and opens its file for writing.</summary>
    /// <param name="name">A named entry; <see cref="ExportEntryName.Main"/> is refused.</param>
    /// <param name="stream">A write-only file stream on success.</param>
    /// <param name="failure">The reason on failure: the main entry, an escaping path, a second open, or the operating system's error.</param>
    /// <returns><see langword="true"/> when the file was created and <paramref name="stream"/> is set.</returns>
    public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
    {
        stream = null;
        if (name.IsMain)
        {
            failure = "A directory target holds named entries; the single output of a file format needs a FileExportTarget.";
            return false;
        }

        // Security: resolve and confirm the path stays below the root before anything is reserved or created.
        string path = Path.GetFullPath(Path.Join(RootPath, name.Text.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(_RootWithSeparator, comparison))
        {
            failure = Invariant($"Entry '{name}' resolves outside the target folder.");
            return false;
        }

        // Reserve the name; the lock section is one set operation, no I/O.
        lock (_Gate)
        {
            if (!_Opened.Add(name))
            {
                failure = Invariant($"Entry '{name}' was already opened.");
                return false;
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); // Invariant: path lies below the root, so it has a parent.
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException)
        {
            // Release the reservation, so the set says what is open and no invalid state remains after the failure.
            lock (_Gate)
            {
                _Opened.Remove(name);
            }

            failure = exception.Message;
            return false;
        }
    }

    #endregion
}
