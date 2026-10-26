// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Shared persistence I/O helpers for settings JSON files.
/// Opens files so other processes can still read them.
/// </summary>
internal static class SettingsFileAccess
{
    #region Constants

    /// <summary>
    /// Manifest of group JSON file names written by <see cref="SettingsManager.Save"/> and <see cref="SettingsManager.SaveChanged"/>:
    /// the group files that settings managers wrote and own, so stale cleanup never deletes unrelated JSON in the folder.
    /// Not a <c>.json</c> file so <see cref="SettingsManager.Load"/> (which enumerates <c>*.json</c>) will not treat it as a group.
    /// </summary>
    internal const string OwnedGroupManifestFileName = ".ni-settings-owned";

    #endregion

    #region Public API

    /// <summary>
    /// Opens <paramref name="path"/> for sequential read while allowing other processes
    /// to read the same file (<see cref="FileShare.Read"/>: others may read, but nobody may write to the file while this stream
    /// is open) and to replace or delete it (<see cref="FileShare.Delete"/>).
    /// </summary>
    internal static FileStream OpenSharedRead(string path)
    {
        // FileShare.Delete matters on Windows: a save replaces a group file with a move over it, and Windows refuses that
        // (access denied) while any handle without delete sharing is open, for example a Load in another process. The reader
        // is not affected: the handle keeps reading the old content, and writers still cannot modify the file under it.
        // The settings and manifest files are parsed once, front to back, so the stream is opened with the sequential-scan hint
        // (the OS can read ahead and need not keep the pages cached for reuse). The 4 MiB buffer makes a large file arrive in few
        // system calls instead of many small reads of the default 4 KiB buffer.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4 * 1024 * 1024,
            FileOptions.SequentialScan);
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
    #region Fields

    private readonly Stream _Inner;
    private readonly long _MaxBytes;
    private long _Total;

    #endregion

    #region Lifecycle

    /// <summary>Wraps <paramref name="inner"/> with a cap of <paramref name="maxBytes"/> bytes.</summary>
    /// <param name="inner">Readable stream; not disposed by the wrapper.</param>
    /// <param name="maxBytes">Largest accepted size in bytes; must be positive (a disabled cap needs no wrapper).</param>
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

    #region Properties

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
        int toRead = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
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
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>Not supported: the wrapper is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Not supported: the wrapper is read-only.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    #endregion
}
