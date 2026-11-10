// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Shared persistence I/O helpers for settings JSON files.
/// Opens files so other processes can still read them.
/// </summary>
internal static class SettingsFileAccess
{
    #region Buffer and temporary file constants

    /// <summary>
    /// The largest read buffer: a large file arrives in few system calls. Internal tuning (not a limit): it only chooses how
    /// the bytes are fetched; the size cap of the loader is <see cref="SettingsLimits.MaxFileSize"/>.
    /// </summary>
    private const int _MaxBufferBytes = 4 * 1024 * 1024;

    /// <summary>The smallest read buffer: the default of <see cref="FileStream"/>.</summary>
    private const int _MinBufferBytes = 4 * 1024;

    /// <summary>The length of the id in a temporary file name: a GUID as 32 hexadecimal digits without dashes.</summary>
    private const int _TempIdLength = 32;

    /// <summary>The ending of every temporary file.</summary>
    private const string _TempSuffix = ".tmp";

    #endregion

    #region Opening, temporary files, and replacing

    /// <summary>
    /// Opens <paramref name="path"/> for sequential read while allowing other processes
    /// to read the same file (<see cref="FileShare.Read"/>: others may read, but nobody may write to the file while this stream
    /// is open) and to replace or delete it (<see cref="FileShare.Delete"/>).
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The open stream; the caller disposes it.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist (also from the length lookup that sizes the buffer).</exception>
    internal static FileStream OpenSharedRead(string path)
    {
        // Perf: a settings file holds a few hundred bytes, so a fixed 4 MiB buffer would allocate a large-object-heap array per
        // open (a profile with 20 groups, the manifest, and every merge read pay it). The buffer fits the file, at most 4 MiB.
        // The length is a hint only: a file that grows after the lookup is still read completely, just in more calls.
        long length = new FileInfo(path).Length;
        int bufferSize = checked((int)Math.Clamp(length, _MinBufferBytes, _MaxBufferBytes));

        // FileShare.Delete matters on Windows: a save replaces a group file with a move over it, and Windows refuses that
        // (access denied) while any handle without delete sharing is open, for example a Load in another process. The reader
        // is not affected: the handle keeps reading the old content, and writers still cannot modify the file under it.
        // The settings and manifest files are parsed once, front to back, so the stream is opened with the sequential-scan hint
        // (the OS can read ahead and need not keep the pages cached for reuse).
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize,
            FileOptions.SequentialScan);
    }

    /// <summary>
    /// Builds a temporary path next to <paramref name="targetPath"/>: <c>&lt;target&gt;.&lt;32 hex digits&gt;.tmp</c>. The id is
    /// unique per call, so the path never names an existing file and a write never overwrites a file of the user.
    /// </summary>
    /// <param name="targetPath">The file that will be replaced.</param>
    /// <returns>The temporary path, in the folder of the target (so the replace is a rename within one volume).</returns>
    /// <remarks>Sync: <see cref="IsTempFileName"/> recognizes exactly this form; the cleanup of leftovers relies on it.</remarks>
    internal static string NewTempPath(string targetPath) =>
        targetPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + _TempSuffix;

    /// <summary>Tells whether a file name is a temporary file of <see cref="NewTempPath"/> for a group file.</summary>
    /// <param name="fileName">The file name without folder.</param>
    /// <returns><see langword="true"/> for <c>&lt;stem&gt;.json.&lt;32 lower-case hex digits&gt;.tmp</c>.</returns>
    /// <remarks>
    /// Security: the save deletes leftovers of interrupted saves, and a user's own <c>notes.json.tmp</c> in the folder must
    /// survive. A foreign file would have to carry a 32-digit id in exactly this form to be taken for a leftover. Sync: the
    /// form is the one <see cref="NewTempPath"/> writes.
    /// </remarks>
    internal static bool IsTempFileName(string fileName)
    {
        if (!fileName.EndsWith(_TempSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> body = fileName.AsSpan(0, fileName.Length - _TempSuffix.Length);
        if (body.Length <= _TempIdLength + 1 || body[^(_TempIdLength + 1)] != '.')
        {
            return false;
        }

        foreach (char c in body[^_TempIdLength..])
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return body[..^(_TempIdLength + 1)].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces <paramref name="targetPath"/> with the complete temporary file <paramref name="tempPath"/> in one step, or
    /// moves the temporary file into place when the target does not exist yet. A reader sees the old or the new content,
    /// never half of one.
    /// </summary>
    /// <param name="tempPath">The temporary file, in the same folder as the target.</param>
    /// <param name="targetPath">The file to replace or create.</param>
    internal static void ReplaceFile(string tempPath, string targetPath)
    {
        // File.Replace and not File.Move(overwrite: true): on Windows a move over a file that a reader has open fails with
        // access denied even when the reader allows deleting (see OpenSharedRead), while File.Replace succeeds and leaves the
        // reader on the old content. File.Replace needs an existing target, so a missing one is created by a plain move.
        try
        {
            File.Replace(tempPath, targetPath, destinationBackupFileName: null);
        }
        catch (FileNotFoundException) when (!File.Exists(targetPath) && File.Exists(tempPath))
        {
            File.Move(tempPath, targetPath);
        }
    }

    /// <summary>
    /// Returns a display label that does not echo a fully resolved filesystem path
    /// (avoids leaking sensitive locations into UI/log strings).
    /// </summary>
    /// <param name="path">A file path; may be empty or end in a separator.</param>
    /// <returns>The file name without folder, or <c>configuration file</c> when the path has none.</returns>
    internal static string SafeFileLabel(string path)
    {
        string fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName))
        {
            return fileName;
        }

        return "configuration file";
    }

    #endregion
}

