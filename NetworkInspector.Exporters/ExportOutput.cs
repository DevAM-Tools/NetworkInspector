// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Abstraction for export output targets (file, stream, stdout).
/// File outputs use a 4 MiB buffer for efficient large-block I/O.
/// <para>
/// Ownership: a file output and a stdout output own their stream and close it on <see cref="Dispose()"/>; an output wrapping
/// a caller-supplied stream never disposes that stream. Exporters create the output through the builder, hand it
/// to their writer through <see cref="GetOrCreateUnderlyingStream"/>, and dispose it exactly once on finish.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. An output belongs to one exporter, which uses it from one thread at a time.
/// </para>
/// </summary>
public abstract class ExportOutput : IDisposable
{
    /// <summary>
    /// Default buffer size for file output (4 MiB). Large blocks keep the number of write system calls low for big
    /// exports; the buffer is allocated once per file output, on the first write.
    /// </summary>
    private const int _DefaultFileBufferSize = 4 * 1024 * 1024;

    /// <summary>Creates a file-backed output with a 4 MiB write buffer.
    /// The file is not created until the first write; if no data is ever written,
    /// no file is created on disk.
    /// </summary>
    /// <param name="path">Absolute or relative path to the output file. Must not be null or empty.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the specified file.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
    public static ExportOutput File(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // Validate path structure early (GetFullPath normalises separators and throws on
        // illegal characters, but does not touch the filesystem). This surfaces bad paths
        // at construction time rather than at the first Write call.
        string _ = Path.GetFullPath(path);
        return new LazyFileExportOutput(path, _DefaultFileBufferSize);
    }

    /// <summary>Creates an output that writes to an existing stream.</summary>
    /// <param name="stream">The target stream. Caller retains ownership.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the given stream.</returns>
    public static ExportOutput FromStream(Stream stream) =>
        new StreamExportOutput(stream, ownsStream: false);

    /// <summary>Creates an output that writes to stdout.</summary>
    /// <returns>A new <see cref="ExportOutput"/> writing to the standard output stream.</returns>
    public static ExportOutput Stdout() =>
        new StreamExportOutput(Console.OpenStandardOutput(), ownsStream: true);

    /// <summary>Writes bytes to the output.</summary>
    /// <param name="data">The data to write.</param>
    /// <exception cref="IOException">The underlying stream could not be written (or, for a file, created).</exception>
    public abstract void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Flushes any buffered data to the output. For a file output that was never written, nothing is flushed and no file
    /// is created.
    /// </summary>
    public abstract void Flush();

    /// <summary>
    /// Total bytes accepted via <see cref="Write"/> so far (includes data still in write buffers).
    /// Does not include bytes written through <see cref="GetOrCreateUnderlyingStream"/> bypassing
    /// <see cref="Write"/>.
    /// </summary>
    public abstract long AcceptedBytes { get; protected set; }

    /// <summary>Gets the underlying stream, creating it if needed.</summary>
    /// <remarks>
    /// This method replaces the former <c>UnderlyingStream</c> property. The name
    /// makes the side-effect explicit — on a <see cref="LazyFileExportOutput"/>, the first
    /// call materialises a new file on disk (create/truncate). Callers that only need to
    /// query whether the stream already exists must call
    /// <see cref="TryGetExistingStream"/> instead.
    /// </remarks>
    internal abstract Stream? GetOrCreateUnderlyingStream();

