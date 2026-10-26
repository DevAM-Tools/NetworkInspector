// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI;

/// <summary>
/// The log file <c>logs/ni.log</c> of the settings base: one line per entry (<c>&lt;UTC time in round-trip format&gt; &lt;level&gt;
/// &lt;source&gt;: &lt;text&gt;</c>), rotated when it would grow past a size so that at most a given number of files exists.
/// Appending never throws: an I/O error is reported once to the problem sink and switches the file logging off, so a full disk
/// or a locked file cannot break a command (the log is best effort; the error writer still gets every line). Thread safety: all
/// members are guarded by one lock; several processes may append to the same file (shared write access); every write goes to
/// the current end of the file, so lines of different processes follow each other whole, except when two writes start in the
/// same instant, which can lose the earlier line.
/// </summary>
internal sealed class CliLogFile : IDisposable
{
    #region Fields

    // Guards _Stream and _Disabled, and serializes rotation. File I/O under this lock is deliberate: the seek to the end and the
    // write must form one step, and a rotation must exclude every other writer of this process. Accepted cost: a slow disk
    // delays the other logging threads of the process; the lines are encoded before the lock is taken to keep it short.
    private readonly Lock _Lock = new();

    private readonly string _Path;

    private readonly long _MaxBytes;

    private readonly int _MaxFiles;

    // Injected clock, so tests can fix the time stamps.
    private readonly TimeProvider _Time;

    // The open append handle; null after Dispose, after a failed rotation, or once the file logging is switched off.
    private FileStream? _Stream;

    // Set by the first I/O error: from then on Append does nothing (one report, no flood of repeated errors).
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

    /// <summary>Disposes the file handle; later appends do nothing. Safe to call more than once.</summary>
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

    /// <summary>
    /// Receives the one message that reports a failed append (the command's error writer); may stay <see langword="null"/>. It
    /// is called after the log lock was released, so a sink that locks the error writer cannot deadlock with a thread that holds
    /// the writer and waits for the log.
    /// </summary>
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

        // Opening is the only place where the log reports a problem by return value: the caller turns it into one warning and
        // runs without a file. Only file-system failures are expected here; anything else is a bug and propagates.
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

        // Most texts are clean; the scan finds that out without allocating, so the common case returns the same instance. Every
        // plugin log line and every list cell passes through here.
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

        // Dirty case: one allocation (the result), written in place; the static lambda captures nothing. The length is kept, one
        // space per control character, so no character is dropped and no surrogate pair is split (a control character is a
        // single UTF-16 unit).
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
        // Build and encode the line before the lock: the critical section then holds only the seek and the write. The time stamp
        // is UTC in the round-trip format ("O").
        string line =_Time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + " " + level + " " + source + ": "
            + SingleLine(text) + Environment.NewLine;
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        // The problem text is produced inside the lock but delivered after it (see ProblemSink).
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
                // Rotation test: the line must still fit. "end > 0" keeps an empty file from rotating forever when a single line
                // is longer than the whole limit: such a line is written to the fresh file as it is.
                long end = stream.Seek(0, SeekOrigin.End);
                if (end > 0 && end + bytes.Length > _MaxBytes)
                {
                    stream = _Rotate();
                    stream.Seek(0, SeekOrigin.End);
                }

                // One write per line, so lines of several processes do not interleave inside a line. Flushed after every line,
                // so the line is in the file when the call returns.
                stream.Write(bytes);
                stream.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The log is switched off for the rest of the run and reported once; the command itself carries on.
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

    /// <summary>
    /// Opens the file for appending with shared read, write, and delete access: other commands of the same user may log to the
    /// same file at the same time, and a reader (or a rotation of another process) must not be locked out.
    /// </summary>
    /// <param name="path">The log file.</param>
    /// <returns>The open stream.</returns>
    private static FileStream _OpenStream(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);


    /// <summary>
    /// Moves <c>ni.log</c> to <c>ni.1.log</c> and shifts the older files up; the oldest beyond the kept number is deleted.
    /// The caller holds the lock. The handle is closed first and a new one is opened at the end, because an open handle
    /// would follow the moved file to its new name. When a step throws, <c>_Stream</c> stays <see langword="null"/> and the
    /// caller switches the file logging off. Two processes that rotate in the same instant are not coordinated (best effort,
    /// like the rest of the log).
    /// </summary>
    /// <returns>The stream of the fresh <c>ni.log</c>.</returns>
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
