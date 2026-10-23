// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The log file <c>logs/ni.log</c> of the settings base: one line per entry (<c>2026-01-01T00:00:00.0000000Z Info source:
/// text</c>), rotated when it would grow past a size so that at most a given number of files exists. Appending never throws:
/// an I/O error is reported once to the problem sink and switches the file logging off, so a full disk or a locked file cannot
/// break a command. Thread safety: all members are guarded by one lock; several processes may append to the same file
/// (shared write access); every write goes to the current end of the file, so lines of different processes follow each other
/// whole, except when two writes start in the same instant, which can lose the earlier line.
/// </summary>
internal sealed class CliLogFile : IDisposable
{
    #region Fields

    private readonly Lock _Lock = new();

    private readonly string _Path;

    private readonly long _MaxBytes;

    private readonly int _MaxFiles;

    private readonly TimeProvider _Time;

    private FileStream? _Stream;

    private bool _Disabled;

    #endregion

    #region Lifecycle

    private CliLogFile(string path, long maxBytes, int maxFiles, TimeProvider time, FileStream stream)
    {
        _Path = path;
        _MaxBytes = maxBytes;
        _MaxFiles = maxFiles;
        _Time = time;
        _Stream = stream;
    }

    /// <summary>Disposes the file handle.</summary>
    public void Dispose()
    {
        lock (_Lock)
        {
            _Stream?.Dispose();
            _Stream = null;
        }
    }

    #endregion

    #region Public API

    /// <summary>Receives the one message that reports a failed append (the command's error writer); may stay <see langword="null"/>.</summary>
    internal Action<string>? ProblemSink { get; set; }

    /// <summary>Creates the folder and opens the file for appending.</summary>
    /// <param name="path">Full path of the current log file (<c>ni.log</c>).</param>
    /// <param name="maxBytes">Size in bytes at which the file is rotated.</param>
    /// <param name="maxFiles">Number of files kept, the current one included (at least 1).</param>
    /// <param name="time">Clock of the time stamps.</param>
    /// <param name="file">The opened file; <see langword="null"/> on failure.</param>
    /// <param name="problem">Why the file could not be opened; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when <paramref name="file"/> is set.</returns>
    internal static bool TryOpen(string path, long maxBytes, int maxFiles, TimeProvider time, [NotNullWhen(true)] out CliLogFile? file,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentNullException.ThrowIfNull(time);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            file = new CliLogFile(path, maxBytes, maxFiles, time, _OpenStream(path));
            problem = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            file = null;
            problem = string.Create(CultureInfo.InvariantCulture, $"The log file '{path}' cannot be used: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Makes a log text safe for a line-oriented sink: every control character (line breaks, tabs, and the escape character
    /// that starts terminal sequences) becomes a space, so an entry stays one line and a text cannot forge a second entry or
    /// drive the terminal. The text of a plugin may contain data from a capture or an exception message.
    /// </summary>
    /// <param name="text">The text to clean.</param>
    /// <returns>The same instance when nothing had to change, else a cleaned copy.</returns>
    internal static string SingleLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Most texts are clean; the scan finds that out without allocating.
        bool clean = true;
        foreach (char character in text)
        {
            if (char.IsControl(character))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i])
                    ? ' '
                    : source[i];
            }
        });
    }

    /// <summary>Appends one line; control characters inside the text become spaces (see <see cref="SingleLine"/>).</summary>
    /// <param name="level">Level word, for example <c>Info</c>, <c>Warning</c>, or <c>Error</c>.</param>
    /// <param name="source">Who wrote the line: a plugin name or a component.</param>
    /// <param name="text">The message.</param>
    internal void Append(string level, string source, string text)
    {
        string line = _Time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + " " + level + " " + source + ": "
            + SingleLine(text) + Environment.NewLine;
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        string? problem = null;
        lock (_Lock)
        {
            if (_Disabled || _Stream is null)
            {
                return;
            }

            try
            {
                FileStream stream = _Stream;

                // FileMode.Append seeks to the end only when the file is opened. Another process may have appended since, so
                // the end is looked up again before every line. Two processes that write within the same instant can still hit
                // the same offset; the log is best effort and says so in the class summary.
                long end = stream.Seek(0, SeekOrigin.End);
                if (end > 0 && end + bytes.Length > _MaxBytes)
                {
                    stream = _Rotate();
                    stream.Seek(0, SeekOrigin.End);
                }

                // One write per line, so lines of several processes do not interleave inside a line.
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _Disabled = true;
                problem = string.Create(CultureInfo.InvariantCulture, $"The log file '{_Path}' is switched off: {exception.Message}");
            }
        }

        if (problem is not null)
        {
            ProblemSink?.Invoke(problem);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>Opens the file for appending with shared read and write access.</summary>
    private static FileStream _OpenStream(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);


    /// <summary>
    /// Moves <c>ni.log</c> to <c>ni.1.log</c> and shifts the older files up; the oldest beyond the kept number is deleted.
    /// The caller holds the lock.
    /// </summary>
    private FileStream _Rotate()
    {
        _Stream?.Dispose();
        _Stream = null;

        string directory = Path.GetDirectoryName(_Path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(_Path);
        string extension = Path.GetExtension(_Path);
        string Numbered(int n) => Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{stem}.{n}{extension}"));

        if (_MaxFiles == 1)
        {
            // Nothing is kept besides the current file: start it afresh.
            File.Delete(_Path);
        }
        else
        {
            File.Delete(Numbered(_MaxFiles - 1));
            for (int n = _MaxFiles - 1; n >= 2; n--)
            {
                if (File.Exists(Numbered(n - 1)))
                {
                    File.Move(Numbered(n - 1), Numbered(n), overwrite: true);
                }
            }

            File.Move(_Path, Numbered(1), overwrite: true);
        }

        FileStream reopened = _OpenStream(_Path);
        _Stream = reopened;
        return reopened;
    }

    #endregion
}