    /// <summary>
    /// Same as <see cref="GetOrCreateUnderlyingStream"/>, but reports an open failure (missing directory, access denied, a
    /// path the platform rejects) and a missing stream as a result instead of an exception. Exporters call this on their first
    /// item, so a file that cannot be created ends up in the normal error path (counter and event) and never escapes
    /// <c>OnFrame</c> or <c>OnPacket</c>.
    /// </summary>
    /// <param name="stream">The stream when the result is <see langword="true"/>; otherwise <see langword="null"/>.</param>
    /// <param name="failure">Reason text when the result is <see langword="false"/>; otherwise empty.</param>
    /// <returns><see langword="true"/> when a stream is available.</returns>
    internal bool TryGetOrCreateUnderlyingStream([NotNullWhen(true)] out Stream? stream, out string failure)
    {
        try
        {
            stream = GetOrCreateUnderlyingStream();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException)
        {
            // Only the failures a file open can produce are expected here; anything else is a bug and still propagates.
            stream = null;
            failure = ex.Message;
            return false;
        }

        if (stream is null)
        {
            failure = "the output has no stream to write to.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns the underlying stream if it has already been materialised, or <c>null</c>
    /// if the stream has not yet been created (i.e. no data has been written yet).
    /// Unlike <see cref="GetOrCreateUnderlyingStream"/>, this method never creates a file
    /// or allocates a stream as a side-effect.
    /// </summary>
    internal abstract Stream? TryGetExistingStream();

    /// <summary>Releases resources used by this output.</summary>
    /// <param name="disposing">True if called from Dispose, false if from finalizer.</param>
    protected abstract void Dispose(bool disposing);

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Lazy file-backed <see cref="ExportOutput"/> implementation.
/// Opens (creates) the file only on the first write so that exporters that never
/// write any data leave no file on disk.
/// </summary>
internal sealed class LazyFileExportOutput : ExportOutput
{
    /// <summary>Target file path, as given by the caller (validated for illegal characters by <see cref="ExportOutput.File"/>).</summary>
    private readonly string _Path;

    /// <summary>Size of the <see cref="BufferedStream"/> wrapped around the file stream.</summary>
    private readonly int _BufferSize;

    /// <summary>The buffered file stream; <c>null</c> until the first write or <see cref="GetOrCreateUnderlyingStream"/> call.</summary>
    private Stream? _Stream;

    /// <inheritdoc/>
    public override long AcceptedBytes { get; protected set; }

    /// <summary>Creates a lazy file output.</summary>
    /// <param name="path">Target file path.</param>
    /// <param name="bufferSize">Write buffer size in bytes.</param>
    internal LazyFileExportOutput(string path, int bufferSize)
    {
        _Path = path;
        _BufferSize = bufferSize;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        // Lazy create: the file appears only with the first byte. FileMode.Create truncates an existing file, and
        // FileShare.None gives the exporter exclusive access so no other process reads or writes a half-written export.
        // The 4 MiB BufferedStream turns many small writer calls into few large file writes.
        _Stream ??= new BufferedStream(
            new FileStream(_Path, FileMode.Create, FileAccess.Write, FileShare.None),
            _BufferSize);
        _Stream.Write(data);
        AcceptedBytes += data.Length;
    }

    /// <inheritdoc/>
    public override void Flush() => _Stream?.Flush();

    /// <inheritdoc/>
    /// <remarks>
    /// Materialises the underlying <see cref="FileStream"/> on first call (same open mode and buffering as
    /// <see cref="Write"/>). The returned stream is the <see cref="BufferedStream"/>, so a writer that writes to it directly
    /// also benefits from the buffer; those bytes are not counted in <see cref="AcceptedBytes"/>.
    /// </remarks>
    internal override Stream? GetOrCreateUnderlyingStream()
    {
        _Stream ??= new BufferedStream(
            new FileStream(_Path, FileMode.Create, FileAccess.Write, FileShare.None),
            _BufferSize);
        return _Stream;
    }

    /// <inheritdoc/>
    internal override Stream? TryGetExistingStream() => _Stream;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Disposing the BufferedStream flushes its pending bytes and closes the file. Nothing happens when the file was
        // never created, so an empty export leaves no file.
        if (disposing)
        {
            _Stream?.Dispose();
            _Stream = null;
        }
    }
}

/// <summary>Stream-backed <see cref="ExportOutput"/> implementation.</summary>
internal sealed class StreamExportOutput : ExportOutput
{
    /// <summary>The target stream.</summary>
    private readonly Stream _Stream;

    /// <summary>Whether <see cref="Dispose(bool)"/> disposes the stream (stdout) or leaves it to the caller.</summary>
    private readonly bool _OwnsStream;

    /// <inheritdoc/>
    public override long AcceptedBytes { get; protected set; }

    /// <summary>Creates a stream-backed output.</summary>
    /// <param name="stream">Target stream.</param>
    /// <param name="ownsStream">If true, the stream is disposed when this output is disposed.</param>
    internal StreamExportOutput(Stream stream, bool ownsStream)
    {
        _Stream = stream;
        _OwnsStream = ownsStream;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        _Stream.Write(data);
        AcceptedBytes += data.Length;
    }

    /// <inheritdoc/>
    public override void Flush() => _Stream.Flush();

    /// <inheritdoc/>
    internal override Stream? GetOrCreateUnderlyingStream() => _Stream;

    /// <inheritdoc/>
    internal override Stream? TryGetExistingStream() => _Stream;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _OwnsStream)
        {
            _Stream.Dispose();
        }
    }
}