/// <summary>
/// Read-only, forward-only stream that delivers at most <c>maxBytes + 1</c> bytes of an inner stream. The extra byte
/// proves that the input is larger than the cap; <see cref="Exceeded"/> reports it. Settings and configuration loads
/// wrap their file stream in it so the size cap holds for the bytes actually read, not only for the length the file
/// system reported before the read (the file can grow in between, and a non-regular file reports no length).
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> not thread-safe; one reader per instance, like the wrapped <see cref="FileStream"/>.</para>
/// <para>Does not own the inner stream: disposing the wrapper leaves the inner stream open.</para>
/// </remarks>
internal sealed class BoundedReadStream : Stream
{
    #region Bounded stream state

    /// <summary>The wrapped stream; not owned.</summary>
    private readonly Stream _Inner;

    /// <summary>The cap in bytes; one byte more than this is delivered at most.</summary>
    private readonly long _MaxBytes;

    /// <summary>The bytes delivered so far; not thread-safe, like the stream.</summary>
    private long _Total;

    #endregion

    #region Bounded stream construction

    /// <summary>Wraps <paramref name="inner"/> with a cap of <paramref name="maxBytes"/> bytes.</summary>
    /// <param name="inner">Readable stream; not disposed by the wrapper.</param>
    /// <param name="maxBytes">Largest accepted size in bytes; must be positive. A cap that is off passes <see cref="long.MaxValue"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    internal BoundedReadStream(Stream inner, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _Inner = inner;
        _MaxBytes = maxBytes;
    }

    #endregion

    #region Overrun report and stream capabilities

    /// <summary>True once more than the cap was delivered; the reader then saw a truncated input.</summary>
    internal bool Exceeded => _Total > _MaxBytes;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <summary>Not supported: the wrapper is forward-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override long Length => throw new NotSupportedException();

    /// <summary>Bytes delivered so far; setting is not supported.</summary>
    /// <exception cref="NotSupportedException">On set.</exception>
    public override long Position
    {
        get => _Total;
        set => throw new NotSupportedException();
    }

    #endregion

    #region Stream API

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        if (_Total > _MaxBytes)
        {
            return 0;
        }

        // Allow exactly one byte past the cap so the overrun is observable: a reader that gets maxBytes + 1 bytes knows the
        // input is larger than the cap, and once that extra byte is delivered every later read returns end of stream (the
        // guard above), so the parser sees a truncated document and the caller checks Exceeded. remaining >= 0 here, and the +1
        // is applied only when remaining is below the buffer length (an int), so nothing overflows even for long.MaxValue and
        // toRead never exceeds buffer.Length.
        long remaining = _MaxBytes - _Total;
        int toRead = remaining >= buffer.Length
            ? buffer.Length
            : (int)remaining + 1;
        int read = _Inner.Read(buffer[..toRead]);
        _Total += read;
        return read;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1
            ? one[0]
            : -1;
    }

    /// <summary>No-op: the wrapper never writes.</summary>
    public override void Flush()
    {
    }

    /// <summary>Not supported: the wrapper is forward-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <param name="offset">Ignored.</param>
    /// <param name="origin">Ignored.</param>
    /// <returns>Never returns.</returns>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Not supported: the wrapper is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <param name="value">Ignored.</param>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported: the wrapper is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <param name="buffer">Ignored.</param>
    /// <param name="offset">Ignored.</param>
    /// <param name="count">Ignored.</param>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion
}

/// <summary>
/// Tells, after a parse failed, whether the nesting of a file exceeds a depth. The JSON parser reports a too deep file with a
/// localized text that carries neither the cause nor the limit, so the failure path counts the nesting itself and the caller
/// can name <see cref="SettingsLimits.MaxJsonDepth"/>. Cold path; stateless and thread-safe.
/// </summary>
/// <remarks>
/// Perf: one pooled buffer and a forward scan, so the probe is linear and needs no memory in proportion to the file. It runs only
/// after a parse failed, never for a file that loaded.
/// </remarks>
internal static class JsonDepthProbe
{
    #region Depth scan

    /// <summary>The size of the pooled read buffer in bytes. Internal tuning, not a limit.</summary>
    private const int _BufferBytes = 4096;

    /// <summary>Scans a file for the deepest nesting of objects and arrays, outside strings.</summary>
    /// <param name="path">The file to read; it was readable a moment ago.</param>
    /// <param name="maxBytes">The most bytes to read (the ceiling of <see cref="SettingsLimits.MaxFileSize"/>).</param>
    /// <param name="maxDepth">The depth limit; <see cref="LimitValue.Off"/> never reports a hit.</param>
    /// <returns><see langword="true"/> when the nesting is deeper than <paramref name="maxDepth"/>.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be accessed.</exception>
    internal static bool FileExceeds(string path, long maxBytes, LimitValue maxDepth)
    {
        if (maxDepth.IsOff)
        {
            return false;
        }

        using FileStream file = SettingsFileAccess.OpenSharedRead(path);
        using BoundedReadStream stream = new(file, maxBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_BufferBytes);
        try
        {
            // Strings may hold brackets, and a quote may be escaped: the scan keeps both states across buffer boundaries.
            long depth = 0;
            bool inString = false;
            bool escaped = false;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                foreach (byte current in buffer.AsSpan(0, read))
                {
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (current == (byte)'\\')
                        {
                            escaped = true;
                        }
                        else if (current == (byte)'"')
                        {
                            inString = false;
                        }
                    }
                    else if (current == (byte)'"')
                    {
                        inString = true;
                    }
                    else if (current is (byte)'{' or (byte)'[')
                    {
                        depth++;
                        if (depth > maxDepth.Value)
                        {
                            return true;
                        }
                    }
                    else if (current is (byte)'}' or (byte)']')
                    {
                        depth--;
                    }
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion
}
