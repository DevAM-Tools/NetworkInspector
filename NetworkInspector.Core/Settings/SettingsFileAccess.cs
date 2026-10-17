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
    /// Manifest of group JSON file names last written by <see cref="SettingsManager.Save"/>.
    /// Not a <c>.json</c> file so <see cref="SettingsManager.Load"/> will not treat it as a group.
    /// </summary>
    internal const string OwnedGroupManifestFileName = ".ni-settings-owned";

    #endregion

    #region Public API

    /// <summary>
    /// Opens <paramref name="path"/> for sequential read while allowing other processes
    /// to read the same file (<see cref="FileShare.Read"/>).
    /// </summary>
    internal static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4 * 1024 * 1024,
            FileOptions.SequentialScan);
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

        // Allow exactly one byte past the cap so the overrun is observable. remaining >= 0 here, and the +1 is applied
        // only when remaining is below the buffer length (an int), so nothing overflows even for long.MaxValue.
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
