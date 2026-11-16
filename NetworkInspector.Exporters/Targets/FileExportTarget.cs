// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// A target with one entry, <see cref="ExportEntryName.Main"/>, written to one file. The file is created (an existing one is
/// truncated) on the first <see cref="TryOpenWrite"/>, so an export that never opens its entry leaves no file. The stream is
/// a 4 MiB <see cref="BufferedStream"/> over a <see cref="FileStream"/> opened with <see cref="FileShare.None"/>.
/// <para>
/// <b>Thread safety:</b> thread-safe; the one-open flag is set with <see cref="Interlocked.Exchange(ref int, int)"/>.
/// </para>
/// </summary>
/// <seealso cref="IExportTarget"/>
/// <seealso cref="DirectoryExportTarget"/>
public sealed class FileExportTarget : IExportTarget
{
    #region Configuration and state

    /// <summary>
    /// Write buffer of the file stream: large blocks keep the number of write calls low for big exports. Perf: the buffer is
    /// allocated once per file, on the first open; its cost is 4 MiB of memory per open file.
    /// </summary>
    private const int _BufferSize = 4 * 1024 * 1024;

    /// <summary>0 until the entry was opened (or an open was attempted), then 1.</summary>
    private int _Opened;

    /// <summary>Creates the target; no file is touched.</summary>
    /// <param name="path">The output file; resolved to a full path now, so a later change of the working directory does not move it.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not a valid path.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public FileExportTarget(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // GetFullPath normalises separators and throws on illegal characters without touching the file system, so a bad path
        // surfaces here and not at the first write.
        FullPath = Path.GetFullPath(path);
    }

    /// <summary>The full path of the output file.</summary>
    public string FullPath { get; }

    #endregion

    #region Opening

    /// <summary>Opens the file on the first call; every later call, and every named entry, is refused.</summary>
    /// <param name="name">Must be <see cref="ExportEntryName.Main"/>.</param>
    /// <param name="stream">The buffered write-only file stream on success.</param>
    /// <param name="failure">The reason on failure: a named entry, a second open, or the operating system's open error.</param>
    /// <returns><see langword="true"/> when the file was created and <paramref name="stream"/> is set.</returns>
    public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
    {
        stream = null;
        if (!name.IsMain)
        {
            failure = Invariant($"A file target holds one output; entry '{name}' needs a DirectoryExportTarget.");
            return false;
        }

        // One open per target: a second open would truncate what the first stream wrote.
        if (Interlocked.Exchange(ref _Opened, 1) != 0)
        {
            failure = "The output was already opened.";
            return false;
        }

        try
        {
            stream = new BufferedStream(new FileStream(FullPath, FileMode.Create, FileAccess.Write, FileShare.None), _BufferSize);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException)
        {
            // The open failures a file system produces are expected failures of the export (missing folder, access denied).
            failure = exception.Message;
            return false;
        }
    }

    #endregion
}
